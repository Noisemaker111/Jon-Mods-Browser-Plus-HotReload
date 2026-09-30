using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace HotReloadTool
{
    public static class FriendSync
    {
        public static string Status = "Friends: host mods sync automatically on join";
        public static string GameVersion { get { return Constants.cVersionMajor + "." + Constants.cVersionMinor + "." + Constants.cVersionBuild; } }
        static string session;
        static string archive;
        static string expectedHash;
        static long expectedLength;
        static FileStream receiving;
        static Timer timeout;
        static bool connected;
        static bool stopped;
        static bool comparing;
        static JObject deferredOffer;
        static string pendingInstall, pendingHash;
        static Action resumeJoin;
        public static bool Installing;
        static readonly object gate = new object();
        class Offer { public string session, path, hash; public long length; public JObject manifest; }
        static readonly Dictionary<ClientInfo, JObject> subscribers = new Dictionary<ClientInfo, JObject>();
        static readonly HashSet<ClientInfo> preparing = new HashSet<ClientInfo>();
        static readonly HashSet<ClientInfo> changedWhilePreparing = new HashSet<ClientInfo>();
        static readonly Dictionary<ClientInfo, Offer> offers = new Dictionary<ClientInfo, Offer>();

        public static void Init()
        {
            JonSyncTransport.Receive = Receive;
            JonSyncTransport.Connected = Connect;
            JonSyncTransport.BeforeWorldJoin = AwaitJoin;
            JonSyncTransport.Disconnected = Reset;
            JonSyncTransport.WorldCleaned = InstallAfterCleanup;
            JonSyncTransport.ClientLeft = DropClient;
            JonSyncTransport.Start();
        }
        public static void Shutdown()
        {
            stopped = true;
            bool waiting;
            lock (gate) waiting = resumeJoin != null;
            JonSyncTransport.Receive = null;
            JonSyncTransport.Connected = null;
            JonSyncTransport.BeforeWorldJoin = null;
            JonSyncTransport.Disconnected = null;
            JonSyncTransport.WorldCleaned = null;
            JonSyncTransport.ClientLeft = null;
            Reset();
            if (waiting) GameManager.Instance.Disconnect();
        }
        public static void Reset()
        {
            lock (gate)
            {
                connected = false;
                resumeJoin = null;
                comparing = false;
                deferredOffer = null;
                session = null;
                if (receiving != null) { receiving.Dispose(); receiving = null; }
                if (!Installing) TryDelete(archive);
                if (timeout != null) { timeout.Dispose(); timeout = null; }
                foreach (var offer in offers.Values) TryDelete(offer.path);
                offers.Clear();
                subscribers.Clear();
                preparing.Clear();
                changedWhilePreparing.Clear();
            }
        }
        public static void Connect()
        {
            if (ConnectionManager.Instance == null || !ConnectionManager.Instance.IsClient || ConnectionManager.Instance.IsServer) return;
            lock (gate)
            {
                if (connected || stopped || Installing || HotReloadCore.RestartRequired) return;
                connected = true;
                session = Guid.NewGuid().ToString("N");
                Status = "Friends: checking host mods...";
                try { Send(new JObject { ["kind"] = "hello", ["session"] = session, ["game"] = GameVersion, ["protocol"] = 1 }, null); }
                catch (Exception e) { Fail("Host does not support this sync release: " + e.Message); return; }
                ArmTimeout();
            }
        }
        static bool AwaitJoin(Action resume)
        {
            lock (gate)
            {
                if (stopped) return false;
                if (Installing || HotReloadCore.RestartRequired)
                {
                    Status = "Friends: restart the game before joining with the installed pack";
                    GameManager.Instance.Disconnect();
                    return true;
                }
                resumeJoin = resume;
                Connect();
                return true;
            }
        }
        static void ArmTimeout()
        {
            if (timeout != null) timeout.Dispose();
            // One deadline per response; progress resets it. No polling.
            Timer deadline = null;
            deadline = new Timer(_ =>
            {
                lock (gate) if (ReferenceEquals(timeout, deadline)) Fail("Host mod sync timed out; rejoin to retry");
            }, null, 300000, Timeout.Infinite);
            timeout = deadline;
        }
        static void Fail(string message)
        {
            bool waiting;
            lock (gate)
            {
                waiting = resumeJoin != null;
                resumeJoin = null;
                Status = "Friends: " + message;
                connected = false;
                comparing = false;
                session = null;
                if (receiving != null) { receiving.Dispose(); receiving = null; }
                if (timeout != null) { timeout.Dispose(); timeout = null; }
            }
            Log.Warning("[HotReload] " + Status);
            if (waiting) HotReloadCore.RunOnMainThread(() => GameManager.Instance.Disconnect());
        }
        static void Send(JObject message, ClientInfo client) { JonSyncTransport.Send(Encoding.UTF8.GetBytes(message.ToString(Newtonsoft.Json.Formatting.None)), client); }
        public static void Receive(byte[] bytes, ClientInfo sender)
        {
            try
            {
                var message = JObject.Parse(Encoding.UTF8.GetString(bytes));
                var connection = ConnectionManager.Instance;
                if (connection == null) return;
                var kind = (string)message["kind"];
                if (connection.IsServer)
                {
                    if (sender == null || !sender.loginDone || sender.disconnecting) return;
                    if (kind == "hello") Prepare(sender, message);
                    else if (kind == "get") SendChunk(sender, message);
                    else if (kind == "received") FinishOffer(sender, message);
                    return;
                }
                if (!connection.IsClient || sender != null || (string)message["session"] != session) return;
                if (kind == "offer") AcceptOffer(message);
                else if (kind == "chunk") AcceptChunk(message);
                else if (kind == "error") Fail((string)message["error"]);
            }
            catch (Exception e) { Fail(e.Message); }
        }
        static void Prepare(ClientInfo client, JObject hello)
        {
            var id = (string)hello["session"];
            if (string.IsNullOrWhiteSpace(id) || id.Length != 32 || (int?)hello["protocol"] != 1) return;
            if ((string)hello["game"] != GameVersion)
            {
                Send(new JObject { ["kind"] = "error", ["session"] = id, ["error"] = "Both players need game " + GameVersion }, client);
                return;
            }
            lock (gate)
            {
                subscribers[client] = hello;
                if (!preparing.Add(client)) { changedWhilePreparing.Add(client); return; }
            }
            var roots = ModScanner.Roots();
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    var path = Path.Combine(ModBrowser.CatalogDir, "sync", Guid.NewGuid().ToString("N") + ".hrpack");
                    var manifest = ModFiles.Export(roots, path, "Host mods", GameVersion);
                    var offer = new Offer { session = id, path = path, hash = ModFiles.HashFile(path), length = new FileInfo(path).Length, manifest = manifest };
                    HotReloadCore.RunOnMainThread(() =>
                    {
                        if (stopped || ConnectionManager.Instance == null || !ConnectionManager.Instance.IsServer) { TryDelete(path); return; }
                        JObject retry = null;
                        lock (gate)
                        {
                            JObject current;
                            preparing.Remove(client);
                            if (!subscribers.TryGetValue(client, out current)) { changedWhilePreparing.Remove(client); TryDelete(path); return; }
                            if (changedWhilePreparing.Remove(client) || (string)current["session"] != id) retry = current;
                            else
                            {
                                Offer previous;
                                if (offers.TryGetValue(client, out previous)) TryDelete(previous.path);
                                offers[client] = offer;
                            }
                        }
                        if (retry != null) { TryDelete(path); Prepare(client, retry); return; }
                        Send(new JObject { ["kind"] = "offer", ["session"] = id, ["hash"] = offer.hash, ["length"] = offer.length,
                            ["game"] = GameVersion, ["fingerprint"] = ModFiles.Fingerprint(manifest) }, client);
                    });
                }
                catch (Exception e)
                {
                    HotReloadCore.RunOnMainThread(() =>
                    {
                        JObject retry = null;
                        lock (gate)
                        {
                            preparing.Remove(client);
                            if (changedWhilePreparing.Remove(client)) subscribers.TryGetValue(client, out retry);
                            if (!subscribers.ContainsKey(client) || stopped) return;
                        }
                        if (retry != null) Prepare(client, retry);
                        else Send(new JObject { ["kind"] = "error", ["session"] = id, ["error"] = e.Message }, client);
                    });
                }
            });
        }
        // Invoked once by mod file-change events, including browser installs.
        public static void HostModsChanged()
        {
            if (Installing || stopped || ConnectionManager.Instance == null || !ConnectionManager.Instance.IsServer) return;
            KeyValuePair<ClientInfo, JObject>[] clients;
            lock (gate) { clients = new List<KeyValuePair<ClientInfo, JObject>>(subscribers).ToArray(); }
            foreach (var client in clients) Prepare(client.Key, client.Value);
        }
        static void SendChunk(ClientInfo client, JObject request)
        {
            Offer offer;
            lock (gate) if (!offers.TryGetValue(client, out offer)) return;
            if ((string)request["session"] != offer.session || (string)request["hash"] != offer.hash) return;
            var offset = (long)request["offset"];
            if (offset < 0 || offset >= offer.length) return;
            var buffer = new byte[(int)Math.Min(24576, offer.length - offset)];
            using (var file = File.OpenRead(offer.path))
            {
                file.Position = offset;
                int count = file.Read(buffer, 0, buffer.Length);
                if (count != buffer.Length) throw new EndOfStreamException();
            }
            Send(new JObject { ["kind"] = "chunk", ["session"] = offer.session, ["hash"] = offer.hash, ["offset"] = offset, ["data"] = Convert.ToBase64String(buffer) }, client);
        }
        static void FinishOffer(ClientInfo client, JObject message)
        {
            lock (gate)
            {
                Offer offer;
                if (offers.TryGetValue(client, out offer) && (string)message["session"] == offer.session && (string)message["hash"] == offer.hash)
                { offers.Remove(client); TryDelete(offer.path); }
            }
        }
        static void DropClient(ClientInfo client)
        {
            if (client == null) return;
            lock (gate)
            {
                subscribers.Remove(client); preparing.Remove(client); changedWhilePreparing.Remove(client);
                Offer offer;
                if (offers.TryGetValue(client, out offer)) { offers.Remove(client); TryDelete(offer.path); }
            }
        }
        static void AcceptOffer(JObject message)
        {
            lock (gate)
            {
                if (Installing) return;
                if (comparing) { deferredOffer = message; return; }
                if (receiving != null) { receiving.Dispose(); receiving = null; }
                comparing = true;
                if (timeout != null) { timeout.Dispose(); timeout = null; }
                if ((string)message["game"] != GameVersion) throw new InvalidDataException("Game version mismatch");
                var fingerprint = (string)message["fingerprint"];
                var roots = ModScanner.Roots();
                // Hashing assets can take time; never freeze the game/UI thread.
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try
                    {
                        var matches = ModFiles.Fingerprint(ModFiles.Inventory(roots, "Local mods", GameVersion)) == fingerprint;
                        HotReloadCore.RunOnMainThread(() => StartReceiving(message, matches));
                    }
                    catch (Exception e) { lock (gate) if ((string)message["session"] == session) Fail(e.Message); }
                });
            }
        }
        static void StartReceiving(JObject message, bool matches)
        {
            lock (gate)
            {
                if ((string)message["session"] != session || stopped || Installing) return;
                comparing = false;
                if (deferredOffer != null) { var latest = deferredOffer; deferredOffer = null; AcceptOffer(latest); return; }
                if (receiving != null) return;
                if (matches)
                {
                    Status = "Friends: mods match the host";
                    Send(new JObject { ["kind"] = "received", ["session"] = session, ["hash"] = message["hash"] }, null);
                    Log.Out("[HotReload] " + Status);
                    var resume = resumeJoin; resumeJoin = null;
                    resume?.Invoke();
                    return;
                }
                expectedHash = (string)message["hash"];
                expectedLength = (long)message["length"];
                if (expectedLength <= 0 || expectedHash == null || expectedHash.Length != 64) throw new InvalidDataException("Invalid host offer");
                var dir = Path.Combine(ModBrowser.CatalogDir, "sync");
                Directory.CreateDirectory(dir);
                archive = Path.Combine(dir, session + ".hrpack");
                receiving = File.Create(archive);
                RequestNext();
            }
        }
        static void RequestNext()
        {
            Status = "Friends: downloading host mods " + receiving.Position + " / " + expectedLength + " bytes";
            Send(new JObject { ["kind"] = "get", ["session"] = session, ["hash"] = expectedHash, ["offset"] = receiving.Position }, null);
            ArmTimeout();
        }
        static void AcceptChunk(JObject message)
        {
            lock (gate)
            {
                if (receiving == null || (string)message["hash"] != expectedHash) return;
                var data = Convert.FromBase64String((string)message["data"]);
                if ((long)message["offset"] != receiving.Position || data.Length == 0 || receiving.Position + data.Length > expectedLength)
                    throw new InvalidDataException("Out-of-order mod transfer");
                receiving.Write(data, 0, data.Length);
                if (receiving.Position < expectedLength) { RequestNext(); return; }
                receiving.Dispose(); receiving = null;
                Send(new JObject { ["kind"] = "received", ["session"] = session, ["hash"] = expectedHash }, null);
                if (timeout != null) { timeout.Dispose(); timeout = null; }
                pendingInstall = archive;
                pendingHash = expectedHash;
                Status = "Friends: verifying host mods...";
                // Stop the current world before replacing mod content. The next game
                // startup loads DLL/assets through the normal game loader.
                Installing = true;
                HotReloadCore.RestartRequired = true;
                HotReloadCore.SetWatch(false);
                bool noWorld = GameManager.Instance.World == null;
                GameManager.Instance.Disconnect();
                if (noWorld) InstallAfterCleanup();
            }
        }
        static void TryDelete(string path) { try { if (!string.IsNullOrEmpty(path)) File.Delete(path); } catch { } }
        static void InstallAfterCleanup()
        {
            string path, hash;
            lock (gate)
            {
                if (pendingInstall == null || stopped) return;
                path = pendingInstall; hash = pendingHash; pendingInstall = null; pendingHash = null;
            }
                var roots = ModScanner.Roots();
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try
                    {
                        if (ModFiles.HashFile(path) != hash) throw new InvalidDataException("Host pack checksum failed");
                        ModFiles.Install(path, roots, Path.Combine(ModBrowser.CatalogDir, "backups"), GameVersion, true);
                        Status = "Friends: host mods installed. Restart the game, then rejoin. Previous mods are backed up.";
                        Log.Out("[HotReload] " + Status);
                    }
                    catch (Exception e) { Fail(e.Message); }
                    finally { Installing = false; TryDelete(path); BrowserUi.InvalidateInstalled(); }
                });
        }
        public static string ExportPack(string name)
        {
            var path = Path.Combine(ModBrowser.CatalogDir, "packs", ModBrowser.SafeName(name) + ".hrpack");
            var roots = ModScanner.Roots();
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { var pack = ModFiles.Export(roots, path, name, GameVersion); Status = ModPack.LastResult = "Pack saved: " + path + " (" + ((JArray)pack["mods"]).Count + " mods)"; }
                catch (Exception e) { Status = ModPack.LastResult = "Pack export failed: " + e.Message; }
                finally { ModPack.InvalidateSaved(); }
            });
            return "Saving portable pack to " + path;
        }
        public static string ImportPack(string path)
        {
            if (GameManager.Instance != null && GameManager.Instance.World != null) return "Leave the world before importing a pack";
            if (Installing) return "A pack is already installing";
            Installing = true;
            bool previousRestart = HotReloadCore.RestartRequired, previousWatch = HotReloadCore.WatchEnabled;
            HotReloadCore.RestartRequired = true;
            HotReloadCore.SetWatch(false);
            var roots = ModScanner.Roots();
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { ModFiles.Install(path, roots, Path.Combine(ModBrowser.CatalogDir, "backups"), GameVersion, false); Status = ModPack.LastResult = "Pack installed. Restart the game to load all content."; }
                catch (Exception e)
                {
                    Status = ModPack.LastResult = "Pack import failed: " + e.Message;
                    HotReloadCore.RunOnMainThread(() => { HotReloadCore.RestartRequired = previousRestart; HotReloadCore.SetWatch(previousWatch); });
                }
                finally { Installing = false; BrowserUi.InvalidateInstalled(); }
            });
            return "Importing " + path;
        }
    }
}
