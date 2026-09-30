using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using UnityEngine;

namespace JonSharedWaypoints
{
    public sealed class ModApi : IModApi
    {
        public void InitMod(Mod mod)
        {
            var old = GameObject.Find("JonSharedWaypoints.Runtime");
            if (old != null) UnityEngine.Object.DestroyImmediate(old);
            var go = new GameObject("JonSharedWaypoints.Runtime");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.AddComponent<Runtime>();
            var harmony = new Harmony("JonSharedWaypoints");
            harmony.UnpatchSelf(); harmony.PatchAll(typeof(ModApi).Assembly);
            if (GameManager.Instance?.World != null) Runtime.Instance.OpenWorld();
            Log.Out("[JonSharedWaypoints] Independent gameplay mod loaded (beta).");
        }
    }
    public sealed class Runtime : MonoBehaviour
    {
        public static Runtime Instance;
        public TeamService Team;
        void Awake() { Instance=this; Team = new TeamService(this); }
        void OnDestroy() { Team?.Dispose(); if (Instance==this) Instance=null; }
        public void OpenWorld() { Team?.OpenWorld(); }
        public void CloseWorld() { Team?.CloseWorld(); }
    }
    [HarmonyPatch(typeof(EntityPlayerLocal),"OnAddedToWorld")]
    public static class OpenWorld { public static void Postfix() { Runtime.Instance?.OpenWorld(); } }
    [HarmonyPatch(typeof(GameManager),"SaveAndCleanupWorld")]
    public static class CloseWorld { public static void Prefix() { Runtime.Instance?.CloseWorld(); } }
}
