using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using Mono.Cecil;
using JonCoopQoL;

static class CoopChecks
{
    static int checks;
    static void Check(bool result, string description)
    {
        if (!result) throw new Exception(description);
        checks++; Console.WriteLine("PASS " + description);
    }
    static readonly Dictionary<string, XmlElement> items = new Dictionary<string, XmlElement>();
    static string Property(string name, string property)
    {
        XmlElement item;
        if (!items.TryGetValue(name, out item)) return "";
        var own = item.SelectSingleNode("property[@name='" + property + "']/@value");
        if (own != null) return own.Value;
        var parent = item.SelectSingleNode("property[@name='Extends']/@value");
        return parent == null ? "" : Property(parent.Value, property);
    }
    static string[] Split(string value) { return value.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).ToArray(); }
    static bool Reading(string name)
    {
        return Split(Property(name,"Group")).Any(x => x == "Books" || x == "BooksOnly" || x == "TCReading") ||
            name.EndsWith("Schematic",StringComparison.OrdinalIgnoreCase) || name.EndsWith("SkillMagazine",StringComparison.OrdinalIgnoreCase);
    }
    static string Category(string name)
    {
        if (!items.ContainsKey(name)) throw new Exception("Native item definition missing: " + name);
        var tags = new HashSet<string>(Split(Property(name,"Tags")),StringComparer.OrdinalIgnoreCase);
        return Rules.Category(name,Split(Property(name,"Group")),tags.Contains,false);
    }
    static int Main(string[] args)
    {
        try
        {
            var xml = new XmlDocument { XmlResolver = null }; xml.Load(args[0]);
            foreach(XmlElement item in xml.SelectNodes("/items/item")) items.Add(item.GetAttribute("name"),item);
            var reading = items.Keys.Where(Reading).ToArray();
            Check(reading.Any(x => x.EndsWith("SkillMagazine")) && reading.Any(x => x.EndsWith("Schematic")) && reading.Any(x => x.StartsWith("book")),"native reading set covers magazines, perk books and schematics");
            var seed = reading.First(x => x.StartsWith("book"));
            var chest = new HashSet<string>(StringComparer.Ordinal) { Category(seed) };
            Check(reading.All(x => Rules.MatchesDestination(false,chest,Category(x))),"one native book routes all " + reading.Length + " native reading definitions");
            Check(!Rules.MatchesDestination(false,chest,Category("gunHandgunT1Pistol")),"book chest rejects a pistol");
            Check(Rules.MatchesDestination(true,null,"other"),"native exact-item match remains valid without a category context");
            Check(!Rules.MatchesDestination(false,new HashSet<string>(),Category(seed)),"empty destination does not accept an arbitrary category");
            var blocks = new XmlDocument { XmlResolver = null }; blocks.Load(Path.Combine(Path.GetDirectoryName(args[0]),"blocks.xml"));
            var corn = (XmlElement)blocks.SelectSingleNode("/blocks/block[@name='plantedCorn1']");
            Check(corn != null && Rules.Category(corn.GetAttribute("name"),null,x=>false,true) != Category("resourceWood") && Rules.Category(corn.GetAttribute("name"),null,x=>false,true) != Rules.Category("woodShapes",null,x=>false,true),"native seed blocks route separately from building blocks and resources");
            Check(Category("medicalFirstAidBandage") == Category("medicalFirstAidKit"),"medicine seed routes other medicine");
            Check(Category("foodCanChili") == Category("drinkJarBoiledWater"),"food and drink share a destination category");

            Check(!Rules.RespawnDue(100,819,30) && Rules.RespawnDue(100,820,30),"skull expiry matches native whole-hour / day eligibility");
            Check(!Rules.RespawnDue(100,20000,0) && !Rules.RespawnDue(100,99,30),"disabled respawn and reversed clock keep markers");
            Check(!Rules.RespawnDue(110,820,30),"nearby-player native clock deferral extends skull duration");
            string file = Path.Combine(args[1],"world-a","loot-skulls.xml");
            var ledger = new LootLedger();
            ledger.Records.Add("1,2,3",new LootRecord { Key="1,2,3", X=1,Y=2,Z=3,PoiId=15,AnchorX=11.5f,AnchorY=20,AnchorZ=-30,TouchedHours=100 });
            ledger.Records.Add("4,5,6",new LootRecord { Key="4,5,6", PoiId=16,TouchedHours=105 });
            ledger.Save(file);
            var reopened = new LootLedger(); reopened.Load(file);
            Check(reopened.Records.Count == 2 && reopened.Records["1,2,3"].AnchorX == 11.5f && reopened.Records["1,2,3"].TouchedHours == 100,"actual loot ledger survives save and reload with world coordinates and native clock");
            reopened.ClearPoi(15); reopened.Save(file); ledger.Load(file);
            Check(ledger.Records.Count == 1 && ledger.Records.ContainsKey("4,5,6") && !File.Exists(file+".tmp"),"quest reset persists only that POI removal through atomic replacement");
            ledger.Load(Path.Combine(args[1],"world-b","loot-skulls.xml"));
            Check(ledger.Records.Count == 0,"another world cannot inherit the previous world's skulls");

            var drive = Rules.VehicleControl(70,15,8,8,false);
            Check(drive.Forward > 0 && drive.Steer > 0 && !drive.Brake,"moving ground vehicle follows ahead and steers toward friend");
            drive = Rules.VehicleControl(20,-15,15,0,false);
            Check(drive.Brake && drive.Forward == 0 && drive.Steer < 0,"closing speed brakes before reaching stopped friend's vehicle");
            Check(Rules.VehicleControl(70,0,5,5,true).Brake && Rules.VehicleControl(4,0,0,0,false).Forward == 0,"obstruction, cliff or arrival suppresses vehicle acceleration");
            Check(Rules.CancelFollow(true,false,false,true,20) && Rules.CancelFollow(false,false,true,true,20) && Rules.CancelFollow(false,false,false,false,20) && Rules.CancelFollow(false,false,false,true,201) && !Rules.CancelFollow(false,false,false,true,20),"manual input, death, party departure and lost range cancel follow");
            CheckNativePatches(args[2],args[3]);
            Console.WriteLine(checks + " co-op checks passed; native gameplay still requires an in-game session.");
            return 0;
        }
        catch(Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    static void CheckNativePatches(string gameDll, string modDll)
    {
        using(var game = AssemblyDefinition.ReadAssembly(gameDll))
        using(var mod = AssemblyDefinition.ReadAssembly(modDll))
        {
            int targets = 0;
            foreach(var type in mod.MainModule.Types)
                foreach(var patch in type.CustomAttributes.Where(x=>x.AttributeType.FullName == "HarmonyLib.HarmonyPatch"))
                {
                    string targetType = ((TypeReference)patch.ConstructorArguments[0].Value).FullName;
                    string targetMethod = (string)patch.ConstructorArguments[1].Value;
                    var native = game.MainModule.Types.Single(x=>x.FullName == targetType);
                    var methods = native.Methods.Where(x=>x.Name == targetMethod).ToArray();
                    if(methods.Length != 1) throw new Exception("Ambiguous or missing native patch target: " + targetType + "." + targetMethod);
                    foreach(var method in type.Methods.Where(x=>x.Name == "Prefix" || x.Name == "Postfix" || x.Name == "Finalizer"))
                        foreach(var parameter in method.Parameters)
                        {
                            int index;
                            if(parameter.Name.StartsWith("__") && int.TryParse(parameter.Name.Substring(2),out index))
                            {
                                if(index >= methods[0].Parameters.Count) throw new Exception("Missing indexed patch argument " + type.Name + "." + parameter.Name);
                                string actual = methods[0].Parameters[index].ParameterType.FullName.TrimEnd('&');
                                string declared = parameter.ParameterType.FullName.TrimEnd('&');
                                if(actual != declared) throw new Exception("Patch parameter mismatch: " + type.Name + " " + actual + " vs " + declared);
                            }
                        }
                    targets++;
                }
            Check(targets >= 15,"all " + targets + " Harmony targets and indexed parameters resolve against the installed game");
            var stash = game.MainModule.Types.Single(x=>x.Name == "XUiM_LootContainer").Methods.Single(x=>x.Name == "StashItems");
            var calls = stash.Body.Instructions.Select(x=>x.Operand as MethodReference).Where(x=>x!=null).ToArray();
            Check(calls.Count(x=>x.DeclaringType.Name == "IInventory" && x.Name == "HasItem") == 1 && calls.Any(x=>x.Name == "IsIgnoredSlot") && calls.Any(x=>x.Name == "TryStackItem") && calls.Any(x=>x.Name == "AddItem"),"native stash has the single routing decision and retains locks, stacking and capacity controls");
        }
    }
}
