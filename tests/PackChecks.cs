using System;
using System.IO;
using System.IO.Compression;
using System.Threading;
using Newtonsoft.Json.Linq;
using HotReloadTool;

class PackChecks
{
    static void Check(bool condition, string message) { if (!condition) throw new Exception(message); Console.WriteLine("PASS " + message); }
    static void Reject(Action operation, string message) { bool failed = false; try { operation(); } catch { failed = true; } Check(failed, message); }
    static void Mod(string root, string folder, string name, string content)
    {
        var dir = Path.Combine(root, folder);
        Directory.CreateDirectory(Path.Combine(dir, "Config"));
        File.WriteAllText(Path.Combine(dir, "ModInfo.xml"), "<xml><Name value=\"" + name + "\"/><Version value=\"1.0\"/></xml>");
        File.WriteAllText(Path.Combine(dir, "Config", "items.xml"), content);
    }
    static int Main(string[] args)
    {
        try
        {
            string work = args[0], host = Path.Combine(work, "host"), client = Path.Combine(work, "client"), backups = Path.Combine(work, "backups");
            Directory.CreateDirectory(host); Directory.CreateDirectory(client);
            Mod(host, "Shared", "SharedMod", "<configs><append xpath=\"/items\"/></configs>");
            Mod(host, "HotReloadTool", "HotReloadTool", "private browser settings");
            Mod(client, "Shared", "SharedMod", "previous version");
            Mod(client, "Extra", "ExtraMod", "extra");
            Mod(client, "HotReloadTool", "HotReloadTool", "preserve me");
            string archive = Path.Combine(work, "friends.hrpack");
            var manifest = ModFiles.Export(host, archive, "Friends", "3.2.0");
            Check(((JArray)manifest["mods"]).Count == 1, "portable packs include manual mods and exclude the manager");
            Check(ModFiles.Fingerprint(manifest) == ModFiles.Fingerprint(ModFiles.ReadManifest(archive)), "pack identity survives save and reload");
            Reject(() => ModFiles.Install(archive, client, backups, "3.1.0", true), "different game versions are rejected before replacing mods");
            Check(File.ReadAllText(Path.Combine(client, "Shared", "Config", "items.xml")) == "previous version", "failed import preserves previous files");
            ModFiles.Install(archive, client, backups, "3.2.0", true);
            Check(ModFiles.Fingerprint(ModFiles.Inventory(host, "Host", "3.2.0")) == ModFiles.Fingerprint(ModFiles.Inventory(client, "Client", "3.2.0")), "host and client have identical file checksums after sync");
            Check(File.ReadAllText(Path.Combine(client, "HotReloadTool", "Config", "items.xml")) == "preserve me", "manager data survives exact host sync");
            Check(!Directory.Exists(Path.Combine(client, "Extra")) && Directory.GetFiles(backups, "items.xml", SearchOption.AllDirectories).Length == 2, "extra and previous mods are backed up");
            ModFiles.Install(archive, client, backups, "3.2.0", true);
            Check(!Directory.Exists(Path.Combine(client, "Shared (2)")), "repeated install does not duplicate mods");
            Reject(() => ModFiles.Child(client, "../escape"), "path traversal is rejected");
            Reject(() => ModFiles.Child(client, "C:/escape"), "absolute paths are rejected");
            var bad = Path.Combine(work, "corrupt.hrpack");
            File.Copy(archive, bad);
            using (var zip = ZipFile.Open(bad, ZipArchiveMode.Update))
            {
                var file = zip.GetEntry("Mods/Shared/Config/items.xml");
                file.Delete();
                using (var writer = new StreamWriter(zip.CreateEntry("Mods/Shared/Config/items.xml").Open())) writer.Write("tampered");
            }
            var before = ModFiles.Fingerprint(ModFiles.Inventory(client, "Client", "3.2.0"));
            Reject(() => ModFiles.Install(bad, client, backups, "3.2.0", true), "tampered archives are rejected");
            Check(before == ModFiles.Fingerprint(ModFiles.Inventory(client, "Client", "3.2.0")), "tampered archive leaves the installed pack intact");
            var slip = Path.Combine(work, "slip.hrpack"); File.Copy(archive, slip);
            using (var zip = ZipFile.Open(slip, ZipArchiveMode.Update)) zip.CreateEntry("../outside.txt");
            Reject(() => ModFiles.Install(slip, client, backups, "3.2.0", true), "archive extraction rejects escaping entries");
            // Simulate a received transfer on disk, including interrupted writes;
            // production checks the same complete-file digest before install.
            var received = Path.Combine(work, "received.hrpack");
            using (var source = File.OpenRead(archive)) using (var target = File.Create(received))
            {
                var buffer = new byte[113]; int n;
                while ((n = source.Read(buffer, 0, buffer.Length)) > 0) target.Write(buffer, 0, n);
            }
            Check(ModFiles.HashFile(received) == ModFiles.HashFile(archive), "chunked transfer reassembles the exact archive");
            using (var target = File.OpenWrite(received)) target.SetLength(new FileInfo(received).Length - 1);
            Check(ModFiles.HashFile(received) != ModFiles.HashFile(archive), "an interrupted transfer fails checksum verification");
            // An occupied file prevents directory replacement; earlier folder moves
            // must be rolled back together.
            var blocked = Path.Combine(work, "blocked"); Directory.CreateDirectory(blocked);
            Mod(blocked, "Extra", "Extra", "original"); File.WriteAllText(Path.Combine(blocked, "Shared"), "occupied file");
            Reject(() => ModFiles.Install(archive, blocked, backups, "3.2.0", true), "disk errors fail the complete install");
            Check(Directory.Exists(Path.Combine(blocked, "Extra")), "disk failure restores previously moved mods");
            string legacyHost = Path.Combine(work, "legacy-host"), legacyClient = Path.Combine(work, "legacy-client");
            Directory.CreateDirectory(legacyHost); Directory.CreateDirectory(legacyClient);
            Mod(legacyHost, "Legacy", "LegacyMod", "legacy mod bytes");
            Mod(legacyClient, "Legacy", "LegacyMod", "previous legacy mod");
            Mod(legacyClient, "LegacyExtra", "LegacyExtra", "previous extra legacy mod");
            Mod(legacyClient, "RenamedManager", "HotReloadTool", "private settings");
            Mod(legacyClient, "RenamedHarmony", "TFP_Harmony", "required dependency");
            var both = Path.Combine(work, "both.hrpack");
            var bothManifest = ModFiles.Export(new[] { host, legacyHost }, both, "Both roots", "3.2.0");
            Check(((JArray)bothManifest["mods"]).Count == 2, "portable packs include the legacy game Mods location");
            ModFiles.Install(both, new[] { client, legacyClient }, backups, "3.2.0", true);
            Check(ModFiles.Fingerprint(bothManifest) == ModFiles.Fingerprint(ModFiles.Inventory(new[] { client, legacyClient }, "Local", "3.2.0")), "sync matches both Mods locations without duplicate mod names");
            Check(File.ReadAllText(Path.Combine(legacyClient, "RenamedManager", "Config", "items.xml")) == "private settings", "protected manager identity survives even when its folder was renamed");
            Check(File.ReadAllText(Path.Combine(legacyClient, "RenamedHarmony", "Config", "items.xml")) == "required dependency", "required game Harmony is protected by its actual mod identity");
            Check(!Directory.Exists(Path.Combine(legacyClient, "Legacy")) && Directory.Exists(Path.Combine(client, "Legacy")), "legacy replacement moves once into the primary Mods folder");
            ModFiles.Export(new[] { host, legacyHost }, both, "Updated name", "3.2.0");
            Check((string)ModFiles.ReadManifest(both)["name"] == "Updated name", "re-export atomically replaces an existing named pack");
            Mod(legacyHost, "Conflicting", "SharedMod", "duplicate identity");
            Reject(() => ModFiles.Export(new[] { host, legacyHost }, both, "Broken", "3.2.0"), "duplicate mod identities across roots are rejected");
            Check((string)ModFiles.ReadManifest(both)["name"] == "Updated name", "a failed re-export preserves the previous saved pack");
            string sourceMods = Path.Combine(work, "source-mods");
            Mod(sourceMods, "SourceLive", "SourceLive", "config");
            Directory.CreateDirectory(Path.Combine(sourceMods, "SourceLive", "src"));
            Directory.CreateDirectory(Path.Combine(sourceMods, "SourceLive", "cache"));
            File.WriteAllText(Path.Combine(sourceMods, "SourceLive", "src", "Mod.cs"), "public class Mod {}");
            File.WriteAllText(Path.Combine(sourceMods, "SourceLive", "SourceLive.dll"), "native startup code");
            var sourceIdentity = ModFiles.Fingerprint(ModFiles.Inventory(sourceMods, "Sources", "3.2.0"));
            File.WriteAllText(Path.Combine(sourceMods, "SourceLive", "cache", "SourceLive.dll"), "fresh local identity");
            Check(sourceIdentity == ModFiles.Fingerprint(ModFiles.Inventory(sourceMods, "Sources", "3.2.0")), "local source compiler caches cannot trigger repeated friend downloads");
            File.WriteAllText(Path.Combine(sourceMods, "SourceLive", "ModInfo.xml"), "<xml><ModInfo><Name value=\"SourceLive\"/></ModInfo></xml>");
            Check(((JArray)ModFiles.Inventory(sourceMods, "Sources", "3.2.0")["mods"]).Count == 1, "existing legacy metadata does not break the whole host pack");
            var queue = new InstallQueue();
            var releaseFirst = new ManualResetEventSlim(false);
            var finished = new ManualResetEventSlim(false);
            string order = "";
            Check(queue.Enqueue("first", () => { releaseFirst.Wait(TimeSpan.FromSeconds(10)); order += "A"; throw new IOException("download failed"); }), "first install is reserved before dispatch");
            Check(!queue.Enqueue("FIRST", () => order += "duplicate"), "repeated clicks do not duplicate a pending install");
            queue.Enqueue("second", () => order += "B");
            queue.Enqueue("third", () => { order += "C"; finished.Set(); });
            releaseFirst.Set();
            Check(finished.Wait(TimeSpan.FromSeconds(10)) && order == "ABC", "failed installs drain the remaining queue in order");
            Console.WriteLine("All production archive checks passed"); return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
}
