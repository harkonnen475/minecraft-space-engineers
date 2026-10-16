using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using Newtonsoft.Json.Linq;
using SEMC.V2;
using Sandbox.Definitions;
using VRage.Game;

class EconomyPlannerCheck
{
    static int assertions;
    static string root = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".."));
    static string seBin = @"C:\Program Files (x86)\Steam\steamapps\common\SpaceEngineers\Bin64";
    static void Main(string[] args)
    {
        if (args.Length > 0) seBin = Path.GetFullPath(args[0]);
        AppDomain.CurrentDomain.AssemblyResolve += delegate(object sender, ResolveEventArgs resolveArgs)
        {
            string name = new AssemblyName(resolveArgs.Name).Name + ".dll";
            foreach (string folder in new[] { Path.Combine(root, "research-se"), Path.Combine(root, "tools", "Pulsar", "Libraries", "Legacy"), seBin })
            { string file = Path.Combine(folder, name); if (File.Exists(file)) return Assembly.LoadFrom(file); }
            return null;
        };
        Run();
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Run()
    {
        var catalog = new ItemCatalog(root);
        const string iron = "se:MyObjectBuilder_Ingot/Iron", plate = "se:MyObjectBuilder_Component/SteelPlate";
        var aliases = (Dictionary<string, string>)typeof(ItemCatalog).GetField("aliases", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(catalog);
        aliases[iron] = "minecraft:iron_ingot"; aliases["minecraft:iron_ingot"] = iron;
        JObject bucket = Mc("bucket", new[] { new[] { "minecraft:iron_ingot" } }, new[] { 0, -1, 0, -1, 0, -1 }, 3, 2);
        var se = new Dictionary<string, decimal> { {iron, 3m} };
        CraftPlan plan = RecipeBridge.Plan(catalog, bucket, se, new JArray(), 1);
        Check(plan.Valid && plan.SeTake[iron] == 3 && plan.McTake.Count == 0, "SE ingots fund native MC bucket");
        Check((bool)plan.McCraft["grid"][0]["foreign"] && plan.McCraft["grid"][1].Type == JTokenType.Null, "shape and foreign credits retained");
        var slots = new JArray(Stack(3, "minecraft:iron_ingot", 2, "components-preserved"));
        string before = slots.ToString();
        plan = RecipeBridge.Plan(catalog, bucket, new Dictionary<string, decimal>{{iron, 1m}}, slots, 1);
        Check(plan.Valid && plan.SeTake[iron] == 1 && (int)plan.McTake[0]["count"] == 2, "mixed native stores fund one recipe");
        Check(slots.ToString() == before && (string)plan.McTake[0]["stackHash"] == "components-preserved", "planning preserves original native stack components");
        plan = RecipeBridge.Plan(catalog, bucket, new Dictionary<string, decimal>(), slots, 1);
        Check(!plan.Valid && slots.ToString() == before, "insufficient resources fail without mutation");
        JObject steel = Se(iron, 21m, plate, 1m);
        plan = RecipeBridge.Plan(catalog, steel, new Dictionary<string, decimal>(), new JArray(Stack(1, "minecraft:iron_ingot", 25, "h")), 1);
        Check(plan.Valid && (int)plan.McTake[0]["count"] == 21 && plan.SeGive[plate] == 1, "MC ingots fund automatically imported SE blueprint");
        plan = RecipeBridge.Plan(catalog, Se(iron, .25m, plate, 1m), new Dictionary<string, decimal>(), new JArray(Stack(1, "minecraft:iron_ingot", 1, "h")), 1);
        Check(plan.Valid && plan.SeGive[iron] == .75m && (int)plan.McTake[0]["count"] == 1, "fractional SE units return exact change");
        plan = RecipeBridge.Plan(catalog, Se(iron, 1.25m, plate, 1m), new Dictionary<string, decimal>{{iron, .5m}}, new JArray(Stack(1, "minecraft:iron_ingot", 1, "h")), 1);
        Check(plan.Valid && plan.SeTake[iron] == .5m && plan.SeGive[iron] == .25m, "mixed fractional material conservation");
        JObject duplicate = Se(iron, .25m, plate, 1m); ((JArray)duplicate["inputs"]).Add(new JObject{{"alternatives", new JArray(iron)},{"amount", .25m}});
        plan = RecipeBridge.Plan(catalog, duplicate, new Dictionary<string, decimal>(), new JArray(Stack(1, "minecraft:iron_ingot", 1, "h")), 1);
        Check(plan.Valid && plan.SeGive[iron] == .5m && (int)plan.McTake[0]["count"] == 1, "repeated fractional prerequisites aggregate before whole-ingot conversion");
        JObject constrained = Mc("alternative", new[] { new[] { "minecraft:oak_planks", "minecraft:birch_planks" }, new[] { "minecraft:oak_planks" } }, new[] { 0, 1 }, 2, 1);
        plan = RecipeBridge.Plan(catalog, constrained, new Dictionary<string, decimal>(), new JArray(Stack(1, "minecraft:oak_planks", 1, "a"), Stack(2, "minecraft:birch_planks", 1, "b")), 1);
        Check(plan.Valid && (string)plan.McCraft["grid"][0]["itemId"] == "minecraft:birch_planks", "overlapping ingredient alternatives allocate restrictive input first");
        plan = RecipeBridge.Plan(catalog, bucket, new Dictionary<string, decimal>{{iron, 9m}}, new JArray(), 3);
        Check(plan.Valid && plan.SeTake[iron] == 9 && (int)plan.McCraft["count"] == 3, "batch scales each occupied cell");
        Check(((JArray)plan.McCraft["grids"]).Count == 3, "batch carries a separate native grid for every craft");
        JObject fragment = Mc("fragment", new[] { new[] { "minecraft:oak_log" } }, new[] { 0 }, 1, 1);
        plan = RecipeBridge.Plan(catalog, fragment, new Dictionary<string, decimal>(), new JArray(Stack(1, "minecraft:oak_log", 1, "named-log"), Stack(2, "minecraft:oak_log", 1, "plain-log")), 2);
        Check(plan.Valid && plan.McTake.Count == 2 && (int)plan.McTake[0]["count"] == 1 && (int)plan.McTake[1]["count"] == 1, "batch consumes fragmented one-unit stacks exactly once");
        Check((int)plan.McCraft["grids"][0][0]["slot"] != (int)plan.McCraft["grids"][1][0]["slot"], "per-craft grids preserve each native stack's components");
        plan = RecipeBridge.Plan(catalog, steel, new Dictionary<string, decimal>(), new JArray(Stack(1, "minecraft:iron_ingot", 100, "h")), 3);
        Check(plan.Valid && (int)plan.McTake[0]["count"] == 63 && plan.SeGive[plate] == 3, "SE batch scales prerequisites and results");
        Check(!RecipeBridge.Plan(catalog, bucket, se, new JArray(), 0).Valid && !RecipeBridge.Plan(catalog, bucket, se, new JArray(), 65).Valid, "batch bounds fail closed");
        bucket["supported"] = false;
        Check(!RecipeBridge.Plan(catalog, bucket, se, new JArray(), 1).Valid, "unsupported machine recipe family is not silently crafted");
        Check(aliases.Count == 2, "no inferred ore-to-ingot conversion");
        var registry = new ItemCatalog(root);
        var native = (Dictionary<string, MyPhysicalItemDefinition>)typeof(ItemCatalog).GetField("native", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(registry);
        var imported = (Dictionary<string, JObject>)typeof(ItemCatalog).GetField("items", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(registry);
        var copper = new MyPhysicalItemDefinition { Id = new MyDefinitionId(typeof(MyObjectBuilder_Ingot), "Copper") };
        string seCopper = ItemCatalog.Key(copper.Id); native[seCopper] = copper;
        imported["examplemod:copper_ingot"] = new JObject{{"engine", "MC"}};
        var rebuild = typeof(ItemCatalog).GetMethod("RebuildAliases", BindingFlags.NonPublic | BindingFlags.Instance); rebuild.Invoke(registry, null);
        Check(registry.Alias("examplemod:copper_ingot") == seCopper && registry.Alias(seCopper) == "examplemod:copper_ingot", "new namespace ingot integrates without per-item code");
        imported["othermod:copper_ingot"] = new JObject{{"engine", "MC"}}; rebuild.Invoke(registry, null);
        Check(registry.Alias(seCopper) == null && registry.Alias("examplemod:copper_ingot") == null, "ambiguous material definitions fail closed regardless of load order");
        Console.WriteLine("PASS " + assertions + " economy planner assertions");
    }
    static JObject Stack(int slot, string id, int count, string hash) { return new JObject{{"slot", slot},{"itemId", id},{"count", count},{"stackHash", hash},{"stackData", new JObject{{"components", "kept"}}}}; }
    static JObject Se(string input, decimal amount, string output, decimal result) { return new JObject{{"engine", "SE"},{"supported", true},{"inputs", new JArray(new JObject{{"alternatives", new JArray(input)},{"amount", amount}})},{"outputs", new JArray(new JObject{{"id", output},{"amount", result}})}}; }
    static JObject Mc(string name, string[][] choices, int[] layout, int width, int height)
    {
        var ingredients = new JArray(); foreach (string[] choice in choices) ingredients.Add(new JObject{{"items", new JArray(choice)}});
        return new JObject{{"engine", "MC"},{"nativeId", "minecraft:" + name},{"supported", true},{"ingredients", ingredients},{"layout", new JArray(layout)},{"width", width},{"height", height}};
    }
    static void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL " + name); assertions++; Console.WriteLine("PASS " + name); }
}
