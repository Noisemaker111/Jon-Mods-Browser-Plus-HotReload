// DemoHotpack.cs - example hot pack: live-editable Harmony patch.
// Lives in <moddir>/hotpacks/DemoHotpack.dll and can be rebuilt + reloaded
// while the game runs ('hr code' in the F1 console, or auto with watch on).
//
// This one patches EntityPlayerLocal.OnUpdateEntity (called every frame) and
// logs a heartbeat once every 5 seconds with the player's current position.
// Change the message / logic, rebuild, run 'hr code', and the new behavior
// is live with no restart.

using System;
using HarmonyLib;
using UnityEngine;

namespace DemoHotpack
{
    [HarmonyPatch(typeof(EntityPlayerLocal), "OnUpdateEntity")]
    public static class Patch_Heartbeat
    {
        static float _nextLog;

        [HarmonyPostfix]
        public static void Postfix(EntityPlayerLocal __instance)
        {
            try
            {
                if (Time.unscaledTime < _nextLog) return;
                _nextLog = Time.unscaledTime + 5f;
                var p = __instance.position;
                Log.Out("[DemoHotpack] alive @ " + p.x.ToString("0") + "," + p.y.ToString("0") + "," + p.z.ToString("0") + "  (edit me and run 'hr code')");
            }
            catch { }
        }
    }
}
