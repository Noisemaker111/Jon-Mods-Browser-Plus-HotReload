using HarmonyLib;

namespace JonCoopQoL
{
    [HarmonyPatch(typeof(XUiC_LootWindow), "OnClose")]
    public static class LootClosed
    {
        public static void Prefix(XUiC_LootWindow __instance, out ITileEntityLootable __state) { __state = __instance.te; }
        public static void Postfix(ITileEntityLootable __state) { CoopRuntime.Instance?.ObserveLoot(__state); }
    }
    // Native storage read is also the client's chunk/network replication boundary.
    [HarmonyPatch(typeof(TEFeatureStorage), "Read")]
    public static class LootReplicated
    {
        public static void Postfix(TEFeatureStorage __instance) { CoopRuntime.Instance?.ObserveLoot(__instance); }
    }
    [HarmonyPatch(typeof(TEFeatureStorage), "UpdateTick")]
    public static class LootClockChanged
    {
        public struct Before { public ulong Time; public bool Touched; }
        public static void Prefix(TEFeatureStorage __instance, out Before __state)
        { __state = new Before { Time = __instance.worldTimeTouched, Touched = __instance.bTouched }; }
        public static void Postfix(TEFeatureStorage __instance, Before __state)
        {
            // Native nearby-player deferral extends the real respawn clock.
            if (__state.Time != __instance.worldTimeTouched || __state.Touched != __instance.bTouched) CoopRuntime.Instance?.ObserveLoot(__instance);
        }
    }
    [HarmonyPatch(typeof(TEFeatureStorage), "OnUnlockedServer")]
    public static class SharedLootClosed
    {
        public static void Prefix(TEFeatureStorage __instance) { CoopRuntime.Instance?.ObserveLoot(__instance); }
    }
    [HarmonyPatch(typeof(PrefabInstance), "ResetBlocksAndRebuild")]
    public static class QuestPoiReset
    {
        public static void Prefix(PrefabInstance __instance) { CoopRuntime.Instance?.ClearPoi(__instance.id); }
    }
}
