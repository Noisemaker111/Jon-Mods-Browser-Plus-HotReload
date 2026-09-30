using System;
using System.Collections.Generic;
using System.IO;

namespace JonGroundPings
{
    public enum TeamKind : byte { Request = 1, Begin = 2, Waypoint = 3, End = 4, Ping = 5 }
    public sealed class TeamWaypoint
    {
        public int X, Y, Z;
        public string Name, Icon;
    }
    public sealed class TeamMessage
    {
        public TeamKind Kind;
        public string World, Batch;
        public int Owner, Count;
        public TeamWaypoint Waypoint;
        public float X, Y, Z, NX, NY, NZ, Heading;
    }
    public static class TeamProtocol
    {
        public static byte[] Encode(TeamMessage message)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write((byte)message.Kind); writer.Write(message.World ?? ""); writer.Write(message.Owner);
                switch (message.Kind)
                {
                    case TeamKind.Request: break;
                    case TeamKind.Begin: writer.Write(message.Batch); writer.Write(message.Count); break;
                    case TeamKind.Waypoint:
                        writer.Write(message.Batch); var w = message.Waypoint;
                        writer.Write(w.X); writer.Write(w.Y); writer.Write(w.Z); writer.Write(w.Name ?? ""); writer.Write(w.Icon ?? ""); break;
                    case TeamKind.End: writer.Write(message.Batch); break;
                    case TeamKind.Ping:
                        writer.Write(message.X); writer.Write(message.Y); writer.Write(message.Z);
                        writer.Write(message.NX); writer.Write(message.NY); writer.Write(message.NZ); writer.Write(message.Heading); break;
                    default: throw new InvalidDataException("Unknown team operation");
                }
                return stream.ToArray();
            }
        }
        public static TeamMessage Decode(byte[] bytes)
        {
            using (var stream = new MemoryStream(bytes, false))
            using (var reader = new BinaryReader(stream))
            {
                var m = new TeamMessage { Kind = (TeamKind)reader.ReadByte(), World = reader.ReadString(), Owner = reader.ReadInt32() };
                switch (m.Kind)
                {
                    case TeamKind.Request: break;
                    case TeamKind.Begin:
                        m.Batch = reader.ReadString(); m.Count = reader.ReadInt32();
                        if (m.Count < 0) throw new InvalidDataException("Negative waypoint count"); break;
                    case TeamKind.Waypoint:
                        m.Batch = reader.ReadString(); m.Waypoint = new TeamWaypoint { X = reader.ReadInt32(), Y = reader.ReadInt32(), Z = reader.ReadInt32(), Name = reader.ReadString(), Icon = reader.ReadString() }; break;
                    case TeamKind.End: m.Batch = reader.ReadString(); break;
                    case TeamKind.Ping:
                        m.X = reader.ReadSingle(); m.Y = reader.ReadSingle(); m.Z = reader.ReadSingle();
                        m.NX = reader.ReadSingle(); m.NY = reader.ReadSingle(); m.NZ = reader.ReadSingle(); m.Heading = reader.ReadSingle();
                        if (!Finite(m.X) || !Finite(m.Y) || !Finite(m.Z) || !Finite(m.NX) || !Finite(m.NY) || !Finite(m.NZ) || !Finite(m.Heading)) throw new InvalidDataException("Invalid ping position"); break;
                    default: throw new InvalidDataException("Unknown team operation");
                }
                if (stream.Position != stream.Length) throw new InvalidDataException("Unexpected team packet suffix");
                return m;
            }
        }
        static bool Finite(float x) { return !float.IsNaN(x) && !float.IsInfinity(x); }
        public static bool Authorized(string currentWorld, string messageWorld, int claimedOwner, int authenticatedOwner, bool loggedIn, bool partyMember)
        { return loggedIn && partyMember && claimedOwner == authenticatedOwner && currentWorld == messageWorld; }
        public const float PingSeconds = 6;
        public static float PingAlpha(float age) { return age < 0 || age >= PingSeconds ? 0 : Math.Min(1, (PingSeconds - age) / 2); }
        public static float PingScale(float age) { return 1.3f + 0.18f * (float)Math.Sin(age * Math.PI * 3); }
    }
    // Begin/items/end commit a whole snapshot atomically, including deletion of
    // every waypoint. Lost or superseded snapshots cannot erase current markers.
    public sealed class TeamSnapshots
    {
        sealed class Pending { public string Batch; public int Count; public readonly List<TeamWaypoint> Items = new List<TeamWaypoint>(); }
        readonly Dictionary<int, Pending> pending = new Dictionary<int, Pending>();
        public readonly Dictionary<int, List<TeamWaypoint>> Completed = new Dictionary<int, List<TeamWaypoint>>();
        public bool Accept(TeamMessage m)
        {
            if (m.Kind == TeamKind.Begin)
            { pending[m.Owner] = new Pending { Batch = m.Batch, Count = m.Count }; return false; }
            if (!pending.TryGetValue(m.Owner, out var p) || p.Batch != m.Batch) return false;
            if (m.Kind == TeamKind.Waypoint)
            {
                if (p.Items.Count >= p.Count) { pending.Remove(m.Owner); return false; }
                p.Items.Add(m.Waypoint); return false;
            }
            if (m.Kind != TeamKind.End) return false;
            pending.Remove(m.Owner);
            if (p.Items.Count != p.Count) return false;
            Completed[m.Owner] = p.Items; return true;
        }
        public void Remove(int owner) { pending.Remove(owner); Completed.Remove(owner); }
        public void Clear() { pending.Clear(); Completed.Clear(); }
    }
}
