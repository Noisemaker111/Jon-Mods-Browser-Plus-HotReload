// Headless 7 Days to Die mod harness.
//
// Boots the installed game's real assemblies (Assembly-CSharp and friends) inside
// the .NET 10 host that ships with PowerShell 7, builds a real ItemClass registry
// straight from the game's own Data/Config XML, loads each built mod DLL, and runs
// the mod's real code against that real data. No game launch and no Unity scene.
//
// This is not a reimplementation of game rules: the mod's own assemblies and the
// game's own types do the work. It catches type, identity and argument errors, null
// and exception paths, and category regressions that are otherwise only found by
// booting the game.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml;

public static class GameSim
{
    static string gamePath;
    static Assembly game;
    static readonly List<string> failures = new List<string>();
    static int checks;

    static readonly List<ItemClass> defs = new List<ItemClass>();
    static readonly Dictionary<string, ItemClass> byName = new Dictionary<string, ItemClass>(StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<string, ItemClass> modifiers = new Dictionary<string, ItemClass>(StringComparer.OrdinalIgnoreCase);

    public static int Main(string[] args)
    {
        try
        {
            gamePath = args[0];
            var modDlls = args.Skip(1).ToArray();
            var managed = Path.Combine(gamePath, "7DaysToDie_Data", "Managed");
            AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
            {
                var name = new AssemblyName(e.Name).Name;
                foreach (var baseDir in new[] { Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), managed })
                {
                    var path = Path.Combine(baseDir, name + ".dll");
                    if (File.Exists(path)) return Assembly.LoadFrom(path);
                }
                return null;
            };

            var timer = Stopwatch.StartNew();
            game = Assembly.LoadFrom(Path.Combine(managed, "Assembly-CSharp.dll"));
            LoadDefinitions();
            var loadMs = timer.ElapsedMilliseconds;

            Console.WriteLine("game assembly : Assembly-CSharp (real installed game)");
            Console.WriteLine("definitions   : " + defs.Count + " items, " + modifiers.Count + " item modifiers (" + loadMs + " ms)");
            Console.WriteLine("mods          : " + string.Join(", ", modDlls.Select(Path.GetFileName)));
            Console.WriteLine();

            foreach (var dll in modDlls) RunMod(dll);

            Console.WriteLine();
            if (failures.Count == 0)
            {
                Console.WriteLine("ALL " + checks + " HEADLESS CHECKS PASSED (" + timer.ElapsedMilliseconds + " ms, no game launch)");
                return 0;
            }
            Console.Error.WriteLine(failures.Count + " FAILURE(S):");
            foreach (var failure in failures) Console.Error.WriteLine("  " + failure);
            return 1;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }

    static void RunMod(string dll)
    {
        Console.WriteLine("== " + Path.GetFileNameWithoutExtension(dll) + " ==");
        Assembly mod;
        try { mod = Assembly.LoadFrom(dll); }
        catch (Exception e) { Fail(Path.GetFileName(dll) + ": assembly did not load: " + Root(e).Message); return; }

        var categories = mod.GetType("JonCategoryStorage.Categories");
        if (categories == null) { Console.WriteLine("  (no known surface in this mod)"); Console.WriteLine(); return; }

        var of = categories.GetMethod("Of", new[] { typeof(ItemClass) });
        Check(of != null, "real Categories.Of(ItemClass) resolves against the built mod");
        if (of == null) return;

        // 1. Every native definition classifies without throwing.
        var buckets = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var exceptions = 0;
        foreach (var def in defs.Concat(modifiers.Values))
        {
            try
            {
                var value = (string)of.Invoke(null, new object[] { def });
                if (string.IsNullOrEmpty(value)) throw new Exception("returned an empty category");
                buckets.TryGetValue(value, out var count);
                buckets[value] = count + 1;
            }
            catch (Exception e)
            {
                exceptions++;
                if (exceptions <= 5) Fail("classify " + def.Name + ": " + Root(e).Message);
            }
        }
        Check(exceptions == 0, "all " + (defs.Count + modifiers.Count) + " real definitions classify without an exception");
        Console.WriteLine("  buckets: " + string.Join(", ", buckets.Select(kv => kv.Key + "=" + kv.Value)));

        // 2. Known definitions land in the intended destination: the exact cases
        //    that until now required booting the game to trust.
        var expectations = new List<(string Name, string Expected)>
        {
            ("gunHandgunT1Pistol", "06 Weapons"),
            ("meleeToolRepairT0StoneAxe", "05 Tools"),
            ("medicalFirstAidBandage", "02 Medicine"),
            ("ammo9mmBulletBall", "04 Ammunition"),
            ("foodCanChili", "03 Food and drink"),
            ("resourceWood", "10 Resources"),
            ("vehicleBicycleChassis", "09 Vehicles and fuel"),
            ("armorLumberjackBoots", "07 Armor and clothing"),
            ("modGunBarrelExtender", "08 Item mods")
        };
        foreach (var (name, expected) in expectations)
        {
            var def = Resolve(name);
            if (def == null) { Fail("native definition missing: " + name); continue; }
            var actual = (string)of.Invoke(null, new object[] { def });
            Check(actual == expected, name + " -> " + expected + " (was " + actual + ")");
        }

        // The whole native reading set (perk books, magazines, schematics) must
        // land in one destination, derived from real data rather than a name list.
        var reading = defs.Where(d => d.Name.EndsWith("Schematic", StringComparison.OrdinalIgnoreCase)
            || d.Name.EndsWith("SkillMagazine", StringComparison.OrdinalIgnoreCase)).ToList();
        Check(reading.Count > 50 && reading.All(d => (string)of.Invoke(null, new object[] { d }) == "01 Books"),
            "all " + reading.Count + " native schematics and skill magazines route to 01 Books");

        // 3. Families must not bleed into one another.
        var families = expectations.Take(8).Select(x => x.Name).ToArray();
        var distinct = families.Select(n => (string)of.Invoke(null, new object[] { byName[n] })).Distinct().Count();
        Check(distinct == families.Length, families.Length + " item families never share a storage destination");

        // 4. The real Harmony patch body, driven with real ItemStack objects.
        var sort = mod.GetType("JonCategoryStorage.SortCategory");
        if (sort != null)
        {
            var prefix = sort.GetMethod("Prefix");
            var itemValueType = game.GetType("ItemValue");
            var stackType = game.GetType("ItemStack");
            var typeField = itemValueType.GetField("type", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            foreach (var name in new[] { "gunHandgunT1Pistol", "resourceWood" })
            {
                var itemValue = Activator.CreateInstance(itemValueType);
                typeField.SetValue(itemValue, byName[name].Id);
                var stack = Activator.CreateInstance(stackType, new object[] { itemValue, 5 });
                var callArgs = new object[] { stack, null };
                prefix.Invoke(null, callArgs);
                var viaPatch = (string)callArgs[1];
                var direct = (string)of.Invoke(null, new object[] { byName[name] });
                Check(viaPatch == direct, "SortCategory.Prefix on a real ItemStack matches Categories.Of for " + name);
            }
            var emptyArgs = new object[] { Activator.CreateInstance(stackType), null };
            prefix.Invoke(null, emptyArgs);
            Check((string)emptyArgs[1] == "zzzzz", "SortCategory.Prefix keeps empty stacks last");
        }

        // 5. Gameplay assemblies must not drag the manager or each other in.
        Check(!mod.GetReferencedAssemblies().Any(r => r.Name == "HotReloadTool" || r.Name == "JonCoopQoL"),
            mod.GetName().Name + " has no manager or combined-mod dependency");
        Console.WriteLine();
    }

    static ItemClass Resolve(string name) =>
        byName.TryGetValue(name, out var item) ? item : (modifiers.TryGetValue(name, out var mod) ? mod : null);

    static void LoadDefinitions()
    {
        var config = Path.Combine(gamePath, "Data", "Config");
        LoadXml(Path.Combine(config, "items.xml"), "items/item", false);
        LoadXml(Path.Combine(config, "item_modifiers.xml"), "item_modifiers/item_modifier", true);
        // The native ItemClass.list is normally filled by ItemClassesFromXml. Fill it
        // the same way so ItemValue-based code paths resolve real types.
        var list = Array.CreateInstance(game.GetType("ItemClass"), defs.Count + modifiers.Count + 1);
        int id = 1;
        foreach (var def in defs.Concat(modifiers.Values))
        {
            def.SetId(id);
            list.SetValue(def, id);
            id++;
        }
        game.GetType("ItemClass").GetField("list", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static).SetValue(null, list);
    }

    static void LoadXml(string path, string xpath, bool modifier)
    {
        var doc = new XmlDocument { XmlResolver = null };
        doc.Load(path);
        var type = game.GetType(modifier ? "ItemClassModifier" : "ItemClass");
        foreach (XmlElement element in doc.SelectNodes("/" + xpath))
        {
            var name = element.GetAttribute("name");
            if (string.IsNullOrEmpty(name) || !modifier && byName.ContainsKey(name)) continue;
            var instance = (ItemClass)Activator.CreateInstance(type);
            SetMember(instance, "Name", name);
            SetMember(instance, "Groups", Split(Property(element, "Group")));
            var tags = Property(element, "Tags");
            if (!string.IsNullOrEmpty(tags)) SetMember(instance, "ItemTags", FastTags<TagGroup.Global>.Parse(tags));
            if (modifier) modifiers[name] = instance;
            else { defs.Add(instance); byName[name] = instance; }
        }
    }

    static string Property(XmlElement element, string property)
    {
        var own = element.SelectSingleNode("property[@name='" + property + "']/@value");
        if (own != null) return own.Value;
        var parent = element.SelectSingleNode("property[@name='Extends']/@value");
        if (parent == null) return "";
        var doc = element.OwnerDocument;
        var parentNode = doc.SelectSingleNode("//item[@name='" + parent.Value + "']") as XmlElement
            ?? doc.SelectSingleNode("//item_modifier[@name='" + parent.Value + "']") as XmlElement;
        return parentNode == null ? "" : Property(parentNode, property);
    }

    static string[] Split(string value) =>
        string.IsNullOrEmpty(value) ? new string[0] : value.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);

    static void SetMember(object target, string member, object value)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        for (var type = target.GetType(); type != null; type = type.BaseType)
            foreach (var candidate in new[] { member, "p" + member })
            {
                var property = type.GetProperty(candidate, flags);
                if (property != null && property.CanWrite) { property.SetValue(target, value); return; }
                var field = type.GetField(candidate, flags);
                if (field != null) { field.SetValue(target, value); return; }
            }
        throw new MissingMemberException(target.GetType().Name, member);
    }

    static void Check(bool condition, string message)
    {
        checks++;
        if (condition) Console.WriteLine("  PASS " + message);
        else Fail(message);
    }

    static void Fail(string message) { failures.Add(message); Console.Error.WriteLine("  FAIL " + message); }
    static Exception Root(Exception e) =>
        e is TargetInvocationException tie && tie.InnerException != null ? Root(tie.InnerException) : e;
}
