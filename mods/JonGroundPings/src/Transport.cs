using System;
using System.IO;
using System.Reflection;
namespace JonGroundPings
{
    public sealed class NetPackageJonGroundPing : NetPackage
    {
        byte[] payload = new byte[0];
        public NetPackageJonGroundPing Setup(byte[] bytes) { payload = bytes; return this; }
        public override NetPackageDirection PackageDirection { get { return NetPackageDirection.Both; } }
        public override bool ReliableDelivery { get { return true; } }
        public override void read(PooledBinaryReader reader)
        {
            int length = reader.ReadInt32();
            if (length < 0 || length > 65536) throw new InvalidDataException("Invalid gameplay packet length");
            payload = reader.ReadBytes(length);
            if (payload.Length != length) throw new EndOfStreamException();
        }
        public override void write(PooledBinaryWriter writer) { base.write(writer); writer.Write(payload.Length); writer.Write(payload); }
        public override int GetLength() { return payload.Length + 6; }
        public override void ProcessPackage(World world, GameManager manager) { TeamTransport.Receive?.Invoke(payload, Sender); }
    }
    public static class TeamTransport
    {
        public static Action<byte[], ClientInfo> Receive;
        public static void Send(byte[] bytes, ClientInfo client)
        {
            if (bytes == null || bytes.Length > 65536) throw new InvalidDataException("Gameplay packet exceeds capacity");
            var packet = NetPackageManager.GetPackage<NetPackageJonGroundPing>().Setup(bytes);
            if (client != null) client.SendPackage(packet);
            else ConnectionManager.Instance.SendToServer(packet, false);
        }
        // A source reload rebinds the handler on the originally registered
        // assembly, preserving native package identity for this game session.
        public sealed class Subscription : IDisposable
        {
            readonly FieldInfo receiver;
            readonly Action<byte[], ClientInfo> handler;
            public readonly Action<byte[], ClientInfo> Send;
            public Subscription(Action<byte[], ClientInfo> callback)
            {
                Type packet;
                if (!NetPackageManager.knownPackageTypes.TryGetValue("NetPackageJonGroundPing", out packet))
                { packet = typeof(NetPackageJonGroundPing); NetPackageManager.knownPackageTypes["NetPackageJonGroundPing"] = packet; }
                var transport = packet.Assembly.GetType("JonGroundPings.TeamTransport");
                receiver = transport.GetField("Receive"); handler = callback;
                receiver.SetValue(null, Delegate.Combine(receiver.GetValue(null) as Delegate, handler));
                Send = (Action<byte[], ClientInfo>)Delegate.CreateDelegate(typeof(Action<byte[], ClientInfo>), transport.GetMethod("Send"));
            }
            public void Dispose() { receiver.SetValue(null, Delegate.Remove(receiver.GetValue(null) as Delegate, handler)); }
        }
    }
}
