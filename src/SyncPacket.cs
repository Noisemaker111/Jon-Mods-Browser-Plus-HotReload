using System;
using System.IO;
using HarmonyLib;

namespace HotReloadTool
{
    // Network types stay in the bootstrap, so core hot swaps keep the game's
    // negotiated package mapping and the same transport identity.
    public class NetPackageJonModSync : NetPackage
    {
        byte[] payload = new byte[0];
        public NetPackageJonModSync Setup(byte[] value) { payload = value; return this; }
        public override NetPackageDirection PackageDirection { get { return NetPackageDirection.Both; } }
        public override bool ReliableDelivery { get { return true; } }
        public override void read(PooledBinaryReader reader)
        {
            int length = reader.ReadInt32();
            if (length < 0 || length > 65536) throw new InvalidDataException("Invalid mod sync packet length");
            payload = reader.ReadBytes(length);
            if (payload.Length != length) throw new EndOfStreamException();
        }
        public override void write(PooledBinaryWriter writer)
        {
            base.write(writer);
            writer.Write(payload.Length);
            writer.Write(payload);
        }
        public override int GetLength() { return payload.Length + 6; }
        public override void ProcessPackage(World world, GameManager manager)
        {
            var handler = JonSyncTransport.Receive;
            if (handler != null) handler(payload, Sender);
        }
    }

    public static class JonSyncTransport
    {
        public static Action<byte[], ClientInfo> Receive;
        public static Action Connected;
        public static Func<Action, bool> BeforeWorldJoin;
        public static Action Disconnected;
        public static Action WorldCleaned;
        public static Action<ClientInfo> ClientLeft;
        public static bool RestartRequired;
        static bool started;
        static bool continuingJoin;
        public static void Start()
        {
            if (started) return;
            started = true;
            NetPackageManager.knownPackageTypes[typeof(NetPackageJonModSync).Name] = typeof(NetPackageJonModSync);
            var harmony = new Harmony("hotreload.sync.transport");
            // Native PlayerAllowed validates the host and upgrades the connection
            // before calling StartGame. Hold world/config loading until mods match.
            harmony.Patch(AccessTools.Method(typeof(GameManager), "StartGame", new[] { typeof(bool) }), prefix: new HarmonyMethod(typeof(JonSyncTransport), "BeforeStartGame"));
            harmony.Patch(AccessTools.Method(typeof(ConnectionManager), "DisconnectFromServer"), postfix: new HarmonyMethod(typeof(JonSyncTransport), "ConnectionEnded"));
            harmony.Patch(AccessTools.Method(typeof(GameManager), "SaveAndCleanupWorld"), postfix: new HarmonyMethod(typeof(JonSyncTransport), "CleanupFinished"));
            harmony.Patch(AccessTools.Method(typeof(GameManager), "PlayerDisconnected"), postfix: new HarmonyMethod(typeof(JonSyncTransport), "PlayerLeft"));
            ModEvents.GameStartDone.RegisterHandler(GameStarted);
            ModEvents.WorldShuttingDown.RegisterHandler(WorldStopped);
        }
        public static bool BeforeStartGame(GameManager __instance, bool __0)
        {
            var connection = ConnectionManager.Instance;
            var handler = BeforeWorldJoin;
            if (continuingJoin || handler == null || connection == null || !connection.IsClient || connection.IsServer || __instance.World != null) return true;
            bool local = __0;
            return !handler(() =>
            {
                if (ConnectionManager.Instance != connection || !connection.IsConnected) return;
                continuingJoin = true;
                try { __instance.StartGame(local); }
                finally { continuingJoin = false; }
            });
        }
        public static void ConnectionEnded() { Disconnected?.Invoke(); }
        static void GameStarted(ref ModEvents.SGameStartDoneData data)
        {
            if (ConnectionManager.Instance != null && ConnectionManager.Instance.IsClient) Connected?.Invoke();
        }
        static void WorldStopped(ref ModEvents.SWorldShuttingDownData data) { Disconnected?.Invoke(); }
        public static void CleanupFinished() { WorldCleaned?.Invoke(); }
        public static void PlayerLeft(ClientInfo __0) { ClientLeft?.Invoke(__0); }
        public static void Send(byte[] bytes, ClientInfo client)
        {
            var packet = NetPackageManager.GetPackage<NetPackageJonModSync>().Setup(bytes);
            if (client != null) client.SendPackage(packet);
            else ConnectionManager.Instance.SendToServer(packet, false);
        }
    }
}
