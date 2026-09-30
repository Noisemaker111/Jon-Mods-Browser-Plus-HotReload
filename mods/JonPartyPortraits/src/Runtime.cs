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
                Log.Warning("[JonPartyPortraits] Portrait: " + error.Message);
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
