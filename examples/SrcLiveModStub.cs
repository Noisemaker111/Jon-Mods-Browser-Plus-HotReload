// SrcLiveModStub.cs - optional template for a src-live mod.
// Copy this file into YOUR mod's src\ folder, edit, save, and the tool
// recompiles + hot-swaps it while you play. No build step.
//
// A src-live mod needs: <ModFolder>\ModInfo.xml + <ModFolder>\src\*.cs

using System;
using HarmonyLib;
using UnityEngine;

namespace MySrcLiveMod
{
    // This patches EntityPlayerLocal.OnUpdateEntity (called every frame) and
    // logs a heartbeat every 10 seconds. Change it and save.
    [HarmonyPatch(typeof(EntityPlayerLocal), "OnUpdateEntity")]
    public static class Patch_Heartbeat
    {
        static float _next;

        [HarmonyPostfix]
        public static void Postfix(EntityPlayerLocal __instance)
        {
            try
            {
                if (Time.unscaledTime < _next) return;
                _next = Time.unscaledTime + 10f;
                var p = __instance.position;
                Log.Out("[MySrcLiveMod] live @ " + p.x.ToString("0") + "," + p.y.ToString("0") + "," + p.z.ToString("0"));
            }
            catch { }
        }
    }
}
