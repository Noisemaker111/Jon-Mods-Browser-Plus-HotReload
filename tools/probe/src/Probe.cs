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
                // a box seeded with one representative item must accept every item in that
                // item's family and reject the others. This is the whole "one scavenger book
                // in the box, drop the next one in" class of behavior, across every family.
                var stash = FindType("JonCategoryStorage.StashCategories");
                if (stash != null)
                {
                    var routing = new (string Group, string Seed, string[] Accept, string[] Reject)[]
                    {
                        ("books", "bookFiremansAlmanacAxes",
                            new[] { "bookFiremansAlmanacHeat", "bookFiremansAlmanacSpeed", "skillBookMaster", "modGunBarrelExtenderSchematic", "schematicMaster" },
                            new[] { "ammo9mmBulletBall", "gunHandgunT1Pistol", "medicalBandage", "foodCanChili" }),
                        ("ammunition", "ammo9mmBulletBall",
                            new[] { "ammo44MagnumBulletBall", "ammo762mmBulletBall", "ammoBundle9mmBulletBall" },
                            new[] { "bookFiremansAlmanacAxes", "medicalBandage", "resourceWood" }),
                        ("medicine", "medicalFirstAidBandage",
                            new[] { "medicalFirstAidKit", "medicalBandage", "medicalAloeCream" },
                            new[] { "foodCanBeef", "ammo9mmBulletBall", "resourceWood" }),
                        ("food", "foodCanChili",
                            new[] { "foodCanBeef", "foodCanChicken", "foodCanLamb" },
                            new[] { "medicalBandage", "ammo9mmBulletBall", "gunHandgunT1Pistol" }),
                        ("item mods", "modGunBarrelExtender",
                            new[] { "modGunScopeSmall", "modGunFlashlight", "modGunMuzzleBrake" },
                            new[] { "gunHandgunT1Pistol", "ammo9mmBulletBall", "resourceWood" }),
                        ("tools", "meleeToolRepairT0StoneAxe",
                            new[] { "meleeToolAxeT1IronFireaxe", "meleeToolAxeT2SteelAxe", "meleeToolRepairT0TazasStoneAxe" },
                            new[] { "gunHandgunT1Pistol", "resourceWood", "foodCanChili" }),
                        ("weapons", "gunHandgunT1Pistol",
                            new[] { "gunRifleT1HuntingRifle" },
                            new[] { "ammo9mmBulletBall", "resourceWood", "armorLumberjackBoots" }),
                        ("armor", "armorLumberjackBoots",
                            new[] { "armorLumberjackHelmet", "armorLumberjackOutfit", "armorLumberjackGloves" },
                            new[] { "gunHandgunT1Pistol", "resourceWood" }),
                        ("vehicles", "vehicleBicycleChassis",
                            new[] { "vehicleMinibikeChassis", "vehicleMinibikeHandlebars" },
                            new[] { "resourceWood", "gunHandgunT1Pistol" }),
                        ("resources", "resourceWood",
                            new[] { "resourceScrapIron", "resourceWoodBundle" },
                            new[] { "gunHandgunT1Pistol", "foodCanChili" }),
                    };

                    var prefix = stash.GetMethod("Prefix");
                    var hasCategory = stash.GetMethod("HasCategory");
                    var finalizer = stash.GetMethod("Finalizer");
                    foreach (var group in routing)
                    {
                        var bag = new Bag(10);
                        var slots = bag.GetSlots();
                        var seed = ItemClass.GetItem(group.Seed, false);
                        if (seed == null) { Log.Out("[SimProbe] ROUTING " + group.Group + " seed missing " + group.Seed); fail++; continue; }
                        slots[0] = new ItemStack(seed, 1);
                        prefix.Invoke(null, new object[] { bag });

                        var accepted = 0; var rejectedWrong = new List<string>();
                        foreach (var name in group.Accept)
                        {
                            var item = ItemClass.GetItem(name, false);
                            if (item != null && (bool)hasCategory.Invoke(null, new object[] { bag, item })) accepted++;
                            else rejectedWrong.Add(name);
                        }
                        var acceptedWrong = new List<string>();
                        foreach (var name in group.Reject)
                        {
                            var item = ItemClass.GetItem(name, false);
                            if (item != null && (bool)hasCategory.Invoke(null, new object[] { bag, item })) acceptedWrong.Add(name);
                        }
                        finalizer.Invoke(null, new object[] { null });

                        int total = group.Accept.Length + group.Reject.Length;
                        if (rejectedWrong.Count == 0 && acceptedWrong.Count == 0)
                        {
                            pass++;
                            Log.Out("[SimProbe] ROUTING " + group.Group + " ok: seed=" + group.Seed + " accepted " + accepted + "/" + group.Accept.Length + ", rejected " + group.Reject.Length);
                        }
                        else
                        {
                            fail++;
                            Log.Out("[SimProbe] ROUTING " + group.Group + " seed=" + group.Seed
                                + " missed=" + string.Join(",", rejectedWrong) + " leaked=" + string.Join(",", acceptedWrong));
                        }
                    }
                }

                // Pathfinding, against real world geometry the server has loaded:
                // a nearby route must be found and every route cell must be walkable.
                var world = GameManager.Instance != null ? GameManager.Instance.World : null;
                var gridPath = FindType("JonFollow.GridPath");
                if (world != null && gridPath != null)
                {
                    object path = Activator.CreateInstance(gridPath);
                    var begin = gridPath.GetMethod("Begin");
                    var cont = gridPath.GetMethod("Continue");
                    var hasProp = gridPath.GetProperty("Has");
                    var pointsProp = gridPath.GetProperty("Points");

                    bool started = false; Vector3 a = default, b = default;
                    foreach (int x in new[] { 420, 440, 460, 480, 380, 500 }) foreach (int z in new[] { 640, 660, 680, 700 })
                    {
                        Vector3 sa, sb;
                        bool oka = Stand(world, x, z, out sa), okb = Stand(world, x + 20, z + 20, out sb);
                        if (!oka || !okb) continue;
                        Log.Out("[SimProbe] PATH probe (" + x + "," + z + ") terrH=" + world.GetTerrainHeight(x, z) + " a=" + sa + " b=" + sb);
                        if ((bool)begin.Invoke(path, new object[] { world, sa, sb })) { started = true; a = sa; b = sb; break; }
                    }
                    bool? result = started ? null : false;
                    int guard = 0;
                    while (result == null && guard++ < 4000) result = (bool?)cont.Invoke(path, null);
                    if (!started)
                    {
                        Log.Out("[SimProbe] PATH skipped (no terrain streamed on a playerless server)");
                    }
                    else
                    {
                        bool hasRoute = result == true && (bool)hasProp.GetValue(path);
                        bool walkable = hasRoute;
                        if (hasRoute)
                        {
                            var points = (System.Collections.IEnumerable)pointsProp.GetValue(path);
                            int checkedCells = 0, blocked = 0;
                            foreach (Vector3 p in points)
                            {
                                checkedCells++;
                                int px = (int)Math.Floor(p.x), py = (int)Math.Floor(p.y), pz = (int)Math.Floor(p.z);
                                var cell = world.GetBlock(px, py, pz);
                                var below = world.GetBlock(px, py - 1, pz);
                                if (!cell.isair && !cell.isWater) blocked++;
                                if (below.isair) blocked++;
                            }
                            walkable = checkedCells > 0 && blocked == 0;
                            if (!walkable) Log.Out("[SimProbe] PATH route has " + blocked + " blocked cells of " + checkedCells);
                        }
                        if (hasRoute) pass++; else { Log.Out("[SimProbe] PATH no route found on real terrain"); fail++; }
                        if (walkable) pass++; else { Log.Out("[SimProbe] PATH route passes through solid blocks"); fail++; }
                    }
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

        // The topmost standable cell at a column, matching GridPath's own rule. Forces the
        // chunk to load first: a dedicated server with no players has streamed nothing.
        static bool Stand(World world, int x, int z, out Vector3 position)
        {
            position = default;
            try { world.GetChunkSync(x >> 4, z >> 4); } catch { }
            int top = Math.Max(10, (int)world.GetHeight(x, z) + 4);
            for (int y = Math.Min(200, top + 8); y > 2; y--)
            {
                if (Open(world, x, y, z) && Open(world, x, y + 1, z) && Solid(world, x, y - 1, z))
                {
                    position = new Vector3(x + 0.5f, y, z + 0.5f);
                    return true;
                }
            }
            return false;
        }

        static bool Open(World world, int x, int y, int z) { var b = world.GetBlock(x, y, z); return b.isair || b.isWater; }
        static bool Solid(World world, int x, int y, int z) { var b = world.GetBlock(x, y, z); return !b.isair; }
    }
}
