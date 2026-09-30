using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using UnityEngine;

namespace JonCategoryStorage
{
    public sealed class ModApi : IModApi
    {
        public void InitMod(Mod mod)
        {
            var harmony = new Harmony("JonCategoryStorage");
            harmony.UnpatchSelf(); harmony.PatchAll(typeof(ModApi).Assembly);
            Log.Out("[JonCategoryStorage] Category sorting and chest routing loaded (beta).");
        }
    }
}
