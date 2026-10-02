// In-engine probe. Runs inside the real 7 Days to Die engine (server or client) and
// asserts that each gameplay mod's real code produces the intended result against the
// engine's own loaded data. Results are written to the game log with a stable prefix so
// the headless runner can assert them.
//
// It is built and dropped into an isolated run by tools/headless.py; it never ships and
// is not part of any gameplay mod.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace SimProbe
{
    public sealed class ModApi : IModApi
    {
        public void InitMod(Mod mod)
        {
            ModEvents.GameStartDone.RegisterHandler(OnStartDone);
        }

        static void OnStartDone(ref ModEvents.SGameStartDoneData data)
        {
            try
            {
                int pass = 0, fail = 0;
                var expectations = new (string Name, string Bucket)[]
                {
                    ("gunHandgunT1Pistol", "06 Weapons"),
                    ("meleeToolRepairT0StoneAxe", "05 Tools"),
                    ("medicalFirstAidBandage", "02 Medicine"),
                    ("ammo9mmBulletBall", "04 Ammunition"),
                    ("foodCanChili", "03 Food and drink"),
                    ("resourceWood", "10 Resources"),
                    ("vehicleBicycleChassis", "09 Vehicles and fuel"),
                    ("armorLumberjackBoots", "07 Armor and clothing"),
                };

                var rules = FindType("JonCategoryStorage.Rules");
                var category = rules?.GetMethod("Category");
                Log.Out("[SimProbe] start rules=" + (category != null) + " items=" + ItemClass.ItemNames.Count);
                if (category == null) { Log.Out("[SimProbe] RESULT pass=0 fail=" + expectations.Length); return; }

                foreach (var expectation in expectations)
                {
                    var item = ItemClass.GetItemClass(expectation.Name, false);
                    if (item == null) { Log.Out("[SimProbe] MISS " + expectation.Name); fail++; continue; }
                    string actual;
                    try
                    {
                        var tag = new Func<string, bool>(t => item.HasAnyTags(FastTags<TagGroup.Global>.Parse(t)));
                        actual = (string)category.Invoke(null, new object[] { item.Name, item.Groups, tag, item.IsBlock() });
                    }
                    catch (Exception error)
                    {
                        Log.Out("[SimProbe] EXC " + expectation.Name + " " + (error.InnerException ?? error).Message);
                        fail++;
                        continue;
                    }
                    if (actual == expectation.Bucket) pass++;
                    else { Log.Out("[SimProbe] MISMATCH " + expectation.Name + " want=" + expectation.Bucket + " got=" + actual); fail++; }
                }

                // The real Harmony patch must be installed on the native sort method.
                var patched = Harmony.GetAllPatchedMethods()
                    .Any(m => m.DeclaringType?.Name == "StackSortUtil" && m.Name == "getGroup");
                if (patched) pass++; else { Log.Out("[SimProbe] UNPATCHED StackSortUtil.getGroup"); fail++; }

                // Container routing, with a real Bag and the mod's real StashItems decision:
                // one representative item must route its whole category and nothing else, and
                // a category added mid-transfer must not enable itself (snapshot semantics).
                var stash = FindType("JonCategoryStorage.StashCategories");
                if (stash != null)
                {
                    var bag = new Bag(8);
                    var slots = bag.GetSlots();
                    slots[0] = new ItemStack(ItemClass.GetItem("schematicMaster", false), 1);
                    var prefix = stash.GetMethod("Prefix");
                    var hasCategory = stash.GetMethod("HasCategory");
                    var finalizer = stash.GetMethod("Finalizer");
                    prefix.Invoke(null, new object[] { bag });
                    bool bookRoutes = (bool)hasCategory.Invoke(null, new object[] { bag, ItemClass.GetItem("modGunBarrelExtenderSchematic", false) });
                    bool pistolRejected = !(bool)hasCategory.Invoke(null, new object[] { bag, ItemClass.GetItem("gunHandgunT1Pistol", false) });
                    slots[1] = new ItemStack(ItemClass.GetItem("gunHandgunT1Pistol", false), 1);
                    bool snapshotHeld = !(bool)hasCategory.Invoke(null, new object[] { bag, ItemClass.GetItem("ammo9mmBulletBall", false) });
                    finalizer.Invoke(null, new object[] { null });

                    if (bookRoutes) pass++; else { Log.Out("[SimProbe] CONTAINER representative book did not route its category"); fail++; }
                    if (pistolRejected) pass++; else { Log.Out("[SimProbe] CONTAINER pistol wrongly routed into a book chest"); fail++; }
                    if (snapshotHeld) pass++; else { Log.Out("[SimProbe] CONTAINER a mid-transfer item enabled its own category"); fail++; }
                }

                Log.Out("[SimProbe] RESULT pass=" + pass + " fail=" + fail);
            }
            catch (Exception error)
            {
                Log.Out("[SimProbe] FATAL " + error);
            }
        }

        static Type FindType(string fullName)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var type = assembly.GetType(fullName, false);
                if (type != null) return type;
            }
            return null;
        }
    }
}
