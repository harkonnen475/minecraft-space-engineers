using System;
using System.Collections.Generic;
using System.Globalization;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Sandbox.Game;
using Sandbox.Game.World;
using Sandbox.Game.Entities.Character;
using Sandbox.ModAPI;
using VRage;
using VRage.Game;
using VRage.Game.Entity;
using VRage.ObjectBuilders;

namespace SEMC.V2
{
    // Both engines keep their native, component-rich item stacks. This facade joins their
    // inventories and recipes; a saved write-ahead transaction joins cross-engine changes.
    public sealed class UnifiedInventory : IDisposable
    {
        readonly string root, journalPath, receiptPath;
        readonly Action<string> log;
        readonly ItemCatalog catalog;
        readonly Dictionary<string, string> defaultItems = new Dictionary<string, string>(StringComparer.Ordinal);
        JObject mcState, worldState, journal, request, utility;
        long nextRead, nextSend;
        string signature, status = "Wczytywanie wspólnego EQ…";
        string diagnosticPauseStage;
        string confirmedMcSelection;
        long confirmedSelectionAt;
        bool connected, loaded, blocked, disposed;
        int revision;
        int displayedCatalogRevision = -1;
        sealed class Requirement { public JObject Row; public string[] Ids; public decimal Amount; }
        sealed class RecipeDisplay { public JObject Native, Row; public bool Supported; public readonly List<Requirement> Inputs = new List<Requirement>(); }
        readonly List<RecipeDisplay> recipeDisplay = new List<RecipeDisplay>();
        public Action<string> SelectionConfirmed;
        public bool Busy { get { return journal != null && (string)journal["stage"] != "complete" || utility != null; } }
        public JObject View { get; private set; }
        public UnifiedInventory(string rootPath, Action<string> logger)
        {
            root = rootPath; log = logger; catalog = new ItemCatalog(root);
            journalPath = Path.Combine(root, "economy-se-v3", "current.json"); receiptPath = Path.Combine(root, "economy-se-v3", "receipts");
            View = new JObject { {"catalog", new JArray()}, {"items", new JArray()}, {"recipes", new JArray()}, {"busy", false}, {"status", status}, {"revision", 0} };
        }
        public void Update(bool backendConnected)
        {
            if (disposed) return;
            connected = backendConnected;
            try
            {
                if (!loaded)
                {
                    if (File.Exists(journalPath)) journal = JObject.Parse(BridgeIO.ReadSharedText(journalPath));
                    if (journal != null && (string)journal["stage"] == "complete") journal = null;
                    loaded = true;
                    if (journal != null) { status = "Odtwarzanie niedokończonej transakcji…"; log("ECONOMY recovery tx=" + (string)journal["txId"] + " stage=" + (string)journal["stage"]); }
                }
                bool changed = catalog.Update();
                if (BridgeIO.Now() >= nextRead)
                {
                    nextRead = BridgeIO.Now() + 500;
                    string path = Path.Combine(root, "economy-state-v3.json");
                    if (File.Exists(path)) mcState = JObject.Parse(BridgeIO.ReadSharedText(path));
                    path = Path.Combine(root, "world-v2.json");
                    if (File.Exists(path)) worldState = JObject.Parse(BridgeIO.ReadSharedText(path));
                    Publish(changed);
                }
                if (journal != null && !blocked && (string)journal["stage"] != diagnosticPauseStage) Advance();
                else if (utility != null) AdvanceUtility();
                View["busy"] = Busy; View["status"] = status;
            }
            catch (IOException error) { status = "Czekam na zakończenie zapisu danych."; log("ECONOMY waiting for file access: " + error); }
            catch (Exception error)
            {
                if (Busy) blocked = true;
                status = "Operacja została zatrzymana. Zachowano dane potrzebne do jej odzyskania.";
                log("ECONOMY ERROR " + error);
            }
        }
        MyInventory Inventory()
        {
            return MyAPIGateway.Session == null || MyAPIGateway.Session.Player == null || MyAPIGateway.Session.Player.Character == null ? null : MyAPIGateway.Session.Player.Character.GetInventory(0) as MyInventory;
        }
        bool Fresh()
        {
            long timestamp = mcState == null ? 0 : (long?)mcState["timestamp"] ?? 0, now = BridgeIO.Now();
            if (!connected || timestamp <= 0 || timestamp > now + 1000 || now - timestamp >= 4000 || String.IsNullOrEmpty((string)mcState["worldId"]) || worldState == null) return false;
            if ((string)mcState["worldId"] != (string)worldState["worldId"] || (string)worldState["dimension"] != "minecraft:overworld") return false;
            string path = (string)worldState["worldPath"], expected = Path.Combine(root, "minecraft-backend", "saves", "SEMC Backend");
            return !String.IsNullOrEmpty(path) && String.Equals(Path.GetFullPath(path).TrimEnd('\\'), Path.GetFullPath(expected).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        }
        Dictionary<string, decimal> Stock(MyInventory inventory)
        {
            var result = new Dictionary<string, decimal>(StringComparer.Ordinal);
            if (inventory != null) foreach (var stack in inventory.GetItems()) CraftPlan.Add(result, ItemCatalog.Key(stack.Content.GetObjectId()), (decimal)stack.Amount);
            return result;
        }
        Dictionary<string, decimal> CraftStock(MyInventory inventory)
        {
            var result = new Dictionary<string, decimal>(StringComparer.Ordinal);
            if (inventory != null) foreach (var stack in inventory.GetItems()) if (CanSpend(stack.Content)) CraftPlan.Add(result, ItemCatalog.Key(stack.Content.GetObjectId()), (decimal)stack.Amount);
            return result;
        }
        bool CanSpend(MyObjectBuilder_PhysicalObject content)
        {
            // Generic SE blueprints accept definition IDs. Preserve decorated/damaged/custom
            // objects rather than silently converting their data into a default item on rollback.
            if (content.Flags != MyItemFlags.None || content.DurabilityHP.HasValue || content.UniqueId != Guid.Empty) return false;
            string id = ItemCatalog.Key(content.GetObjectId()), expected;
            if (!defaultItems.TryGetValue(id, out expected))
            {
                var builder = MyObjectBuilderSerializer.CreateNewObject(content.TypeId, content.SubtypeName) as MyObjectBuilder_PhysicalObject;
                if (builder == null) return false;
                expected = MyAPIGateway.Utilities.SerializeToXML<MyObjectBuilder_PhysicalObject>(builder); defaultItems[id] = expected;
            }
            return MyAPIGateway.Utilities.SerializeToXML<MyObjectBuilder_PhysicalObject>(content) == expected;
        }
        void Publish(bool force)
        {
            MyInventory inventory = Inventory(); if (inventory == null) return;
            var stock = Stock(inventory); var craftStock = CraftStock(inventory); var slots = mcState == null ? new JArray() : mcState["slots"] as JArray ?? new JArray();
            bool fresh = Fresh();
            string next = catalog.Revision.ToString() + ":" + fresh.ToString() + ":" + (mcState == null ? "" : (string)mcState["selectedSlot"]) + ":" + Hash(CraftPlan.Json(stock).ToString(Formatting.None) + slots.ToString(Formatting.None));
            if (!force && next == signature) return;
            Stopwatch watch = Stopwatch.StartNew();
            signature = next;
            if (displayedCatalogRevision != catalog.Revision) BuildDisplay();
            var items = new JArray();
            foreach (var stack in inventory.GetItems())
            {
                string id = ItemCatalog.Key(stack.Content.GetObjectId()); JObject source = catalog.Item(id);
                var row = source == null ? new JObject { {"id", id}, {"engine", "SE"}, {"name", id} } : (JObject)source.DeepClone();
                row["amount"] = (decimal)stack.Amount; row["stackKey"] = "se:" + inventory.Owner.EntityId.ToString() + ":" + stack.ItemId.ToString(); row["nativeItemId"] = stack.ItemId;
                items.Add(row);
            }
            foreach (JObject stack in slots)
            {
                if (((int?)stack["count"] ?? 0) <= 0) continue;
                string id = (string)stack["itemId"]; JObject source = catalog.Item(id);
                var row = source == null ? new JObject { {"id", id}, {"engine", "MC"}, {"name", (string)stack["displayName"] ?? id} } : (JObject)source.DeepClone();
                row["name"] = (string)stack["displayName"] ?? (string)row["name"]; row["amount"] = (int)stack["count"]; row["unit"] = "szt.";
                row["slot"] = stack["slot"]; row["stackKey"] = "mc:" + (string)stack["slot"] + ":" + (string)stack["stackHash"]; row["stackHash"] = stack["stackHash"]; row["stackData"] = stack["stackData"] == null ? null : stack["stackData"].DeepClone();
                items.Add(row);
            }
            var availableStock = new Dictionary<string, decimal>(craftStock, StringComparer.Ordinal);
            foreach (JObject stack in slots) { string id = (string)stack["itemId"]; if (id != null) CraftPlan.Add(availableStock, id, (int?)stack["count"] ?? 0); }
            foreach (RecipeDisplay entry in recipeDisplay)
            {
                bool possible = fresh && entry.Supported;
                foreach (Requirement input in entry.Inputs)
                {
                    decimal available = 0;
                    foreach (string id in input.Ids) { decimal amount; if (availableStock.TryGetValue(id, out amount)) available += amount; }
                    input.Row["available"] = available;
                    if (available < input.Amount) possible = false;
                }
                CraftPlan plan = possible ? RecipeBridge.Plan(catalog, entry.Native, craftStock, slots, 1) : null;
                entry.Row["craftable"] = plan != null && plan.Valid;
                entry.Row["reason"] = !fresh ? "Minecraft jest rozłączony." : !entry.Supported ? "Ta rodzina receptur wymaga osobnego urządzenia." : plan == null ? "Brakuje składników w połączonym EQ." : plan.Error;
            }
            if (status == "Wczytywanie wspólnego EQ…" && fresh) status = "Wspólne EQ gotowe. Receptury korzystają z zasobów obu gier.";
            watch.Stop();
            View["items"] = items; View["busy"] = Busy; View["status"] = status; View["revision"] = ++revision;
            string selected = "minecraft:air"; int selectedSlot = mcState == null ? -1 : (int?)mcState["selectedSlot"] ?? -1;
            foreach (JObject stack in slots) if ((int?)stack["slot"] == selectedSlot && ((int?)stack["count"] ?? 0) > 0) { selected = (string)stack["itemId"]; break; }
            if (confirmedMcSelection != null && ((long?)mcState["timestamp"] ?? 0) < confirmedSelectionAt) selected = confirmedMcSelection;
            else confirmedMcSelection = null;
            View["selectedItemId"] = selected;
            View["worldId"] = mcState == null ? null : mcState["worldId"]; View["connected"] = fresh; View["seVolume"] = (decimal)inventory.CurrentVolume; View["seMaxVolume"] = (decimal)inventory.MaxVolume;
            View["buildMs"] = watch.ElapsedMilliseconds; View["aliasPolicy"] = "1 sztabka MC = 1 kg sztabki SE tego samego metalu";
            if (watch.ElapsedMilliseconds > 150) log("ECONOMY view built in " + watch.ElapsedMilliseconds + "ms recipes=" + recipeDisplay.Count);
        }
        void BuildDisplay()
        {
            var all = new JArray(); foreach (JObject item in catalog.Items) all.Add(item.DeepClone()); View["catalog"] = all;
            var rows = new JArray(); recipeDisplay.Clear();
            foreach (JObject source in catalog.Recipes)
            {
                var row = new JObject { {"id", source["id"]}, {"engine", source["engine"]}, {"name", source["name"]}, {"inputs", source["inputs"].DeepClone()}, {"outputs", source["outputs"].DeepClone()}, {"station", source["station"]}, {"description", source["description"]}, {"componentBundle", source["componentBundle"]}, {"supported", source["supported"]} };
                var entry = new RecipeDisplay { Native = source, Row = row, Supported = (bool?)source["supported"] ?? false };
                foreach (JObject input in (JArray)row["inputs"])
                {
                    var ids = new HashSet<string>(StringComparer.Ordinal);
                    foreach (JToken option in (JArray)input["alternatives"]) { string id = (string)option; ids.Add(id); string alias = catalog.Alias(id); if (alias != null) ids.Add(alias); }
                    var options = new string[ids.Count]; ids.CopyTo(options);
                    entry.Inputs.Add(new Requirement { Row = input, Ids = options, Amount = (decimal)input["amount"] });
                }
                recipeDisplay.Add(entry); rows.Add(row);
            }
            View["recipes"] = rows; displayedCatalogRevision = catalog.Revision;
        }
        public string Craft(string recipeId, int count)
        {
            if (Busy) return SetStatus("Trwa poprzednia operacja.");
            if (!Fresh()) return SetStatus("Minecraft musi być połączony przed wytwarzaniem.");
            MyInventory inventory = Inventory(); if (inventory == null) return SetStatus("Brak ekwipunku postaci.");
            JObject recipe = catalog.Recipe(recipeId);
            CraftPlan plan = RecipeBridge.Plan(catalog, recipe, CraftStock(inventory), mcState["slots"] as JArray, count);
            if (!plan.Valid) return SetStatus(plan.Error);
            string capacity = Capacity(inventory, plan.SeTake, plan.SeGive); if (capacity != null) return SetStatus(capacity);
            string tx = Guid.NewGuid().ToString("N");
            var affected = new HashSet<string>(plan.SeTake.Keys); affected.UnionWith(plan.SeGive.Keys);
            var stock = Stock(inventory); var before = new JObject(); var reserve = new JObject(); var after = new JObject();
            foreach (string id in affected)
            {
                decimal amount, take, give; stock.TryGetValue(id, out amount); plan.SeTake.TryGetValue(id, out take); plan.SeGive.TryGetValue(id, out give);
                before[id] = amount; reserve[id] = amount - take; after[id] = amount - take + give;
            }
            var args = new JObject { {"txId", tx}, {"takes", plan.McTake}, {"gives", new JArray()} }; if (plan.McCraft != null) args["craft"] = plan.McCraft;
            journal = new JObject { {"version", 3}, {"txId", tx}, {"worldId", mcState["worldId"]}, {"seWorld", MySession.Static.CurrentPath}, {"characterId", inventory.Owner.EntityId}, {"recipeId", recipeId}, {"count", count}, {"stage", "reserve"}, {"created", BridgeIO.Now()}, {"before", before}, {"reserved", reserve}, {"after", after}, {"seTake", CraftPlan.Json(plan.SeTake)}, {"seGive", CraftPlan.Json(plan.SeGive)}, {"mcPrepare", args} };
            blocked = false; request = null; WriteJournal();
            return SetStatus("Wytwarzanie: " + (string)recipe["name"]);
        }
        string Capacity(MyInventory inventory, IDictionary<string, decimal> take, IDictionary<string, decimal> give)
        {
            decimal volume = (decimal)inventory.CurrentVolume, mass = (decimal)inventory.CurrentMass;
            foreach (var pair in take) { var definition = catalog.Native(pair.Key); if (definition == null) return "Nieznana definicja SE."; volume -= pair.Value * (decimal)definition.Volume; mass -= pair.Value * (decimal)definition.Mass; }
            foreach (var pair in give)
            {
                var definition = catalog.Native(pair.Key); if (definition == null || pair.Value <= 0) return "Nieznany wynik receptury SE.";
                if (definition.HasIntegralAmounts && pair.Value != Decimal.Truncate(pair.Value)) return "Receptura daje niecałkowitą liczbę części.";
                volume += pair.Value * (decimal)definition.Volume; mass += pair.Value * (decimal)definition.Mass;
            }
            if (volume > (decimal)inventory.MaxVolume + .000001m) return "Za mało miejsca w ekwipunku SE.";
            if (mass > (decimal)inventory.MaxMass + .000001m) return "Przekroczona masa ekwipunku SE.";
            return null;
        }
        void Advance()
        {
            if (!Fresh()) { SetStatus("Transakcja zachowana. Czekam na ponowne połączenie Minecrafta."); return; }
            if ((string)journal["worldId"] != (string)mcState["worldId"] || !String.Equals((string)journal["seWorld"], MySession.Static.CurrentPath, StringComparison.OrdinalIgnoreCase)) { Block("Zapis transakcji należy do innego świata."); return; }
            MyInventory inventory = Inventory(); if (inventory == null) return;
            if ((long)journal["characterId"] != inventory.Owner.EntityId) { Block("Postać zmieniła się w trakcie transakcji."); return; }
            string stage = (string)journal["stage"];
            if (stage == "reserve")
            {
                if (Matches(inventory, (JObject)journal["before"])) Apply(inventory, (JObject)journal["seTake"], false);
                else if (!Matches(inventory, (JObject)journal["reserved"])) { Block("Ekwipunek zmienił się przed rezerwacją. Nie powtarzam pobierania."); return; }
                if (!Matches(inventory, (JObject)journal["reserved"])) { Block("Niepełna rezerwacja SE."); return; }
                if (!Save()) return;
                Stage("prepare"); return;
            }
            if (stage == "prepare" || stage == "commit" || stage == "abort")
            {
                string operation = stage;
                JObject args = stage == "prepare" ? (JObject)journal["mcPrepare"].DeepClone() : new JObject { {"txId", journal["txId"]} };
                JObject response = Rpc(operation, args);
                if (response == null) return;
                string remote = (string)response["status"]; bool ok = (bool?)response["ok"] ?? false;
                if (remote == "committed") { Stage("grant"); return; }
                if (stage == "prepare")
                {
                    if (ok && remote == "prepared") Stage("commit");
                    else { journal["error"] = (string)response["error"] ?? "Minecraft odrzucił składniki."; Stage("abort"); }
                }
                else if (stage == "abort")
                {
                    if (remote == "aborted" || remote == "unknown") Stage("rollback");
                    else { request = null; nextSend = BridgeIO.Now() + 1000; SetStatus("Czekam na potwierdzenie zwrotu składników Minecrafta."); }
                }
                else if (remote == "aborted") Stage("rollback");
                else { request = null; nextSend = BridgeIO.Now() + 1000; SetStatus("Czekam na potwierdzenie wytworzenia; składniki pozostają zarezerwowane."); }
                return;
            }
            if (stage == "grant")
            {
                if (Matches(inventory, (JObject)journal["reserved"]))
                {
                    var give = Values((JObject)journal["seGive"]); string error = Capacity(inventory, new Dictionary<string, decimal>(), give);
                    if (error != null) { SetStatus(error + " Wynik czeka w transakcji."); return; }
                    Apply(inventory, (JObject)journal["seGive"], true);
                }
                else if (!Matches(inventory, (JObject)journal["after"])) { Block("Stan EQ nie odpowiada potwierdzonej transakcji. Wynik nie został wydany ponownie."); return; }
                if (!Matches(inventory, (JObject)journal["after"])) { Block("Niepełne wydanie wyniku SE."); return; }
                if (!Save()) return;
                Complete("Wytworzono. Składniki pobrano ze wspólnego EQ."); return;
            }
            if (stage == "rollback")
            {
                if (Matches(inventory, (JObject)journal["reserved"])) Apply(inventory, (JObject)journal["seTake"], true);
                else if (!Matches(inventory, (JObject)journal["before"])) { Block("Zwrot czeka na zgodny stan ekwipunku."); return; }
                if (!Matches(inventory, (JObject)journal["before"])) { Block("Niepełny zwrot składników SE."); return; }
                if (!Save()) return;
                Complete("Nie wytworzono; składniki zwrócono. " + UserError((string)journal["error"]));
            }
        }
        bool Save()
        {
            if (MySession.Static.IsSaveInProgress) return false;
            if (!MySession.Static.Save(null, null)) { SetStatus("Czekam na zapis świata przed zatwierdzeniem transakcji."); return false; }
            return !MySession.Static.IsSaveInProgress;
        }
        bool Matches(MyInventory inventory, JObject expected)
        {
            var stock = Stock(inventory);
            foreach (var pair in expected) { decimal amount; stock.TryGetValue(pair.Key, out amount); if (amount != (decimal)pair.Value) return false; }
            return true;
        }
        void Apply(MyInventory inventory, JObject change, bool add)
        {
            foreach (var pair in change)
            {
                decimal amount = (decimal)pair.Value; if (amount <= 0) continue;
                var definition = catalog.Native(pair.Key); if (definition == null) throw new InvalidOperationException("Nieznana definicja: " + pair.Key);
                if (add)
                {
                    var builder = MyObjectBuilderSerializer.CreateNewObject(definition.Id.TypeId, definition.Id.SubtypeName);
                    if (!inventory.AddItems((MyFixedPoint)amount, builder)) throw new InvalidOperationException("Ekwipunek nie przyjął " + pair.Key);
                }
                else
                {
                    decimal remaining = amount;
                    var stacks = new List<MyPhysicalInventoryItem>(inventory.GetItems());
                    foreach (var stack in stacks)
                    {
                        if (stack.Content.GetObjectId() != definition.Id || !CanSpend(stack.Content)) continue;
                        decimal take = Math.Min(remaining, (decimal)stack.Amount);
                        ((VRage.Game.ModAPI.IMyInventory)inventory).RemoveItems(stack.ItemId, (MyFixedPoint?)((MyFixedPoint)take), false, false);
                        var after = inventory.GetItemByID(stack.ItemId); decimal left = after.HasValue ? (decimal)after.Value.Amount : 0;
                        if ((decimal)stack.Amount - left != take) throw new InvalidOperationException("Niepełne pobranie stosu " + pair.Key);
                        remaining -= take; if (remaining <= 0) break;
                    }
                    if (remaining != 0) throw new InvalidOperationException("Niepełne pobranie " + pair.Key);
                }
            }
            signature = null;
        }
        JObject Rpc(string operation, JObject args)
        {
            if (request == null)
            {
                string id = (string)journal["requestId"];
                if (String.IsNullOrEmpty(id)) { id = Guid.NewGuid().ToString("N"); journal["requestId"] = id; WriteJournal(); }
                request = Envelope(id, operation, args);
            }
            string path = Path.Combine(root, "economy-response-v3.json");
            if (File.Exists(path))
            {
                JObject response = JObject.Parse(BridgeIO.ReadSharedText(path));
                if ((string)response["id"] == (string)request["id"] && (response["worldId"] == null || (string)response["worldId"] == (string)request["worldId"])) return response;
            }
            if (BridgeIO.Now() >= nextSend) { nextSend = BridgeIO.Now() + 750; Atomic(Path.Combine(root, "economy-request-v3.json"), request); }
            return null;
        }
        JObject Envelope(string id, string operation, JObject args) { return new JObject { {"version", 3}, {"id", id}, {"worldId", mcState["worldId"]}, {"operation", operation}, {"args", args} }; }
        void Stage(string value) { journal["stage"] = value; journal.Remove("requestId"); request = null; nextSend = 0; WriteJournal(); }
        void Complete(string message)
        {
            journal["stage"] = "complete"; journal["completed"] = BridgeIO.Now(); WriteJournal();
            Directory.CreateDirectory(receiptPath); Atomic(Path.Combine(receiptPath, (string)journal["txId"] + ".json"), journal);
            log("ECONOMY transaction " + (string)journal["txId"] + " complete recipe=" + (string)journal["recipeId"]);
            journal = null; request = null; signature = null; SetStatus(message);
        }
        void WriteJournal() { Atomic(journalPath, journal); }
        void Block(string message) { blocked = true; SetStatus(message + " Transakcja została zachowana do odzyskania."); log("ECONOMY blocked " + (string)journal["txId"] + ": " + message); }
        string SetStatus(string message) { status = message; View["status"] = message; View["busy"] = Busy; return message; }
        public string Select(string itemId)
        {
            if (Busy) return SetStatus("Poczekaj na zakończenie transakcji.");
            JObject exact = null;
            if (itemId != null && itemId.StartsWith("stack:", StringComparison.Ordinal))
            {
                string key = itemId.Substring(6);
                foreach (JObject row in (JArray)View["items"]) if ((string)row["stackKey"] == key) { exact = row; break; }
                if (exact == null) return SetStatus("Ten stos zmienił się lub zniknął. Wybierz go ponownie w EQ.");
                itemId = (string)exact["id"];
            }
            JObject item = catalog.Item(itemId); if (item == null) return SetStatus("Nieznany przedmiot.");
            if ((string)item["engine"] == "SE")
            {
                if (!((bool?)item["selectable"] ?? false)) return SetStatus("Ten przedmiot jest surowcem lub częścią, a nie narzędziem do ręki.");
                var character = MyAPIGateway.Session.Player.Character as MyCharacter; var inventory = Inventory();
                if (character == null || inventory == null) return SetStatus("Brak postaci.");
                foreach (var stack in inventory.GetItems()) if (ItemCatalog.Key(stack.Content.GetObjectId()) == itemId)
                {
                    if (exact != null && ((uint?)exact["nativeItemId"] != stack.ItemId || (string)exact["stackKey"] != "se:" + inventory.Owner.EntityId.ToString() + ":" + stack.ItemId.ToString())) continue;
                    if (!character.CanSwitchToWeapon(stack.Content.GetObjectId())) return SetStatus("Nie można teraz wyposażyć tego narzędzia.");
                    character.SwitchToWeapon(stack.ItemId, true);
                    if (SelectionConfirmed != null) SelectionConfirmed(itemId);
                    return SetStatus("Wyposażono: " + (string)item["name"]);
                }
                return SetStatus("Najpierw wytwórz lub zdobądź ten przedmiot.");
            }
            if (!Fresh()) return SetStatus("Minecraft jest rozłączony.");
            bool owned = false;
            foreach (JObject stack in mcState["slots"] as JArray ?? new JArray())
                if ((string)stack["itemId"] == itemId && ((int?)stack["count"] ?? 0) > 0 && (exact == null || (int?)stack["slot"] == (int?)exact["slot"] && (string)stack["stackHash"] == (string)exact["stackHash"])) { owned = true; break; }
            if (!owned) return SetStatus(exact == null ? "Nie masz tego przedmiotu we wspólnym EQ. Najpierw go zdobądź lub wytwórz." : "Ten stos zmienił się lub zniknął. Wybierz go ponownie w EQ.");
            var args = new JObject { {"itemId", itemId} };
            if (exact != null) { args["slot"] = exact["slot"]; args["stackHash"] = exact["stackHash"]; }
            utility = Envelope(Guid.NewGuid().ToString("N"), "select", args); nextSend = 0;
            return SetStatus("Wybieranie: " + (string)item["name"]);
        }
        void AdvanceUtility()
        {
            if (!Fresh()) return;
            if ((string)utility["worldId"] != (string)mcState["worldId"]) { utility = null; SetStatus("Świat Minecrafta zmienił się przed wykonaniem polecenia."); return; }
            string path = Path.Combine(root, "economy-response-v3.json");
            if (File.Exists(path))
            {
                JObject response = JObject.Parse(BridgeIO.ReadSharedText(path));
                if ((string)response["id"] == (string)utility["id"])
                {
                    bool ok = (bool?)response["ok"] ?? false; string selected = (string)utility["args"]["itemId"]; utility = null; signature = null;
                    if (ok)
                    {
                        confirmedMcSelection = selected; confirmedSelectionAt = (long?)response["timestamp"] ?? BridgeIO.Now();
                        View["selectedItemId"] = selected;
                        if (SelectionConfirmed != null) SelectionConfirmed(selected);
                    }
                    SetStatus(ok ? "Wybrano przedmiot." : UserError((string)response["error"])); return;
                }
            }
            if (BridgeIO.Now() >= nextSend) { nextSend = BridgeIO.Now() + 750; Atomic(Path.Combine(root, "economy-request-v3.json"), utility); }
        }
        string UserError(string technical)
        {
            if (!String.IsNullOrEmpty(technical)) log("ECONOMY native rejection: " + technical);
            string text = (technical ?? "").ToLowerInvariant();
            if (text.Contains("not in the shared inventory") || text.Contains("not owned") || text.Contains("not in inventory"))
                return "Nie masz tego przedmiotu we wspólnym EQ. Najpierw go zdobądź lub wytwórz.";
            if (text.Contains("full") || text.Contains("capacity") || text.Contains("no space") || text.Contains("enough space") || text.Contains("inventory space") || text.Contains("fit in inventory"))
                return "Brakuje miejsca w ekwipunku. Zwolnij miejsce i spróbuj ponownie.";
            if (text.Contains("menu") || text.Contains("container") || text.Contains("carried"))
                return "Zamknij otwartą skrzynię lub warsztat i spróbuj ponownie.";
            if (text.Contains("changed") || text.Contains("hash") || text.Contains("stale"))
                return "Ekwipunek zmienił się w trakcie operacji. Spróbuj ponownie.";
            if (text.Contains("insufficient") || text.Contains("not enough") || text.Contains("missing ingredient"))
                return "Brakuje składników we wspólnym EQ.";
            if (text.Contains("pending") || text.Contains("busy") || text.Contains("prepared transaction") || text.Contains("reserved by transaction") || text.Contains("transaction is active"))
                return "Poczekaj na zakończenie poprzedniej operacji.";
            return "Nie udało się wykonać operacji. Spróbuj ponownie; szczegóły zapisano w dzienniku.";
        }
        public JObject Execute(JObject command)
        {
            string action = (string)command["operation"] ?? (string)command["action"] ?? "inspect";
            if (action == "craft") return new JObject { {"status", Craft((string)command["recipeId"], (int?)command["count"] ?? 1)}, {"busy", Busy} };
            if (action == "select") return new JObject { {"status", Select((string)command["itemId"])}, {"busy", Busy} };
            if (action == "pause-next")
            {
                string stage = (string)command["stage"];
                if (stage != "reserve" && stage != "prepare" && stage != "commit" && stage != "grant" && stage != "rollback" && stage != null) throw new ArgumentException("Invalid transaction pause stage");
                diagnosticPauseStage = stage;
                return new JObject { {"pauseStage", stage}, {"note", "Diagnostic pause is in memory only; runtime reload resumes the saved transaction."} };
            }
            if (action == "grant-se")
            {
                if (Busy) throw new InvalidOperationException("Transaction in progress");
                string id = (string)command["itemId"]; decimal amount = (decimal?)command["amount"] ?? 1; var definition = catalog.Native(id);
                if (definition == null || amount <= 0 || amount > 10000) throw new ArgumentException("Invalid test item or amount");
                var give = new Dictionary<string, decimal> { {id, amount} }; var inventory = Inventory(); string error = Capacity(inventory, new Dictionary<string, decimal>(), give); if (error != null) throw new InvalidOperationException(error);
                Apply(inventory, CraftPlan.Json(give), true); log("ECONOMY explicit diagnostic grant " + amount.ToString(CultureInfo.InvariantCulture) + " " + id); Publish(true);
            }
            if (action == "export") { Atomic(Path.Combine(root, "economy-view-v3.json"), View); return new JObject { {"path", Path.Combine(root, "economy-view-v3.json")} }; }
            var result = new JObject { {"status", status}, {"busy", Busy}, {"blocked", blocked}, {"pauseStage", diagnosticPauseStage}, {"connected", Fresh()}, {"worldId", View["worldId"]}, {"catalogCount", ((JArray)View["catalog"]).Count}, {"recipeCount", ((JArray)View["recipes"]).Count}, {"buildMs", View["buildMs"]}, {"items", View["items"].DeepClone()}, {"transaction", journal == null ? null : journal.DeepClone()} };
            if (action == "search")
            {
                string query = ((string)command["query"] ?? "").ToLowerInvariant(), scope = (string)command["scope"] ?? "recipes";
                int limit = Math.Max(1, Math.Min(100, (int?)command["limit"] ?? 20));
                var rows = new JArray(); JArray source = View[scope == "catalog" ? "catalog" : "recipes"] as JArray;
                foreach (JObject row in source) if (((string)row["id"] ?? "").ToLowerInvariant().Contains(query) || ((string)row["name"] ?? "").ToLowerInvariant().Contains(query)) { rows.Add(row.DeepClone()); if (rows.Count >= limit) break; }
                result["matches"] = rows;
            }
            if ((bool?)command["full"] ?? false) result["view"] = View.DeepClone();
            return result;
        }
        static Dictionary<string, decimal> Values(JObject source) { var result = new Dictionary<string, decimal>(); foreach (var pair in source) result[pair.Key] = (decimal)pair.Value; return result; }
        internal static void Atomic(string path, JObject value)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)); string temporary = path + ".tmp";
            byte[] bytes = Encoding.UTF8.GetBytes(value.ToString(Formatting.None));
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None)) { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
            if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
        }
        static string Hash(string value) { using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-", ""); }
        public void Dispose() { disposed = true; }
    }
}
