// Event-based live-edit example. Replace the spawn behavior with your mod.
// Source-live: put this in <Mod>/src/ with ModInfo.xml; edits trigger compilation.
// The player entering the world is the event, with no periodic state checks.
using HarmonyLib;

namespace MySrcLiveMod
{
    [HarmonyPatch(typeof(EntityPlayerLocal), "OnAddedToWorld")]
    public static class PlayerEnteredWorld
    {
        [HarmonyPostfix]
        public static void Postfix(EntityPlayerLocal __instance)
        {
            Log.Out("[MySrcLiveMod] player entered the world at " + __instance.position);
        }
    }
}
