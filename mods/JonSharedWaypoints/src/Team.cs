using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace JonSharedWaypoints
{
    public sealed class TeamService : IDisposable
    {
        readonly Runtime runtime;
        readonly TeamSnapshots snapshots = new TeamSnapshots();
        readonly Dictionary<int, List<Waypoint>> mirrors = new Dictionary<int, List<Waypoint>>();
        readonly Dictionary<int, string> partySignatures = new Dictionary<int, string>();
        TeamTransport.Subscription subscription;
        Action<byte[], ClientInfo> send;
        string world, ownSignature;
        bool applying, disposed, closing;
        public TeamService(Runtime owner)
        {
            runtime = owner;
            subscription = new TeamTransport.Subscription(Receive);
            send = subscription.Send;
        }
        World World => GameManager.Instance?.World;
        EntityPlayerLocal Local => World?.GetPrimaryPlayer();
        bool Server => ConnectionManager.Instance != null && ConnectionManager.Instance.IsServer;
        void EnsureWorld()
        {
            if (World == null || world == TeamProtocol.WorldKey()) return;
            Clear(); world = TeamProtocol.WorldKey();
        }
        public void OpenWorld() { closing = false; EnsureWorld(); PartyChanged(Local); }
        public void CloseWorld() { closing = true; Clear(); }
        public void Spawned(int id)
        {
            closing = false; EnsureWorld(); partySignatures.Remove(id); PartyChanged(Player(id));
        }
        public void Clear()
        {
            foreach (var id in mirrors.Keys.ToArray()) RemoveMirror(id);
            mirrors.Clear(); snapshots.Clear(); partySignatures.Clear();
            world = ownSignature = null;
        }
        public void Dispose()
        {
            disposed = true; Clear();
            subscription?.Dispose();
        }
        TeamMessage Message(TeamKind kind, int owner) { return new TeamMessage { Kind = kind, World = world, Owner = owner }; }
        void Send(TeamMessage m, ClientInfo client = null) { if (send != null) send(TeamProtocol.Encode(m), client); }
        EntityPlayer Player(int id) { return World?.GetEntity(id) as EntityPlayer; }
        static bool SameParty(EntityPlayer a, EntityPlayer b) { return a != null && b != null && a.Party != null && b.Party == a.Party && a.Party.ContainsMember(b.entityId); }
        void Receive(byte[] bytes, ClientInfo sender)
        {
            if (disposed || closing) return;
            if (!ThreadManager.IsMainThread())
            {
                var observed = World;
                ThreadManager.AddSingleTaskMainThread("Jon team message", () => { if (!disposed && World == observed) Receive(bytes, sender); });
                return;
            }
            try
            {
                EnsureWorld(); if (World == null) return;
                var m = TeamProtocol.Decode(bytes);
                if (m.World != world || m.Kind == TeamKind.Ping) return;
                if (Server)
                {
                    var owner = sender == null ? null : Player(sender.entityId);
                    if (!TeamProtocol.Authorized(world, m.World, m.Owner, sender == null ? -1 : sender.entityId,
                        sender != null && sender.loginDone && !sender.disconnecting, owner?.Party != null && owner.Party.ContainsMember(owner.entityId))) return;
                    if (m.Kind == TeamKind.Request)
                    {
                        PublishOwn(true);
                        foreach (var member in owner.Party.MemberList)
                        {
                            if (snapshots.Completed.TryGetValue(member.entityId, out var points)) SendSnapshot(member.entityId, points, sender);
                            if (member.entityId != owner.entityId) RequestFrom(member);
                        }
                    }
                    else if (snapshots.Accept(m))
                    { BroadcastSnapshot(owner); ApplySnapshot(m.Owner); }
                }
                else
                {
                    // Native clients only accept the host's connection. The host
                    // authenticated every owner before relaying to this party.
                    if (sender != null || Local == null) return;
                    if (m.Kind == TeamKind.Request) { PublishOwn(true); return; }
                    if (m.Owner == Local.entityId || !SameParty(Local, Player(m.Owner))) return;
                    if (snapshots.Accept(m)) ApplySnapshot(m.Owner);
                }
            }
            catch (Exception error) { Log.Warning("[JonSharedWaypoints] Team packet ignored: " + error.Message); }
        }
        void RequestFrom(EntityPlayer member)
        {
            var client = ConnectionManager.Instance.Clients.ForEntityId(member.entityId);
            if (client != null && client.loginDone && !client.disconnecting) Send(Message(TeamKind.Request, member.entityId), client);
        }
        void Broadcast(TeamMessage m, EntityPlayer owner)
        {
            if (owner?.Party == null) return;
            foreach (var member in owner.Party.MemberList)
            {
                if (member.entityId == owner.entityId || !SameParty(owner, member)) continue;
                var client = ConnectionManager.Instance.Clients.ForEntityId(member.entityId);
                if (client != null && client.loginDone && !client.disconnecting) Send(m, client);
            }
        }
        void SendSnapshot(int owner, List<TeamWaypoint> points, ClientInfo client)
        {
            string batch = Guid.NewGuid().ToString("N");
            var m = Message(TeamKind.Begin, owner); m.Batch = batch; m.Count = points.Count; Send(m, client);
            foreach (var point in points)
            { m = Message(TeamKind.Waypoint, owner); m.Batch = batch; m.Waypoint = point; Send(m, client); }
            m = Message(TeamKind.End, owner); m.Batch = batch; Send(m, client);
        }
        void BroadcastSnapshot(EntityPlayer owner)
        {
            if (send == null || owner?.Party == null || !snapshots.Completed.TryGetValue(owner.entityId, out var points)) return;
            foreach (var member in owner.Party.MemberList)
            {
                if (member.entityId == owner.entityId || !SameParty(owner, member)) continue;
                var client = ConnectionManager.Instance.Clients.ForEntityId(member.entityId);
                if (client != null && client.loginDone && !client.disconnecting) SendSnapshot(owner.entityId, points, client);
            }
        }
        public void PublishOwn(bool force = false)
        {
            if (disposed || closing || applying || send == null || Local?.Party == null) return;
            EnsureWorld();
            var points = Local.Waypoints.Collection.list.Where(w => w.IsSaved && !w.bIsAutoWaypoint && w.lastKnownPositionEntityId < 0)
                .Select(w => new TeamWaypoint { X = w.pos.x, Y = w.pos.y, Z = w.pos.z, Name = w.name.Text, Icon = w.icon }).ToList();
            // Compare the actual wire data, including names and icons. Paging or
            // filtering the native map cannot cause a redundant snapshot.
            string signature = string.Join("|", points.Select(p => Convert.ToBase64String(TeamProtocol.Encode(new TeamMessage { Kind = TeamKind.Waypoint, World = world, Batch = "", Owner = Local.entityId, Waypoint = p }))));
            if (!force && signature == ownSignature) return;
            try
            {
                if (Server) { snapshots.Completed[Local.entityId] = points; BroadcastSnapshot(Local); }
                else SendSnapshot(Local.entityId, points, null);
                ownSignature = signature;
            }
            catch (Exception error) { Log.Warning("[JonSharedWaypoints] Waypoint publish: " + error.Message); }
        }
        public void PartyChanged(EntityPlayer player)
        {
            if (disposed || closing || World == null) return;
            EnsureWorld();
            foreach (var id in mirrors.Keys.ToArray())
                if (!SameParty(Local, Player(id))) { RemoveMirror(id); snapshots.Remove(id); }
            if (player == null) return;
            if (player.Party == null) { snapshots.Remove(player.entityId); }
            string signature = player.Party == null ? "" : player.Party.PartyID + ":" + string.Join(",", player.Party.MemberList.Select(p => p.entityId).OrderBy(id => id));
            if (partySignatures.TryGetValue(player.entityId, out var previous) && previous == signature) return;
            partySignatures[player.entityId] = signature;
            if (Local == player)
            {
                ownSignature = null;
                PublishOwn(true);
                if (!Server && player.Party != null && send != null) Send(Message(TeamKind.Request, player.entityId));
            }
            if (Server && player.Party != null)
            {
                foreach (var member in player.Party.MemberList)
                { BroadcastSnapshot(member); RequestFrom(member); }
                foreach (var id in snapshots.Completed.Keys.ToArray()) if (SameParty(Local, Player(id))) ApplySnapshot(id);
            }
            RefreshMap();
        }
        void RemoveMirror(int id)
        {
            if (!mirrors.TryGetValue(id, out var existing)) return;
            var list = MapList();
            if (list != null)
            {
                if (existing.Contains(list.SelectedWaypoint)) list.SelectedWaypoint = null;
                if (existing.Contains(list.TrackedWaypoint)) list.TrackedWaypoint = null;
            }
            foreach (var w in existing)
            {
                Local?.Waypoints?.Collection.Remove(w);
                if (w.navObject != null) NavObjectManager.Instance?.UnRegisterNavObject(w.navObject);
                // Nav objects are pooled. A late native text-filter callback
                // must not rename a recycled marker belonging to someone else.
                w.navObject = null;
            }
            mirrors.Remove(id);
        }
        void ApplySnapshot(int ownerId)
        {
            var owner = Player(ownerId);
            if (Local == null || ownerId == Local.entityId || !SameParty(Local, owner) || !snapshots.Completed.TryGetValue(ownerId, out var points)) return;
            applying = true;
            try
            {
                var list = MapList();
                string selected = mirrors.TryGetValue(ownerId, out var selectedOld) && selectedOld.Contains(list?.SelectedWaypoint) ? Key(list.SelectedWaypoint) : null;
                var tracked = mirrors.TryGetValue(ownerId, out var old) ? new HashSet<string>(old.Where(w => w.bTracked).Select(w => w.pos + ":" + w.icon + ":" + w.name.Text)) : new HashSet<string>();
                RemoveMirror(ownerId);
                var created = new List<Waypoint>(); mirrors[ownerId] = created;
                foreach (var point in points)
                {
                    var w = new Waypoint { pos = new Vector3i(point.X, point.Y, point.Z), icon = point.Icon,
                        name = new AuthoredText(owner.PlayerDisplayName + ": " + point.Name, owner.PersistentPlayerData?.PrimaryId),
                        ownerId = owner.PersistentPlayerData?.PrimaryId, IsSaved = false };
                    // Unsaved native waypoints never leak into the local save or
                    // get exported as if the receiver authored them.
                    Local.Waypoints.Collection.Add(w); created.Add(w);
                    w.bTracked = tracked.Contains(w.pos + ":" + w.icon + ":" + w.name.Text);
                    w.navObject = NavObjectManager.Instance.RegisterNavObject("waypoint", w.pos.ToVector3(), w.icon, false, -1, null);
                    w.navObject.IsActive = w.bTracked; w.navObject.IsTracked = w.bTracked;
                    w.navObject.name = owner.PlayerDisplayName + ": waypoint";
                    GeneratedTextManager.GetDisplayText(w.name, text => { if (w.navObject != null) w.navObject.name = text; }, true, false, (GeneratedTextManager.TextFilteringMode)3, (GeneratedTextManager.BbCodeSupportMode)2);
                }
                if (list != null && selected != null) list.SelectedWaypoint = created.FirstOrDefault(w => Key(w) == selected);
                RefreshMap();
            }
            finally { applying = false; }
        }
        void RefreshMap()
        {
            var list = MapList();
            if (list == null) return;
            list.TrackedWaypoint = null; list.GetTrackedWaypoint();
            list.UpdateWaypointsList(list.SelectedWaypoint);
        }
        static string Key(Waypoint w) { return w.pos + ":" + w.icon + ":" + w.name.Text; }
        XUiC_MapWaypointList MapList() { return Local?.playerUI?.xui?.GetWindow("mapTracking")?.Controller?.GetChildById("waypointList") as XUiC_MapWaypointList; }


    }
    [HarmonyPatch(typeof(XUiC_MapWaypointList), "UpdateWaypointsList")]
    public static class WaypointsChanged { public static void Postfix() { Runtime.Instance?.Team?.PublishOwn(); } }
    [HarmonyPatch(typeof(EntityPlayer), "HandleOnPartyJoined")]
    public static class TeamJoined { public static void Postfix(EntityPlayer __instance) { Runtime.Instance?.Team?.PartyChanged(__instance); } }
    [HarmonyPatch(typeof(EntityPlayer), "HandleOnPartyChanged")]
    public static class TeamChanged { public static void Postfix(EntityPlayer __instance) { Runtime.Instance?.Team?.PartyChanged(__instance); } }
    [HarmonyPatch(typeof(EntityPlayer), "HandleOnPartyLeave")]
    public static class TeamLeft { public static void Postfix(EntityPlayer __instance) { Runtime.Instance?.Team?.PartyChanged(__instance); } }
    [HarmonyPatch(typeof(GameManager), "PlayerSpawnedInWorld")]
    public static class TeamSpawned { public static void Postfix(int __3) { Runtime.Instance?.Team?.Spawned(__3); } }
}
