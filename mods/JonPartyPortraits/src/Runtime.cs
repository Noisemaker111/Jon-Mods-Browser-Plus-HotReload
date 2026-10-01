using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using UnityEngine;

namespace JonPartyPortraits
{
    public sealed class ModApi : IModApi
    {
        public void InitMod(Mod mod)
        {
            var old = GameObject.Find("JonPartyPortraits.Runtime");
            if (old != null) UnityEngine.Object.DestroyImmediate(old);
            var go = new GameObject("JonPartyPortraits.Runtime");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.AddComponent<Runtime>();
            var harmony = new Harmony("JonPartyPortraits");
            harmony.UnpatchSelf(); harmony.PatchAll(typeof(ModApi).Assembly);
            Runtime.Instance.BindExistingParty();
            Log.Out("[JonPartyPortraits] Independent gameplay mod loaded (beta).");
        }
    }
    public sealed class Runtime : MonoBehaviour
    {
        public static Runtime Instance;
        readonly Dictionary<XUiC_PartyEntry, Portrait> portraits = new Dictionary<XUiC_PartyEntry, Portrait>();
        bool makingPortrait;
        void Awake() { Instance = this; }
        void OnDestroy() { foreach (var portrait in portraits.Values) portrait.Dispose(); portraits.Clear(); if (Instance == this) Instance = null; }
        public void CloseWorld() { foreach (var portrait in portraits.Values) portrait.Dispose(); portraits.Clear(); }
        sealed class Portrait
        {
            public int EntityId;
            public RenderTextureSystem Render;
            public XUiV_Texture View;
            public void Dispose() { if (View != null) View.Texture = null; Render?.Cleanup(); }
        }
        public void BindExistingParty()
        {
            var player = GameManager.Instance?.World?.GetPrimaryPlayer();
            if (player?.playerUI?.xui == null) return;
            var list = player.playerUI.xui.GetWindow("windowGroupBars")?.Controller?.GetChildById("hud") as XUiC_PartyEntryList;
            if (list == null) return;
            foreach (var entry in list.entryList) { SetPortrait(entry); }
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
                // Its menu animator is stepped to the idle pose before the
                // single render; otherwise the rig is captured in its T-pose.
                var animator = visual.GetComponentInChildren<Animator>();
                if (animator != null)
                {
                    animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                    for (int i = 0; i < 12; i++) animator.Update(0.2f);
                }
                // Head-and-shoulders framing on the actual head bone.
                var parent = render.ParentGO.transform;
                var head = FindBone(visual.transform, "head");
                Vector3 face = head != null ? parent.InverseTransformPoint(head.position) + new Vector3(0, 0.03f, 0) : new Vector3(0, 1.6f, 0);
                render.cam.transform.localPosition = face + new Vector3(0, 0.02f, -1.2f);
                render.cam.transform.LookAt(parent.TransformPoint(face));
                render.cam.orthographic = true;
                render.cam.orthographicSize = 0.17f;
                render.cam.backgroundColor = new Color(0.06f,0.065f,0.08f,1);
                render.cam.enabled = false;
                // Key light from the front-left of the face.
                render.LightGO.transform.localPosition = face + new Vector3(-0.35f, 0.3f, -0.9f);
                render.LightGO.transform.LookAt(parent.TransformPoint(face));
                // The native helper sizes the light's range from a target offset
                // of zero, which leaves the face unlit.
                var light = render.LightGO.GetComponent<Light>();
                if (light != null) { light.range = 6f; light.intensity = 2.2f; }
                render.cam.Render();
                view.Texture = render.RenderTex;
                portraits.Add(entry, new Portrait { EntityId=entry.Player.entityId, Render=render, View=view });
            }
            catch (Exception error)
            {
                if (render != null) render.Cleanup(); else if (visual != null) Destroy(visual);
                Log.Warning("[JonPartyPortraits] Portrait: " + error.Message);
            }
            finally { makingPortrait = false; }
        }
        static Transform FindBone(Transform root, string name)
        {
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
                if (string.Equals(t.name, name, StringComparison.OrdinalIgnoreCase)) return t;
            return null;
        }
        // Remembers when each party member last gained a level, for the
        // "LEVEL n!" highlight on their entry.
        readonly Dictionary<int, int> knownLevels = new Dictionary<int, int>();
        readonly Dictionary<int, float> levelUpUntil = new Dictionary<int, float>();
        public bool LevelledUp(EntityPlayer player)
        {
            int level = player.Progression?.Level ?? 0;
            if (knownLevels.TryGetValue(player.entityId, out int known) && level > known) levelUpUntil[player.entityId] = Time.time + 8f;
            knownLevels[player.entityId] = level;
            return levelUpUntil.TryGetValue(player.entityId, out float until) && Time.time < until;
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
    }
    [HarmonyPatch(typeof(GameManager),"SaveAndCleanupWorld")]
    public static class CloseWorld { public static void Prefix() { Runtime.Instance?.CloseWorld(); } }
    [HarmonyPatch(typeof(XUiC_PartyEntry),"SetPlayer")]
    public static class PartyPortrait { public static void Postfix(XUiC_PartyEntry __instance) { Runtime.Instance?.SetPortrait(__instance); } }
    [HarmonyPatch(typeof(XUiC_PartyEntry),"OnOpen")]
    public static class PartyOpened { public static void Postfix(XUiC_PartyEntry __instance) { Runtime.Instance?.SetPortrait(__instance); } }
    [HarmonyPatch(typeof(XUiC_PartyEntry),"OnClose")]
    public static class PartyClosed { public static void Postfix(XUiC_PartyEntry __instance) { Runtime.Instance?.ClosePortrait(__instance); } }
    [HarmonyPatch(typeof(EModelSDCS),"UpdateEquipment")]
    public static class PortraitEquipmentChanged { public static void Postfix(EModelSDCS __instance) { Runtime.Instance?.EquipmentChanged(__instance); } }
    // The native player-stats package already carries level and XP (and the
    // full progression with death penalty and skill points), but the game
    // only sends it when its stats-changed flag is set, which XP gain does not
    // do. Flag it on the change itself so party members see it at once.
    [HarmonyPatch]
    public static class ShareProgression
    {
        static System.Collections.Generic.IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(Progression), "AddLevelExp");
            yield return AccessTools.Method(typeof(Progression), "AddXPDeficit");
            yield return AccessTools.Method(typeof(Progression), "SpendSkillPoints");
            yield return AccessTools.Method(typeof(Progression), "OnRespawnFromDeath");
        }
        public struct Before { public int Level, Points, Deficit; }
        public static void Prefix(Progression __instance, out Before __state)
        { __state = new Before { Level = __instance.Level, Points = __instance.SkillPoints, Deficit = __instance.ExpDeficit }; }
        public static void Postfix(Progression __instance, Before __state)
        {
            if (!(__instance.parent is EntityPlayerLocal player)) return;
            player.bPlayerStatsChanged = true;
            if (__instance.Level != __state.Level || __instance.SkillPoints != __state.Points || __instance.ExpDeficit != __state.Deficit)
                __instance.bProgressionStatsChanged = true;
        }
    }
    // The entry redraws only when health or distance changes; level, XP,
    // death penalty, skill points and the level-up highlight count too.
    [HarmonyPatch(typeof(XUiC_PartyEntry),"HasChanged")]
    public static class ProgressionRedraw
    {
        public sealed class Seen { public long Signature; public bool Known; }
        static readonly System.Runtime.CompilerServices.ConditionalWeakTable<XUiC_PartyEntry, Seen> seen = new System.Runtime.CompilerServices.ConditionalWeakTable<XUiC_PartyEntry, Seen>();
        public static void Postfix(XUiC_PartyEntry __instance, ref bool __result)
        {
            var player = __instance.Player;
            var g = player?.Progression;
            if (g == null) return;
            bool up = Runtime.Instance != null && Runtime.Instance.LevelledUp(player);
            long signature = ((long)g.Level << 48) ^ ((long)g.ExpToNextLevel << 20) ^ ((long)g.ExpDeficit << 4) ^ ((long)g.SkillPoints << 36) ^ (up ? 1 : 0);
            var last = seen.GetOrCreateValue(__instance);
            if (!last.Known || last.Signature != signature) { last.Known = true; last.Signature = signature; __result = true; }
        }
    }
    [HarmonyPatch(typeof(XUiC_PartyEntry),"GetBindingValueInternal")]
    public static class PartyStaminaBinding
    {
        public static bool Prefix(XUiC_PartyEntry __instance, ref string __0, string __1, ref bool __result)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var player = __instance.Player;
            var stamina = player?.Stats?.Stamina;
            // Level and XP replicate with each player's own stats; the death
            // penalty (XP that must be earned back first) shows in red.
            var progression = player?.Progression;
            int need = progression == null ? 1 : Math.Max(1, progression.GetExpForNextLevel());
            float xp = progression == null ? 0 : Mathf.Clamp01(progression.GetLevelProgressPercentage());
            float debt = progression == null ? 0 : Mathf.Clamp01(progression.ExpDeficit / (float)need);
            switch (__1)
            {
                case "staminafill": __0 = (stamina?.ValuePercentUI ?? 0).ToString(inv); break;
                case "staminamodifiedmax": __0 = (stamina == null || stamina.Max <= 0 ? 0 : stamina.ModifiedMax / stamina.Max).ToString(inv); break;
                case "jonxpfill": __0 = xp.ToString(inv); break;
                case "jondeficitfill": __0 = Mathf.Clamp01(xp + debt).ToString(inv); break;
                case "jonlevel":
                    bool up = player != null && Runtime.Instance != null && Runtime.Instance.LevelledUp(player);
                    __0 = progression == null ? "" : (up ? "[ffd43b]LEVEL " + progression.Level + "![-]" : "Lv " + progression.Level) +
                        (progression.SkillPoints > 0 ? " [ffd43b]+" + progression.SkillPoints + "[-]" : "");
                    break;
                default: return true;
            }
            __result = true; return false;
        }
    }
}
