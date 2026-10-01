using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using UnityEngine;

namespace JonFollow
{
    public sealed class ModApi : IModApi
    {
        public void InitMod(Mod mod)
        {
            var old = GameObject.Find("JonFollow.Runtime");
            if (old != null) UnityEngine.Object.DestroyImmediate(old);
            var go = new GameObject("JonFollow.Runtime");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.AddComponent<Runtime>();
            var harmony = new Harmony("JonFollow");
            harmony.UnpatchSelf(); harmony.PatchAll(typeof(ModApi).Assembly);
            Runtime.Instance.BindExistingParty();
            Log.Out("[JonFollow] Independent gameplay mod loaded (beta).");
        }
    }
    public sealed class Runtime : MonoBehaviour
    {
        public static Runtime Instance;
        public readonly FollowState Follow = new FollowState();
        // The party HUD sits below the open inventory's input layer, so its
        // widgets never receive clicks; right-clicks are hit-tested here.
        readonly List<XUiC_PartyEntry> entries = new List<XUiC_PartyEntry>();
        EntityPlayer menuTarget;
        Rect menuBounds;
        void Awake() { Instance = this; }
        void OnDestroy() { entries.Clear(); Follow.Stop(null); if (Instance == this) Instance = null; }
        public void CloseWorld() { Follow.Stop(null); menuTarget = null; entries.Clear(); }
        public void BindExistingParty()
        {
            var player = GameManager.Instance?.World?.GetPrimaryPlayer();
            if (player?.playerUI?.xui == null) return;
            var list = player.playerUI.xui.GetWindow("windowGroupBars")?.Controller?.GetChildById("hud") as XUiC_PartyEntryList;
            if (list == null) return;
            foreach (var entry in list.entryList) { BindParty(entry); }
        }
        public void BindParty(XUiC_PartyEntry entry)
        {
            if (!entries.Contains(entry)) entries.Add(entry);
        }
        static bool UnderMouse(XUiC_PartyEntry entry, Vector3 mouse)
        {
            var view = entry.ViewComponent;
            var camera = entry.xui?.playerUI?.camera;
            if (view?.UiTransform == null || camera == null || !view.UiTransform.gameObject.activeInHierarchy) return false;
            var corners = view.WorldCorners;
            Vector3 min = camera.WorldToScreenPoint(corners[0]), max = min;
            for (int i = 1; i < corners.Length; i++) { var p = camera.WorldToScreenPoint(corners[i]); min = Vector3.Min(min, p); max = Vector3.Max(max, p); }
            return mouse.x >= min.x && mouse.x <= max.x && mouse.y >= min.y && mouse.y <= max.y;
        }
        void OpenMenu(EntityPlayerLocal player)
        {
            var e = Event.current;
            if (e.type != EventType.MouseDown || e.button != 1 || !player.windowManager.IsModalWindowOpen()) return;
            Vector3 mouse = Input.mousePosition;
            entries.RemoveAll(entry => entry == null || entry.ViewComponent == null);
            foreach (var entry in entries)
            {
                if (entry.Player == null || entry.Player == player || !UnderMouse(entry, mouse)) continue;
                menuTarget = entry.Player;
                menuBounds = new Rect(Mathf.Clamp(mouse.x, 0, Screen.width - 230), Mathf.Clamp(Screen.height - mouse.y, 0, Screen.height - 100), 230, 100);
                e.Use();
                return;
            }
        }
        void OnGUI()
        {
            var player = GameManager.Instance?.World?.GetPrimaryPlayer();
            if (player == null || player.IsDead()) return;
            OpenMenu(player);
            if (!string.IsNullOrEmpty(Follow.Status))
            {
                // Above the native notification icons, clear of the inventory windows.
                float top = Screen.height * 0.70f;
                GUI.Box(new Rect(12, top, 300, 35), Follow.Status);
                if (Follow.TargetId >= 0 && player.windowManager.IsModalWindowOpen() && GUI.Button(new Rect(12, top + 38, 130, 28), "Stop following")) Follow.Stop("Follow stopped");
            }
            if (menuTarget == null) return;
            if (!player.windowManager.IsModalWindowOpen()) { menuTarget = null; return; }
            GUI.Box(menuBounds, menuTarget.PlayerDisplayName);
            float away = Vector3.Distance(player.position, menuTarget.position);
            if (away > Rules.StartRange) GUI.Label(new Rect(menuBounds.x+12,menuBounds.y+33,210,24), "Get within " + Rules.StartRange + " m to follow (" + Mathf.RoundToInt(away) + " m)");
            else if (GUI.Button(new Rect(menuBounds.x+8,menuBounds.y+29,214,28),"Follow (on foot / vehicle)"))
            { Follow.Start(menuTarget); menuTarget = null; }
            else if (GUI.Button(new Rect(menuBounds.x+8,menuBounds.y+61,214,28),"Cancel")) menuTarget = null;
        }
    }
    [HarmonyPatch(typeof(XUiC_PartyEntry),"Init")]
    public static class PartyInit { public static void Postfix(XUiC_PartyEntry __instance) { Runtime.Instance?.BindParty(__instance); } }
    [HarmonyPatch(typeof(GameManager),"SaveAndCleanupWorld")]
    public static class CloseWorld { public static void Prefix() { Runtime.Instance?.CloseWorld(); } }
}
