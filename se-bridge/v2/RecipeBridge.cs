using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace SEMC.V2
{
    public sealed class CraftPlan
    {
        public readonly Dictionary<string, decimal> SeTake = new Dictionary<string, decimal>(StringComparer.Ordinal);
        public readonly Dictionary<string, decimal> SeGive = new Dictionary<string, decimal>(StringComparer.Ordinal);
        public readonly JArray McTake = new JArray();
        public JObject McCraft;
        public string Error;
        public bool Valid { get { return Error == null; } }
        public static void Add(Dictionary<string, decimal> values, string id, decimal amount) { decimal old; values.TryGetValue(id, out old); values[id] = old + amount; }
        public static JObject Json(Dictionary<string, decimal> values) { var result = new JObject(); foreach (var pair in values) result[pair.Key] = pair.Value; return result; }
    }

    // Pure planner: inputs are snapshots, and planning never changes either engine.
    public static class RecipeBridge
    {
        sealed class Stock
        {
            public int Slot, Count, Used;
            public string Id, Hash;
        }
        public static CraftPlan Plan(ItemCatalog catalog, JObject recipe, IDictionary<string, decimal> nativeStock, JArray mcSlots, int count)
        {
            var plan = new CraftPlan();
            if (recipe == null) { plan.Error = "Nie ma takiej receptury."; return plan; }
            if (!((bool?)recipe["supported"] ?? false)) { plan.Error = "Ta rodzina receptur wymaga osobnego urządzenia."; return plan; }
            if (count < 1 || count > 64) { plan.Error = "Wybierz od 1 do 64 operacji."; return plan; }
            var mc = new List<Stock>();
            if (mcSlots != null) foreach (JObject item in mcSlots)
            {
                int amount = (int?)item["count"] ?? 0; string id = (string)item["itemId"];
                if (amount > 0 && !String.IsNullOrEmpty(id)) mc.Add(new Stock { Slot = (int?)item["slot"] ?? -1, Id = id, Count = amount, Hash = (string)item["stackHash"] });
            }
            if ((string)recipe["engine"] == "SE") PlanSe(catalog, recipe, nativeStock, mc, count, plan);
            else PlanMc(catalog, recipe, nativeStock, mc, count, plan);
            if (plan.Error != null) return plan;
            foreach (Stock stack in mc) if (stack.Used > 0) plan.McTake.Add(new JObject { {"slot", stack.Slot}, {"count", stack.Used}, {"stackHash", stack.Hash} });
            return plan;
        }
        static void PlanSe(ItemCatalog catalog, JObject recipe, IDictionary<string, decimal> se, List<Stock> mc, int count, CraftPlan plan)
        {
            var required = new Dictionary<string, decimal>(StringComparer.Ordinal);
            foreach (JObject input in (JArray)recipe["inputs"])
                CraftPlan.Add(required, (string)input["alternatives"][0], (decimal)input["amount"] * count);
            foreach (var input in required)
            {
                string id = input.Key; decimal needed = input.Value, available, reserved;
                se.TryGetValue(id, out available); plan.SeTake.TryGetValue(id, out reserved); decimal own = Math.Min(needed, Math.Max(0, available - reserved));
                if (own > 0) CraftPlan.Add(plan.SeTake, id, own); needed -= own;
                if (needed <= 0) continue;
                string alias = catalog.Alias(id);
                if (alias == null) { plan.Error = "Brakuje: " + Name(catalog, id); return; }
                int take = checked((int)Decimal.Ceiling(needed)), remaining = take;
                foreach (Stock stack in mc) if (stack.Id == alias)
                {
                    int amount = Math.Min(remaining, stack.Count - stack.Used); stack.Used += amount; remaining -= amount; if (remaining == 0) break;
                }
                if (remaining > 0) { plan.Error = "Brakuje: " + Name(catalog, id); return; }
                // MC stacks are integral. A fractional SE prerequisite receives its exact SE change.
                if (take > needed) CraftPlan.Add(plan.SeGive, id, take - needed);
            }
            foreach (JObject output in (JArray)recipe["outputs"]) CraftPlan.Add(plan.SeGive, (string)output["id"], (decimal)output["amount"] * count);
        }
        static void PlanMc(ItemCatalog catalog, JObject recipe, IDictionary<string, decimal> se, List<Stock> mc, int count, CraftPlan plan)
        {
            var ingredients = recipe["ingredients"] as JArray; var layout = recipe["layout"] as JArray;
            if (ingredients == null || ingredients.Count == 0) { plan.Error = "Brak opisu składników."; return; }
            int width = (int?)recipe["width"] ?? 0, height = (int?)recipe["height"] ?? 0;
            if (layout == null || layout.Count == 0) { layout = new JArray(); for (int i = 0; i < ingredients.Count; i++) layout.Add(i); }
            if (width < 1 || height < 1) { width = Math.Min(3, Math.Max(1, layout.Count)); height = (layout.Count + width - 1) / width; }
            if (width > 3 || height > 3 || layout.Count > 9) { plan.Error = "Receptura nie mieści się w warsztacie 3 × 3."; return; }
            int stride = width * height; var repeatedLayout = new JArray();
            for (int batch = 0; batch < count; batch++) for (int i = 0; i < stride; i++) repeatedLayout.Add(i < layout.Count ? (int)layout[i] : -1);
            var grid = new JObject[stride * count];
            var cells = new List<int>(); for (int i = 0; i < repeatedLayout.Count; i++) if ((int)repeatedLayout[i] >= 0) cells.Add(i);
            // Solve restrictive ingredients first, preserving their original position in the grid.
            cells.Sort(delegate(int a, int b) { return Options(ingredients, (int)repeatedLayout[a]).Count.CompareTo(Options(ingredients, (int)repeatedLayout[b]).Count); });
            int attempts = 0;
            if (!Allocate(0, cells, repeatedLayout, ingredients, grid, mc, se, catalog, plan.SeTake, 1, ref attempts)) { plan.Error = "Brakuje składników w połączonym EQ."; return; }
            var grids = new JArray();
            for (int batch = 0; batch < count; batch++)
            {
                var json = new JArray(); for (int i = 0; i < stride; i++) { JObject cell = grid[batch * stride + i]; json.Add(cell == null ? JValue.CreateNull() : (JToken)cell); } grids.Add(json);
            }
            plan.McCraft = new JObject { {"recipeId", (string)recipe["nativeId"]}, {"width", width}, {"height", height}, {"count", count}, {"grid", grids[0].DeepClone()}, {"grids", grids} };
        }
        static JArray Options(JArray ingredients, int index)
        {
            if (index < 0 || index >= ingredients.Count) return new JArray();
            return ingredients[index]["items"] as JArray ?? new JArray();
        }
        static bool Allocate(int depth, List<int> cells, JArray layout, JArray ingredients, JObject[] grid, List<Stock> mc, IDictionary<string, decimal> se, ItemCatalog catalog, Dictionary<string, decimal> usedSe, int count, ref int attempts)
        {
            if (depth == cells.Count) return true;
            if (++attempts > 10000) return false;
            int cell = cells[depth];
            foreach (JToken option in Options(ingredients, (int)layout[cell]))
            {
                string id = (string)option;
                foreach (Stock stock in mc) if (stock.Id == id && stock.Count - stock.Used >= count)
                {
                    stock.Used += count; grid[cell] = new JObject { {"slot", stock.Slot}, {"itemId", id} };
                    if (Allocate(depth + 1, cells, layout, ingredients, grid, mc, se, catalog, usedSe, count, ref attempts)) return true;
                    stock.Used -= count;
                }
                string alias = catalog.Alias(id); decimal available, used;
                if (alias == null || !se.TryGetValue(alias, out available)) continue;
                usedSe.TryGetValue(alias, out used); if (available - used < count) continue;
                usedSe[alias] = used + count; grid[cell] = new JObject { {"itemId", id}, {"foreign", true} };
                if (Allocate(depth + 1, cells, layout, ingredients, grid, mc, se, catalog, usedSe, count, ref attempts)) return true;
                if (used == 0) usedSe.Remove(alias); else usedSe[alias] = used;
            }
            grid[cell] = null; return false;
        }
        static string Name(ItemCatalog catalog, string id) { JObject item = catalog.Item(id); return item == null ? id : (string)item["name"]; }
    }
}
