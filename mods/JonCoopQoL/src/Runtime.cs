using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using UnityEngine;

namespace JonCoopQoL
{
    public sealed class ModApi : IModApi
    {
        public void InitMod(Mod mod)
        {
            const string key = "JonCoopQoL.Runtime";
            var existing = GameObject.Find(key);
            // Native boot and source hot reload take the same path. Destroying
            // the old component executes its own cleanup, including old delegates.
            if (existing != null) UnityEngine.Object.DestroyImmediate(existing);
            var gameObject = new GameObject(key);
            UnityEngine.Object.DontDestroyOnLoad(gameObject);
            gameObject.AddComponent<CoopRuntime>();
            new Harmony("JonCoopQoL").PatchAll(typeof(ModApi).Assembly);
            if (GameManager.Instance?.World?.GetPrimaryPlayer() != null) CoopRuntime.Instance.OpenWorld();
            CoopRuntime.Instance.BindExistingParty();
            Log.Out("[JonCoopQoL] Loot skulls, party portraits, follow, category routing, ground pings and team waypoints loaded.");
        }
    }

    public sealed class CoopRuntime : MonoBehaviour
    {
        public static CoopRuntime Instance;
        public readonly FollowState Follow = new FollowState();
        public readonly LootLedger Loot = new LootLedger();
        public GroundPings Pings;
        public TeamService Team;
        readonly Dictionary<XUiC_PartyEntry, Portrait> portraits = new Dictionary<XUiC_PartyEntry, Portrait>();
        readonly Dictionary<XUiController, XUiEvent_OnPressEventHandler> handlers = new Dictionary<XUiController, XUiEvent_OnPressEventHandler>();
        string saveFile, worldGuid;
        Texture2D skull;
        EntityPlayer menuTarget;
        Rect menuBounds;
        bool makingPortrait;
        sealed class Portrait
        {
            public int EntityId;
            public RenderTextureSystem Render;
            public XUiV_Texture View;
            public void Dispose() { if (View != null) View.Texture = null; Render?.Cleanup(); }
        }
        void Awake() { Instance = this; skull = MakeSkull(); Pings = new GroundPings(); Team = new TeamService(this); }
        void OnDestroy()
        {
            Team?.Dispose(); Pings?.Dispose();
            foreach (var pair in handlers) pair.Key.OnRightPress -= pair.Value;
            handlers.Clear();
            foreach (var portrait in portraits.Values) portrait.Dispose();
            portraits.Clear();
            Follow.Stop(null);
            if (skull != null) Destroy(skull);
            if (Instance == this) Instance = null;
        }
        public void BindExistingParty()
        {
            var player = GameManager.Instance?.World?.GetPrimaryPlayer();
            if (player?.playerUI?.xui == null) return;
            var list = player.playerUI.xui.GetWindow("windowGroupBars")?.Controller?.GetChildById("hud") as XUiC_PartyEntryList;
            if (list == null) return;
            foreach (var entry in list.entryList) { BindParty(entry); SetPortrait(entry); }
        }
        public void BindParty(XUiC_PartyEntry entry)
        {
            var button = entry.GetChildById("jonFollowTarget");
            if (button == null || handlers.ContainsKey(button)) return;
            XUiEvent_OnPressEventHandler handler = (sender, mouseButton) =>
            {
                var local = entry.xui.playerUI.entityPlayer;
                if (entry.Player == null || entry.Player == local || !local.windowManager.IsModalWindowOpen()) return;
                menuTarget = entry.Player;
                Vector3 mouse = Input.mousePosition;
                menuBounds = new Rect(Mathf.Clamp(mouse.x, 0, Screen.width - 230), Mathf.Clamp(Screen.height - mouse.y, 0, Screen.height - 100), 230, 100);
            };
            handlers.Add(button, handler);
            button.OnRightPress += handler;
        }
        public void SetPortrait(XUiC_PartyEntry entry)
        {
            if (makingPortrait) return;
            var view = entry.GetChildById("jonPortrait")?.ViewComponent as XUiV_Texture;
            if (view == null) return;
            if (portraits.TryGetValue(entry, out var old))
            {
                if (entry.Player != null && old.EntityId == entry.Player.entityId) return;
                old.Dispose(); portraits.Remove(entry);
            }
            if (entry.Player == null || !(entry.Player.emodel is EModelSDCS model)) return;
            RenderTextureSystem render = null;
            GameObject visual = null;
            try
            {
                makingPortrait = true;
                SDCSUtils.TransformCatalog catalog = null;
                SDCSUtils.CreateVizUI(model.Archetype, ref visual, ref catalog, entry.Player, false);
                if (visual == null) return;
                render = new RenderTextureSystem();
                render.Create("JonPortrait_" + entry.Player.entityId, visual, Vector3.zero, Vector3.zero, new Vector2i(128,128), true, false, 1);
                render.ParentGO.transform.localPosition = new Vector3(entry.Player.entityId * 50f, -10000, 0);
                Utils.SetLayerRecursively(visual, 11);
                visual.transform.localRotation = Quaternion.Euler(0,180,0);
                // Native character UI visualization: face and worn equipment,
                // without cloning gameplay scripts, colliders, or live entities.
                render.cam.transform.localPosition = new Vector3(0,1.55f,-1.15f);
                render.cam.transform.LookAt(render.ParentGO.transform.TransformPoint(new Vector3(0,1.55f,0)));
                render.cam.orthographic = true;
                render.cam.orthographicSize = 0.38f;
                render.cam.backgroundColor = new Color(0.06f,0.065f,0.08f,1);
                render.cam.enabled = false;
                render.LightGO.transform.localPosition = new Vector3(0,2,-1);
                render.cam.Render();
                view.Texture = render.RenderTex;
                portraits.Add(entry, new Portrait { EntityId=entry.Player.entityId, Render=render, View=view });
            }
            catch (Exception error)
            {
                if (render != null) render.Cleanup(); else if (visual != null) Destroy(visual);
                Log.Warning("[JonCoopQoL] Portrait: " + error.Message);
            }
            finally { makingPortrait = false; }
        }
        public void EquipmentChanged(EModelSDCS model)
        {
            if (makingPortrait) return;
            var local = GameManager.Instance?.World?.GetPrimaryPlayer();
            var list = local?.playerUI?.xui?.GetWindow("windowGroupBars")?.Controller?.GetChildById("hud") as XUiC_PartyEntryList;
            if (list == null) return;
            foreach (var entry in list.entryList)
                if (entry.Player?.emodel == model) { ClosePortrait(entry); SetPortrait(entry); }
        }
        public void ClosePortrait(XUiC_PartyEntry entry)
        {
            if (portraits.TryGetValue(entry,out var portrait)) { portrait.Dispose(); portraits.Remove(entry); }
        }
        public void OpenWorld()
        {
            var world = GameManager.Instance?.World;
            if (world == null || world.Guid == worldGuid) return;
            Follow.Stop(null); menuTarget = null; worldGuid = world.Guid;
            saveFile = Path.Combine(ConnectionManager.Instance.IsClient ? GameIO.GetSaveGameLocalDir() : GameIO.GetSaveGameDir(), "JonCoopQoL", "loot-skulls.xml");
            try { Loot.Load(saveFile); }
            catch (Exception error) { Loot.Records.Clear(); Log.Warning("[JonCoopQoL] Loot history: " + error.Message); }
            Team?.OpenWorld();
        }
        public void CloseWorld()
        {
            Team?.CloseWorld();
            Follow.Stop(null); menuTarget = null; Loot.Records.Clear(); saveFile = worldGuid = null;
            foreach (var portrait in portraits.Values) portrait.Dispose();
            portraits.Clear();
        }
        void SaveLoot()
        {
            if (saveFile == null) return;
            try { Loot.Save(saveFile); }
            catch (Exception error) { Log.Warning("[JonCoopQoL] Saving loot history: " + error.Message); }
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
        void OnGUI()
        {
            var player = GameManager.Instance?.World?.GetPrimaryPlayer();
            if (player == null || player.IsDead()) return;
            Pings.HandleInput(player, Team);
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
            if (!string.IsNullOrEmpty(Follow.Status))
            {
                GUI.Box(new Rect(12, Screen.height-135,360,35), Follow.Status);
                if (Follow.TargetId >= 0 && player.windowManager.IsModalWindowOpen() && GUI.Button(new Rect(12,Screen.height-98,130,28),"Stop following")) Follow.Stop("Follow stopped");
            }
            if (menuTarget == null) return;
            if (!player.windowManager.IsModalWindowOpen()) { menuTarget = null; return; }
            GUI.Box(menuBounds, menuTarget.PlayerDisplayName);
            if (GUI.Button(new Rect(menuBounds.x+8,menuBounds.y+29,214,28),"Follow (on foot / vehicle)"))
            { Follow.Start(menuTarget); menuTarget = null; }
            else if (GUI.Button(new Rect(menuBounds.x+8,menuBounds.y+61,214,28),"Cancel")) menuTarget = null;
        }
        void OnRenderObject() { Pings?.Render(GameManager.Instance?.World?.GetPrimaryPlayer()); }
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
    }
    [HarmonyPatch(typeof(EntityPlayerLocal),"OnAddedToWorld")]
    public static class OpenWorld { public static void Postfix() { CoopRuntime.Instance?.OpenWorld(); } }
    [HarmonyPatch(typeof(GameManager),"SaveAndCleanupWorld")]
    public static class CloseWorld { public static void Prefix() { CoopRuntime.Instance?.CloseWorld(); } }
    [HarmonyPatch(typeof(XUiC_PartyEntry),"Init")]
    public static class PartyInit { public static void Postfix(XUiC_PartyEntry __instance) { CoopRuntime.Instance?.BindParty(__instance); } }
    [HarmonyPatch(typeof(XUiC_PartyEntry),"SetPlayer")]
    public static class PartyPortrait { public static void Postfix(XUiC_PartyEntry __instance) { CoopRuntime.Instance?.SetPortrait(__instance); } }
    [HarmonyPatch(typeof(XUiC_PartyEntry),"OnOpen")]
    public static class PartyOpened { public static void Postfix(XUiC_PartyEntry __instance) { CoopRuntime.Instance?.SetPortrait(__instance); } }
    [HarmonyPatch(typeof(XUiC_PartyEntry),"OnClose")]
    public static class PartyClosed { public static void Postfix(XUiC_PartyEntry __instance) { CoopRuntime.Instance?.ClosePortrait(__instance); } }
    [HarmonyPatch(typeof(EModelSDCS),"UpdateEquipment")]
    public static class PortraitEquipmentChanged { public static void Postfix(EModelSDCS __instance) { CoopRuntime.Instance?.EquipmentChanged(__instance); } }
    [HarmonyPatch(typeof(XUiC_PartyEntry),"GetBindingValueInternal")]
    public static class PartyStaminaBinding
    {
        public static bool Prefix(XUiC_PartyEntry __instance, ref string __0, string __1, ref bool __result)
        {
            var stamina = __instance.Player?.Stats?.Stamina;
            switch (__1)
            {
                case "staminafill": __0 = (stamina?.ValuePercentUI ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture); break;
                case "staminamodifiedmax": __0 = (stamina == null || stamina.Max <= 0 ? 0 : stamina.ModifiedMax / stamina.Max).ToString(System.Globalization.CultureInfo.InvariantCulture); break;
                default: return true;
            }
            __result = true; return false;
        }
    }
}
