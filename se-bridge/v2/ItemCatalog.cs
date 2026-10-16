using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using Sandbox.Definitions;
using VRage.Game;

namespace SEMC.V2
{
    // Definitions and recipes are discovered from the running engines. No item whitelist.
    public sealed class ItemCatalog
    {
        readonly string root;
        readonly Dictionary<string, JObject> items = new Dictionary<string, JObject>(StringComparer.Ordinal);
        readonly Dictionary<string, JObject> recipes = new Dictionary<string, JObject>(StringComparer.Ordinal);
        readonly Dictionary<string, string> aliases = new Dictionary<string, string>(StringComparer.Ordinal);
        readonly Dictionary<string, MyPhysicalItemDefinition> native = new Dictionary<string, MyPhysicalItemDefinition>(StringComparer.Ordinal);
        long nextRead;
        DateTime catalogTime, recipeTime;
        bool loaded;
        public IEnumerable<JObject> Items { get { return items.Values; } }
        public IEnumerable<JObject> Recipes { get { return recipes.Values; } }
        public int Revision { get; private set; }
        public ItemCatalog(string rootPath) { root = rootPath; }
        public JObject Item(string id) { JObject value; return id != null && items.TryGetValue(id, out value) ? value : null; }
        public JObject Recipe(string id) { JObject value; return id != null && recipes.TryGetValue(id, out value) ? value : null; }
        public MyPhysicalItemDefinition Native(string id) { MyPhysicalItemDefinition value; return id != null && native.TryGetValue(id, out value) ? value : null; }
        public string Alias(string id) { string value; return id != null && aliases.TryGetValue(id, out value) ? value : null; }
        public static string Key(MyDefinitionId id) { return "se:" + id.ToString(); }

        public bool Update()
        {
            if (BridgeIO.Now() < nextRead) return false;
            nextRead = BridgeIO.Now() + 1500;
            bool changed = false;
            if (!loaded && MyDefinitionManager.Static != null)
            {
                foreach (var definition in MyDefinitionManager.Static.GetPhysicalItemDefinitions())
                {
                    if (!definition.Enabled) continue;
                    string key = Key(definition.Id);
                    native[key] = definition;
                    string icon = definition.Icons != null && definition.Icons.Length > 0 ? definition.Icons[0] : null;
                    if (!String.IsNullOrEmpty(icon) && !Path.IsPathRooted(icon)) icon = Path.Combine(Sandbox.MySandboxGame.Static != null ? VRage.FileSystem.MyFileSystem.ContentPath : "", icon);
                    items[key] = new JObject { {"id", key}, {"engine", "SE"}, {"source", "se"}, {"name", definition.DisplayNameText ?? definition.Id.SubtypeName}, {"icon", icon}, {"unit", definition.HasIntegralAmounts ? "szt." : "kg"}, {"integral", definition.HasIntegralAmounts}, {"mass", definition.Mass}, {"volume", definition.Volume}, {"selectable", MyDefinitionManager.Static.HandItemExistsFor(definition.Id)} };
                }
                foreach (var definition in MyDefinitionManager.Static.GetBlueprintDefinitions())
                {
                    if (!definition.Enabled || definition.Prerequisites == null || definition.Results == null || definition.Prerequisites.Length == 0 || definition.Results.Length == 0) continue;
                    var inputs = new JArray(); var outputs = new JArray(); bool usable = true;
                    foreach (var part in definition.Prerequisites)
                    {
                        string key = Key(part.Id); if (!native.ContainsKey(key) || (decimal)part.Amount <= 0) { usable = false; break; }
                        inputs.Add(new JObject { {"alternatives", new JArray(key)}, {"amount", (decimal)part.Amount} });
                    }
                    foreach (var part in definition.Results)
                    {
                        string key = Key(part.Id); if (!native.ContainsKey(key) || (decimal)part.Amount <= 0) { usable = false; break; }
                        outputs.Add(new JObject { {"id", key}, {"amount", (decimal)part.Amount} });
                    }
                    if (!usable) continue;
                    string id = Key(definition.Id);
                    bool bundle = definition is MyCompositeBlueprintDefinition;
                    string name = definition.DisplayNameText ?? definition.Id.SubtypeName;
                    recipes[id] = new JObject { {"id", id}, {"nativeId", definition.Id.ToString()}, {"nativeType", definition.GetType().FullName}, {"engine", "SE"}, {"name", bundle ? "Komponenty: " + name : name}, {"inputs", inputs}, {"outputs", outputs}, {"supported", true}, {"componentBundle", bundle}, {"description", bundle ? "Wytwarza zestaw części do budowy bloku SE. Wynikami są wymienione komponenty." : ""}, {"station", bundle ? "Warsztat · części do budowy SE" : "Wspólny warsztat"}, {"seconds", definition.BaseProductionTimeInSeconds} };
                }
                loaded = true; changed = true;
            }
            string path = Path.Combine(root, "catalog-mc-v3.json");
            if (File.Exists(path) && File.GetLastWriteTimeUtc(path) != catalogTime)
            {
                var data = JObject.Parse(BridgeIO.ReadSharedText(path)); var list = data["items"] as JArray;
                if (list != null)
                {
                    var remove = new List<string>(); foreach (var pair in items) if ((string)pair.Value["engine"] == "MC") remove.Add(pair.Key); foreach (string key in remove) items.Remove(key);
                    foreach (JObject entry in list)
                    {
                        string id = (string)entry["itemId"]; if (String.IsNullOrEmpty(id) || id == "minecraft:air") continue;
                        var item = (JObject)entry.DeepClone(); item["id"] = id; item["name"] = (string)entry["displayName"] ?? id; item["engine"] = "MC"; item["source"] = "mc"; item["unit"] = "szt."; item["integral"] = true; item["selectable"] = true; item["block"] = entry["blockStateId"] != null;
                        items[id] = item;
                    }
                    catalogTime = File.GetLastWriteTimeUtc(path); changed = true;
                }
            }
            path = Path.Combine(root, "recipes-mc-v3.json");
            if (File.Exists(path) && File.GetLastWriteTimeUtc(path) != recipeTime)
            {
                var data = JObject.Parse(BridgeIO.ReadSharedText(path)); var list = data["recipes"] as JArray;
                if (list != null)
                {
                    var remove = new List<string>(); foreach (var pair in recipes) if ((string)pair.Value["engine"] == "MC") remove.Add(pair.Key); foreach (string key in remove) recipes.Remove(key);
                    foreach (JObject entry in list)
                    {
                        string id = (string)entry["recipeId"]; if (String.IsNullOrEmpty(id)) continue;
                        var inputs = new JArray(); var ingredients = entry["ingredients"] as JArray; var layout = entry["layout"] as JArray;
                        if (ingredients != null) for (int i = 0; i < ingredients.Count; i++)
                        {
                            var ingredient = (JObject)ingredients[i]; int amount = 0;
                            if (layout == null || layout.Count == 0) amount = 1; else foreach (JToken cell in layout) if ((int)cell == i) amount++;
                            if (amount > 0) inputs.Add(new JObject { {"alternatives", ingredient["items"] == null ? new JArray() : ingredient["items"].DeepClone()}, {"amount", amount} });
                        }
                        JObject result = entry["result"] as JObject;
                        string resultId = result == null ? null : (string)result["itemId"];
                        var recipe = (JObject)entry.DeepClone(); recipe["id"] = "mc:" + id; recipe["nativeId"] = id; recipe["engine"] = "MC"; recipe["inputs"] = inputs;
                        recipe["outputs"] = String.IsNullOrEmpty(resultId) ? new JArray() : new JArray(new JObject { {"id", resultId}, {"amount", (int?)result["count"] ?? 1} });
                        JObject output = Item(resultId); recipe["name"] = output == null ? id : (string)output["name"];
                        recipe["supported"] = (bool?)entry["craftable"] ?? false; recipe["station"] = "Wspólny warsztat";
                        recipes[(string)recipe["id"]] = recipe;
                    }
                    recipeTime = File.GetLastWriteTimeUtc(path); changed = true;
                }
            }
            if (changed) { RebuildAliases(); Revision++; }
            return changed;
        }

        void RebuildAliases()
        {
            aliases.Clear();
            // Explicit unit policy, applied to every matching registry entry: one MC ingot = one SE kg.
            var ingots = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var candidates = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in native) if (pair.Value.IsIngot) Candidate(ingots, pair.Value.Id.SubtypeName.Replace("_", ""), pair.Key);
            foreach (var pair in items)
            {
                const string suffix = "_ingot"; int colon = pair.Key.IndexOf(':');
                if ((string)pair.Value["engine"] != "MC" || colon < 1 || !pair.Key.EndsWith(suffix, StringComparison.Ordinal)) continue;
                string material = pair.Key.Substring(colon + 1, pair.Key.Length - colon - 1 - suffix.Length).Replace("_", "");
                Candidate(candidates, material, pair.Key);
            }
            foreach (var pair in candidates)
            {
                List<string> se;
                // Ambiguous mods must declare a material policy instead of winning by load order.
                if (pair.Value.Count != 1 || !ingots.TryGetValue(pair.Key, out se) || se.Count != 1) continue;
                aliases[pair.Value[0]] = se[0]; aliases[se[0]] = pair.Value[0];
            }
        }
        static void Candidate(Dictionary<string, List<string>> groups, string material, string id)
        { List<string> values; if (!groups.TryGetValue(material, out values)) { values = new List<string>(); groups[material] = values; } values.Add(id); }
    }
}
