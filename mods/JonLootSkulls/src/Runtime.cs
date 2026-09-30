using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using UnityEngine;

namespace JonLootSkulls
{
    public sealed class ModApi : IModApi
    {
        public void InitMod(Mod mod)
        {
            var old = GameObject.Find("JonLootSkulls.Runtime");
            if (old != null) UnityEngine.Object.DestroyImmediate(old);
            var go = new GameObject("JonLootSkulls.Runtime");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.AddComponent<Runtime>();
            var harmony = new Harmony("JonLootSkulls");
            harmony.UnpatchSelf(); harmony.PatchAll(typeof(ModApi).Assembly);
            Runtime.Instance.OpenWorld();
            Log.Out("[JonLootSkulls] Independent gameplay mod loaded (beta).");
        }
    }
    public sealed class Runtime : MonoBehaviour
    {
        public static Runtime Instance;
        public readonly LootLedger Loot = new LootLedger();
        string saveFile, worldGuid;
        Texture2D skull;
        void Awake() { Instance = this; skull = MakeSkull(); }
        void OnDestroy() { if (skull != null) Destroy(skull); if (Instance == this) Instance = null; }
        public void CloseWorld() { Loot.Records.Clear(); saveFile = worldGuid = null; }
        public void OpenWorld()
        {
            var world = GameManager.Instance?.World;
            if (world == null || world.Guid == worldGuid) return;
            worldGuid = world.Guid;
            saveFile = Path.Combine(ConnectionManager.Instance.IsClient ? GameIO.GetSaveGameLocalDir() : GameIO.GetSaveGameDir(), "JonLootSkulls", "loot-skulls.xml");
            try { var legacy = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(saveFile)), "JonCoopQoL", "loot-skulls.xml"); Loot.Load(File.Exists(saveFile) ? saveFile : legacy); }
            catch (Exception error) { Loot.Records.Clear(); Log.Warning("[JonLootSkulls] Loot history: " + error.Message); }
        }
        void SaveLoot()
        {
            if (saveFile == null) return;
            try { Loot.Save(saveFile); }
            catch (Exception error) { Log.Warning("[JonLootSkulls] Saving loot history: " + error.Message); }
        }
        public void ClearPoi(int id) { Loot.ClearPoi(id); SaveLoot(); }
        public void ObserveLoot(ITileEntityLootable container)
        {
            // A queued chunk-read callback can outlive a source hot reload.
            // The retired runtime must never overwrite the replacement's ledger.
            if (Instance != this || container == null || GameManager.Instance?.World == null) return;
            // Chunk reads also occur on workers. Unity and ledger mutation stay
            // on the main thread, scheduled by that concrete replication event.
            if (!ThreadManager.IsMainThread())
            {
                var observedWorld = GameManager.Instance.World;
                ThreadManager.AddSingleTaskMainThread("Jon loot replicated", () =>
                {
                    if (Instance == this && GameManager.Instance?.World == observedWorld) ObserveLoot(container);
                });
                return;
            }
            if (container is TEFeatureStorage removed && removed.Parent == null) return;
            OpenWorld();
            Vector3i position = container.ToWorldPos();
            string key = position.x + "," + position.y + "," + position.z;
            bool playerPlaced = container is TEFeatureStorage storage && storage.Parent.PlayerPlaced;
            if (container.bPlayerStorage || playerPlaced || !container.bTouched || !container.IsEmpty())
            {
                if (Loot.Records.Remove(key)) SaveLoot();
                return;
            }
            int touched = GameUtils.WorldTimeToTotalHours(container.worldTimeTouched);
            if (Loot.Records.TryGetValue(key, out var old) && old.TouchedHours == touched) return;
            var poi = GameManager.Instance.World.GetPOIAtPosition(position.ToVector3(), null, null);
            Vector3 anchor = position.ToVector3() + new Vector3(0.5f,2,0.5f);
            if (poi != null) anchor = poi.boundingBoxPosition.ToVector3() + new Vector3(poi.boundingBoxSize.x / 2f, poi.boundingBoxSize.y + 3, poi.boundingBoxSize.z / 2f);
            Loot.Records[key] = new LootRecord { Key=key, X=position.x,Y=position.y,Z=position.z, PoiId=poi == null ? -1 : poi.id,
                AnchorX=anchor.x, AnchorY=anchor.y, AnchorZ=anchor.z, TouchedHours=touched };
            SaveLoot();
        }
        static Texture2D MakeSkull()
        {
            var texture = new Texture2D(64,64,TextureFormat.RGBA32,false);
            for (int y=0;y<64;y++) for(int x=0;x<64;x++)
            {
                float dx=x-31.5f,dy=y-38;
                bool head=dx*dx/625+dy*dy/400<1;
                bool jaw=y>=10 && y<=27 && Math.Abs(dx)<15;
                bool eye=((x-22)*(x-22)+(y-39)*(y-39)<34)||((x-42)*(x-42)+(y-39)*(y-39)<34);
                bool nose=y>=26&&y<=32&&Math.Abs(dx)<(32-y)*0.6f;
                bool teeth=y<19 && y>=10 && (x%7<2);
                texture.SetPixel(x,y,(head||jaw) ? (eye||nose||teeth ? new Color(0.08f,0.05f,0.05f,1):new Color(0.95f,0.88f,0.77f,1)) : Color.clear);
            }
            texture.Apply(); return texture;
        }
        void OnGUI()
        {
            var player = GameManager.Instance?.World?.GetPrimaryPlayer();
            if (player == null || player.IsDead()) return;
            if (Event.current.type == EventType.Repaint && player.playerCamera != null)
            {
                int now = GameUtils.WorldTimeToTotalHours(GameManager.Instance.World.GetWorldTime());
                int days = GamePrefs.GetInt(EnumGamePrefs.LootRespawnDays);
                var places = new Dictionary<string,LootRecord>();
                foreach (var record in Loot.Records.Values)
                {
                    if (Rules.RespawnDue(record.TouchedHours, now, days)) continue;
                    string place = record.PoiId < 0 ? record.Key : "poi:" + record.PoiId;
                    if (!places.TryGetValue(place, out var prior) || prior.TouchedHours < record.TouchedHours) places[place] = record;
                }
                foreach (var record in places.Values)
                {
                    var anchor = new Vector3(record.AnchorX, record.AnchorY, record.AnchorZ);
                    if (Vector3.Distance(anchor, player.position) > 500) continue;
                    var screen = player.playerCamera.WorldToScreenPoint(anchor - Origin.position);
                    if (screen.z <= 0) continue;
                    var bounds = new Rect(screen.x - 15, Screen.height - screen.y - 36, 30,36);
                    GUI.DrawTexture(bounds, skull);
                    int hours = Math.Max(0, record.TouchedHours + days * 24 - now);
                    GUI.Label(new Rect(bounds.x-70,bounds.y+35,170,25), days <= 0 ? "Looted · no respawn" : "Looted · " + hours + " game hours");
                }
            }
        }
    }
    [HarmonyPatch(typeof(EntityPlayerLocal),"OnAddedToWorld")]
    public static class OpenWorld { public static void Postfix() { Runtime.Instance?.OpenWorld(); } }
    [HarmonyPatch(typeof(GameManager),"SaveAndCleanupWorld")]
    public static class CloseWorld { public static void Prefix() { Runtime.Instance?.CloseWorld(); } }
}
