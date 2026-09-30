using System;
using System.Linq;
using HarmonyLib;
using UnityEngine;
namespace JonGroundPings
{
    public sealed class TeamService : IDisposable
    {
        readonly Runtime runtime;
        readonly TeamTransport.Subscription subscription;
        bool disposed, closing;
        public TeamService(Runtime owner) { runtime=owner; subscription=new TeamTransport.Subscription(Receive); }
        World World => GameManager.Instance?.World;
        EntityPlayerLocal Local => World?.GetPrimaryPlayer();
        bool Server => ConnectionManager.Instance != null && ConnectionManager.Instance.IsServer;
        static bool SameParty(EntityPlayer a, EntityPlayer b) { return a != null && b != null && a.Party != null && b.Party == a.Party && a.Party.ContainsMember(b.entityId); }
        void Broadcast(TeamMessage m, EntityPlayer owner)
        {
            if (owner?.Party == null) return;
            foreach (var member in owner.Party.MemberList)
            {
                if (member.entityId == owner.entityId || !SameParty(owner, member)) continue;
                var client=ConnectionManager.Instance.Clients.ForEntityId(member.entityId);
                if (client != null && client.loginDone && !client.disconnecting) subscription.Send(TeamProtocol.Encode(m),client);
            }
        }
        void Receive(byte[] bytes, ClientInfo sender)
        {
            if (disposed || closing) return;
            if (!ThreadManager.IsMainThread())
            {
                var observed=World;
                ThreadManager.AddSingleTaskMainThread("Jon ping received",()=> { if (!disposed && World==observed) Receive(bytes,sender); }); return;
            }
            try
            {
                if (World==null) return;
                var m=TeamProtocol.Decode(bytes);
                if (m.Kind!=TeamKind.Ping || m.World!=World.Guid) return;
                var owner=World.GetEntity(m.Owner) as EntityPlayer;
                if (Server)
                {
                    if (!TeamProtocol.Authorized(World.Guid,m.World,m.Owner,sender==null?-1:sender.entityId,
                        sender!=null && sender.loginDone && !sender.disconnecting,owner?.Party!=null && owner.Party.ContainsMember(owner.entityId))) return;
                    if (Vector3.Distance(new Vector3(m.X,m.Y,m.Z),owner.position)>500) return;
                    Broadcast(m,owner);
                }
                else if (sender!=null) return;
                if (SameParty(Local,owner)) runtime.Pings.Add(m);
            }
            catch (Exception error) { Log.Warning("[JonGroundPings] Ignored packet: "+error.Message); }
        }
        public void Ping(TeamMessage m)
        {
            if (Local==null || disposed || closing) return;
            m.World=World.Guid; m.Owner=Local.entityId; runtime.Pings.Add(m);
            if (Local.Party==null) return;
            if (Server) Broadcast(m,Local); else subscription.Send(TeamProtocol.Encode(m),null);
        }
        public void OpenWorld() { closing=false; runtime.Pings.Clear(); }
        public void CloseWorld() { closing=true; runtime.Pings.Clear(); }
        public void PartyChanged(EntityPlayer player) { if (Local?.Party==null) runtime.Pings.Clear(); }
        public void Dispose() { disposed=true; runtime.Pings.Clear(); subscription.Dispose(); }
    }
    [HarmonyPatch(typeof(EntityPlayer),"HandleOnPartyLeave")]
    public static class TeamLeft { public static void Postfix(EntityPlayer __instance) { Runtime.Instance?.Team?.PartyChanged(__instance); } }
}
