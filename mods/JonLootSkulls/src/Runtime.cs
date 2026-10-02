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
            // Only containers that belong to a POI building count; a nest or
            // trash pile out in the open never gets a marker.
            var poi = GameManager.Instance.World.GetPOIAtPosition(position.ToVector3(), null, null);
            if (poi != null) Changed(poi.id);
            if (poi == null || container.bPlayerStorage || playerPlaced || !container.bTouched)
            {
                if (Loot.Records.Remove(key)) SaveLoot();
                return;
            }
            int touched = GameUtils.WorldTimeToTotalHours(container.worldTimeTouched);
            if (Loot.Records.TryGetValue(key, out var old) && old.TouchedHours == touched && old.PoiId == poi.id) return;
            Vector3 anchor = poi.boundingBoxPosition.ToVector3() + new Vector3(poi.boundingBoxSize.x / 2f, poi.boundingBoxSize.y + 3, poi.boundingBoxSize.z / 2f);
            Loot.Records[key] = new LootRecord { Key=key, X=position.x,Y=position.y,Z=position.z, PoiId=poi.id,
                AnchorX=anchor.x, AnchorY=anchor.y, AnchorZ=anchor.z, TouchedHours=touched };
            SaveLoot();
        }

        // Per POI: how many natural containers it holds, recounted only after
        // a container in it changes or loads.
        sealed class PoiCount { public int Total; public bool Stale = true; }
        readonly Dictionary<int, PoiCount> counts = new Dictionary<int, PoiCount>();
        void Changed(int poiId) { if (!counts.TryGetValue(poiId, out var c)) counts[poiId] = c = new PoiCount(); c.Stale = true; }
        int Total(PrefabInstance poi)
        {
            if (!counts.TryGetValue(poi.id, out var c)) counts[poi.id] = c = new PoiCount();
            if (!c.Stale) return c.Total;
            c.Stale = false;
            var world = GameManager.Instance.World;
            Vector3i min = poi.boundingBoxPosition, max = min + poi.boundingBoxSize;
            int total = 0;
            foreach (var chunk in world.ChunkCache.GetChunkArrayCopySync())
            {
                var origin = chunk.GetWorldPos();
                if (origin.x + 16 < min.x || origin.x > max.x || origin.z + 16 < min.z || origin.z > max.z) continue;
                foreach (var te in chunk.GetTileEntities().list)
                {
                    if (!te.TryGetSelfOrFeature<ITileEntityLootable>(out var loot) || loot.bPlayerStorage) continue;
                    if (loot is TEFeatureStorage storage && (storage.Parent == null || storage.Parent.PlayerPlaced)) continue;
                    var at = te.ToWorldPos();
                    if (at.x >= min.x && at.x < max.x && at.y >= min.y && at.y < max.y && at.z >= min.z && at.z < max.z) total++;
                }
            }
            return c.Total = Math.Max(c.Total, total);
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
            if (player == null || player.IsDead() || Event.current.type != EventType.Repaint || player.playerCamera == null) return;
            var world = GameManager.Instance.World;
            var decorator = GameManager.Instance.GetDynamicPrefabDecorator();
            if (decorator == null) return;
            int now = GameUtils.WorldTimeToTotalHours(world.GetWorldTime());
            int days = GamePrefs.GetInt(EnumGamePrefs.LootRespawnDays);
            var searched = new Dictionary<int, int>();
            var respawn = new Dictionary<int, int>();
            foreach (var record in Loot.Records.Values)
            {
                if (record.PoiId < 0 || Rules.RespawnDue(record.TouchedHours, now, days)) continue;
                searched[record.PoiId] = (searched.TryGetValue(record.PoiId, out int n) ? n : 0) + 1;
                int left = record.TouchedHours + days * 24 - now;
                if (!respawn.TryGetValue(record.PoiId, out int first) || left < first) respawn[record.PoiId] = left;
            }
            foreach (var pair in searched)
            {
                var poi = decorator.GetPrefab(pair.Key);
                if (poi == null) continue;
                var anchor = poi.boundingBoxPosition.ToVector3() + new Vector3(poi.boundingBoxSize.x / 2f, poi.boundingBoxSize.y + 3, poi.boundingBoxSize.z / 2f);
                if (Vector3.Distance(anchor, player.position) > 500) continue;
                var screen = player.playerCamera.WorldToScreenPoint(anchor - Origin.position);
                if (screen.z <= 0) continue;
                int total = Math.Max(Total(poi), pair.Value);
                int percent = Mathf.RoundToInt(100f * pair.Value / total);
                int zombies = 0;
                Vector3 min = poi.boundingBoxPosition.ToVector3(), max = min + poi.boundingBoxSize.ToVector3();
                foreach (var entity in world.Entities.list)
                    if (entity is EntityEnemy enemy && !enemy.IsDead() && enemy.position.x >= min.x && enemy.position.x < max.x && enemy.position.z >= min.z && enemy.position.z < max.z && enemy.position.y >= min.y - 2 && enemy.position.y < max.y) zombies++;
                var bounds = new Rect(screen.x - 15, Screen.height - screen.y - 36, 30, 36);
                GUI.DrawTexture(bounds, skull);
                string state = percent >= 100 ? "Looted" : percent + "% looted";
                string timer = days <= 0 ? "no respawn" : "respawns in " + Math.Max(0, respawn[pair.Key]) + " h";
                Label(new Rect(bounds.x - 95, bounds.y + 36, 220, 22), state + " · " + pair.Value + "/" + total + " containers");
                Label(new Rect(bounds.x - 95, bounds.y + 56, 220, 22), timer + (zombies > 0 ? " · " + zombies + " zombie" + (zombies == 1 ? "" : "s") : ""));
            }
        }
        static GUIStyle style;
        static void Label(Rect at, string text)
        {
            if (style == null) style = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };
            var color = GUI.color; GUI.color = Color.black; GUI.Label(new Rect(at.x + 1, at.y + 1, at.width, at.height), text, style); GUI.color = color; GUI.Label(at, text, style);
        }
    }
    [HarmonyPatch(typeof(EntityPlayerLocal),"OnAddedToWorld")]
    public static class OpenWorld { public static void Postfix() { Runtime.Instance?.OpenWorld(); } }
    [HarmonyPatch(typeof(GameManager),"SaveAndCleanupWorld")]
    public static class CloseWorld { public static void Prefix() { Runtime.Instance?.CloseWorld(); } }
}
