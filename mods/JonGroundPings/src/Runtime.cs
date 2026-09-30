using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using UnityEngine;

namespace JonGroundPings
{
    public sealed class ModApi : IModApi
    {
        public void InitMod(Mod mod)
        {
            var old = GameObject.Find("JonGroundPings.Runtime");
            if (old != null) UnityEngine.Object.DestroyImmediate(old);
            var go = new GameObject("JonGroundPings.Runtime");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.AddComponent<Runtime>();
            var harmony = new Harmony("JonGroundPings");
            harmony.UnpatchSelf(); harmony.PatchAll(typeof(ModApi).Assembly);
            if (GameManager.Instance?.World != null) Runtime.Instance.OpenWorld();
            Log.Out("[JonGroundPings] Independent gameplay mod loaded (beta).");
        }
    }
    public sealed class Runtime : MonoBehaviour
    {
        public static Runtime Instance;
        public TeamService Team;
        public GroundPings Pings;
        void Awake() { Instance=this; Pings = new GroundPings(); Team = new TeamService(this); }
        void OnDestroy() { Team?.Dispose(); Pings?.Dispose(); if (Instance==this) Instance=null; }
        public void OpenWorld() { Team?.OpenWorld(); }
        public void CloseWorld() { Team?.CloseWorld(); }
        void OnGUI() { var player=GameManager.Instance?.World?.GetPrimaryPlayer(); if (player!=null && !player.IsDead()) Pings.HandleInput(player,Team); }
        void OnRenderObject() { Pings?.Render(GameManager.Instance?.World?.GetPrimaryPlayer()); }
    }
    [HarmonyPatch(typeof(EntityPlayerLocal),"OnAddedToWorld")]
    public static class OpenWorld { public static void Postfix() { Runtime.Instance?.OpenWorld(); } }
    [HarmonyPatch(typeof(GameManager),"SaveAndCleanupWorld")]
    public static class CloseWorld { public static void Prefix() { Runtime.Instance?.CloseWorld(); } }
}
