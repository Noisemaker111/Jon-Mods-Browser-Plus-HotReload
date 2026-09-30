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
        readonly Dictionary<XUiController, XUiEvent_OnPressEventHandler> handlers = new Dictionary<XUiController, XUiEvent_OnPressEventHandler>();
        EntityPlayer menuTarget;
        Rect menuBounds;
        void Awake() { Instance = this; }
        void OnDestroy() { foreach (var pair in handlers) pair.Key.OnRightPress -= pair.Value; handlers.Clear(); Follow.Stop(null); if (Instance == this) Instance = null; }
        public void CloseWorld() { Follow.Stop(null); menuTarget = null; }
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
            var button = entry.GetChildById("jonFollowTarget");
            if (button == null || handlers.ContainsKey(button)) return;
            button.ViewComponent.Size = entry.ViewComponent.Size;
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
        void OnGUI()
        {
            var player = GameManager.Instance?.World?.GetPrimaryPlayer();
            if (player == null || player.IsDead()) return;
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
    }
    [HarmonyPatch(typeof(XUiC_PartyEntry),"Init")]
    public static class PartyInit { public static void Postfix(XUiC_PartyEntry __instance) { Runtime.Instance?.BindParty(__instance); } }
    [HarmonyPatch(typeof(GameManager),"SaveAndCleanupWorld")]
    public static class CloseWorld { public static void Prefix() { Runtime.Instance?.CloseWorld(); } }
}
