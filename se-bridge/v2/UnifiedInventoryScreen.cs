using System;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json.Linq;
using Sandbox.Graphics.GUI;
using VRage.Utils;
using VRageMath;

namespace SEMC.V2
{
    // Controls are bounded by the page size, independently of registry size.
    public static class UnifiedInventoryScreen
    {
        static InventoryScreen active;
        public static bool IsOpen { get { return active != null; } }
        public static void Open(string root, Func<JObject> view, Action<string> select,
            Action<string, int> craft, Action<string> log, bool catalogOnly = false)
        {
            if (active != null) return;
            var screen = new InventoryScreen(root, view, select, craft, log ?? delegate { }, catalogOnly ? 0 : 1);
            active = screen;
            screen.Closed += delegate(MyGuiScreenBase closed, bool unloading) { if (ReferenceEquals(active, closed)) active = null; };
            try { MyGuiSandbox.AddScreen(screen); }
            catch { active = null; throw; }
        }
        public static void Close()
        { InventoryScreen screen = active; active = null; if (screen != null) screen.CloseScreenNow(); }
        public static void OpenCrafting(string root, Func<JObject> view, Action<string> select,
            Action<string, int> craft, Action<string> log)
        {
            Close(); Open(root, view, select, craft, log);
            if (active != null) active.SwitchTab(2);
        }

        sealed class InventoryScreen : MyGuiScreenBase
        {
            const string Blank = "Textures\\GUI\\Blank.dds";
            const int GridPageSize = 40, RecipePageSize = 8;
            static readonly Vector4 White = new Vector4(.94f, .97f, .98f, 1), Muted = new Vector4(.63f, .72f, .76f, 1), Mint = new Vector4(.52f, .88f, .74f, 1);
            readonly Func<JObject> getView;
            readonly Action<string> select;
            readonly Action<string, int> craft;
            readonly Action<string> log;
            readonly ItemIcons icons;
            readonly List<MyGuiControlBase> body = new List<MyGuiControlBase>();
            readonly Dictionary<string, JObject> catalog = new Dictionary<string, JObject>(StringComparer.Ordinal);
            readonly Dictionary<string, decimal> owned = new Dictionary<string, decimal>(StringComparer.Ordinal);
            readonly List<JObject> filtered = new List<JObject>();
            JObject view = new JObject();
            MyGuiControlTextbox search;
            MyGuiControlLabel countLabel, pageLabel, statusLabel;
            readonly ActionButton[] tabs = new ActionButton[3];
            ActionButton previous, next;
            int tab, page, pages = 1, sourceFilter;
            string query = "", selectedRecipe = "", renderedRevision, localStatus;
            long nextRefresh, searchDue, localStatusUntil;
            bool rebuilding, dirty = true;
            bool renderedBusy;

            public InventoryScreen(string root, Func<JObject> provider, Action<string> onSelect,
                Action<string, int> onCraft, Action<string> logger, int initialTab)
                : base(new Vector2(.5f), new Vector4(.035f, .065f, .085f, .99f), new Vector2(.96f, .92f), true, Blank)
            {
                getView = provider; select = onSelect; craft = onCraft; log = logger; tab = initialTab;
                icons = new ItemIcons(root);
                EnabledBackgroundFade = true; m_backgroundFadeColor = new Color(0, 0, 0, 185);
                CanHideOthers = false; CanBeHidden = false; CanHaveFocus = true; DrawMouseCursor = true;
                m_closeOnEsc = true; m_canShareInput = false; CloseButtonEnabled = true;
                RecreateControls(true);
            }
            public override string GetFriendlyName() { return "SEMC_UnifiedInventory"; }
            public override void RecreateControls(bool constructor)
            {
                base.RecreateControls(constructor);
                if (icons == null) return;
                body.Clear();
                Label("MINECRAFT × SPACE ENGINEERS", new Vector2(-.435f, -.412f), .82f, White, false);
                Label("Przedmioty i receptury obu światów", new Vector2(-.435f, -.375f), .52f, Muted, false);
                string[] names = { "KATALOG", "EKWIPUNEK", "WYTWARZANIE" };
                for (int i = 0; i < 3; i++)
                {
                    int index = i;
                    tabs[i] = Button(names[i], new Vector2(-.295f + i * .294f, -.320f), new Vector2(.276f, .047f), delegate { SwitchTab(index); }, false);
                    tabs[i].Selected = i == tab;
                }
                Label("Szukaj", new Vector2(-.435f, -.259f), .52f, Muted, false);
                search = new MyGuiControlTextbox(position: new Vector2(-.073f, -.259f), defaultText: query, maxLength: 100, textScale: .64f);
                search.Size = new Vector2(.568f, .043f);
                search.Name = "SEMC_Search";
                search.TextChanged += delegate { if (!rebuilding) { query = search.Text ?? ""; page = 0; searchDue = BridgeIO.Now() + 180; dirty = true; } };
                Controls.Add(search);
                Button("Wszystkie / MC / SE", new Vector2(.333f, -.259f), new Vector2(.207f, .043f), delegate { sourceFilter = (sourceFilter + 1) % 3; page = 0; dirty = true; }, false);
                countLabel = Label("", new Vector2(-.435f, -.210f), .49f, Muted, false);
                previous = Button("‹", new Vector2(-.083f, .356f), new Vector2(.05f, .035f), delegate { if (page > 0) { page--; dirty = true; } }, false);
                next = Button("›", new Vector2(.083f, .356f), new Vector2(.05f, .035f), delegate { if (page + 1 < pages) { page++; dirty = true; } }, false);
                pageLabel = Label("", new Vector2(0, .356f), .5f, Muted, false, MyGuiDrawAlignEnum.HORISONTAL_CENTER_AND_VERTICAL_CENTER);
                statusLabel = Label("", new Vector2(-.435f, .403f), .47f, Mint, false, MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_CENTER, .765f);
                Label("Esc — gra", new Vector2(.435f, .403f), .48f, Muted, false, MyGuiDrawAlignEnum.HORISONTAL_RIGHT_AND_VERTICAL_CENTER);
                nextRefresh = 0; dirty = true; RefreshSnapshot(); BuildBody();
                FocusedControl = search;
            }
            public void SwitchTab(int value)
            {
                if (tab == value) return;
                tab = value; page = 0; selectedRecipe = ""; dirty = true;
                for (int i = 0; i < tabs.Length; i++) if (tabs[i] != null) tabs[i].Selected = i == tab;
            }
            public override bool Update(bool hasFocus)
            {
                bool result = base.Update(hasFocus);
                long now = BridgeIO.Now();
                if (now >= nextRefresh) RefreshSnapshot();
                bool busy = (bool?)view["busy"] == true;
                if (busy != renderedBusy) { renderedBusy = busy; if (tab == 2) dirty = true; }
                if (dirty && now >= searchDue) BuildBody();
                string message = now < localStatusUntil ? localStatus : (string)view["status"];
                if (String.IsNullOrEmpty(message)) message = tab == 0 ? "Katalog nie dodaje przedmiotów. Wybierz posiadany przedmiot." : tab == 1 ? "Kliknij narzędzie lub przedmiot, aby go wybrać." : "Receptury korzystają ze wspólnych zasobów.";
                if ((bool?)view["busy"] == true) message = "Trwa przenoszenie lub wytwarzanie…";
                statusLabel.Text = message;
                return result;
            }
            void RefreshSnapshot()
            {
                nextRefresh = BridgeIO.Now() + 500;
                try
                {
                    JObject updated = getView == null ? null : getView();
                    if (updated == null) { view = new JObject(new JProperty("status", "Katalog jest przygotowywany…")); return; }
                    string revision = (string)updated["revision"];
                    bool changed = revision == null || revision != renderedRevision;
                    view = updated;
                    if (!changed) return;
                    renderedRevision = revision; catalog.Clear(); owned.Clear();
                    JArray entries = view["catalog"] as JArray;
                    if (entries != null) foreach (JObject item in entries) { string id = (string)item["id"]; if (!String.IsNullOrEmpty(id)) catalog[id] = item; }
                    JArray items = view["items"] as JArray;
                    if (items != null) foreach (JObject item in items)
                    {
                        string id = (string)item["id"]; if (String.IsNullOrEmpty(id)) continue;
                        decimal amount; owned.TryGetValue(id, out amount); owned[id] = amount + Number(item["amount"] ?? item["count"]);
                    }
                    dirty = true;
                }
                catch (Exception error) { LocalMessage("Nie udało się odczytać ekwipunku.", error); }
            }
            void BuildBody()
            {
                rebuilding = true;
                try
                {
                    foreach (MyGuiControlBase control in body) Controls.Remove(control);
                    body.Clear(); filtered.Clear();
                    JArray source = view[tab == 0 ? "catalog" : tab == 1 ? "items" : "recipes"] as JArray;
                    if (source != null) foreach (JObject item in source)
                    {
                        if (tab == 1 && Number(item["amount"] ?? item["count"]) <= 0) continue;
                        string engine = Engine(item);
                        if (sourceFilter == 1 && engine != "MC" || sourceFilter == 2 && engine != "SE") continue;
                        string searchable = DisplayName(item) + " " + (string)item["id"];
                        if (searchable.IndexOf(query.Trim(), StringComparison.CurrentCultureIgnoreCase) < 0) continue;
                        filtered.Add(item);
                    }
                    filtered.Sort(delegate(JObject a, JObject b) { return StringComparer.CurrentCultureIgnoreCase.Compare(DisplayName(a), DisplayName(b)); });
                    int pageSize = tab == 2 ? RecipePageSize : GridPageSize;
                    pages = Math.Max(1, (filtered.Count + pageSize - 1) / pageSize); page = Math.Min(page, pages - 1);
                    string filter = sourceFilter == 1 ? "Minecraft" : sourceFilter == 2 ? "Space Engineers" : "Obie gry";
                    countLabel.Text = filter + "  ·  " + filtered.Count + (tab == 2 ? " receptur" : " pozycji");
                    pageLabel.Text = (page + 1) + " / " + pages; previous.Enabled = page > 0; next.Enabled = page + 1 < pages;
                    if (filtered.Count == 0)
                    {
                        Label(tab == 1 && query.Length == 0 ? "Ekwipunek jest pusty." : "Brak wyników.", new Vector2(0, .02f), .7f, White, true, MyGuiDrawAlignEnum.HORISONTAL_CENTER_AND_VERTICAL_CENTER);
                        Label("Zmień wyszukiwanie lub filtr gry.", new Vector2(0, .065f), .5f, Muted, true, MyGuiDrawAlignEnum.HORISONTAL_CENTER_AND_VERTICAL_CENTER);
                    }
                    else if (tab == 2) BuildRecipes(); else BuildGrid();
                    dirty = false;
                }
                finally { rebuilding = false; }
            }
            void BuildGrid()
            {
                for (int offset = 0; offset < GridPageSize && page * GridPageSize + offset < filtered.Count; offset++)
                {
                    JObject item = filtered[page * GridPageSize + offset]; string id = (string)item["id"];
                    Vector2 center = new Vector2(-.39725f + (offset % 8) * .1135f, -.147f + (offset / 8) * .1f);
                    var button = new ActionButton(center, new Vector2(.104f, .091f));
                    string name = DisplayName(item), engine = Engine(item); decimal count = tab == 1 ? Number(item["amount"] ?? item["count"]) : Owned(id);
                    button.Name = "SEMC_Item_" + offset; button.SetToolTip(name + "\n" + id + "\nPosiadasz: " + Format(count) + Unit(item));
                    button.ButtonClicked += delegate { Choose(item); };
                    AddBody(button);
                    string icon = Icon(item);
                    if (icon != null) AddChildImage(button, icon, new Vector2(0, -.015f), new Vector2(.033f, .044f));
                    else AddChildLabel(button, engine, new Vector2(0, -.015f), .57f, Muted, .085f);
                    AddChildLabel(button, name, new Vector2(0, .025f), .43f, White, .093f);
                    if (count > 0) AddChildLabel(button, Format(count), new Vector2(.046f, -.031f), .39f, Mint, .073f, MyGuiDrawAlignEnum.HORISONTAL_RIGHT_AND_VERTICAL_CENTER);
                    if (count == 0 && tab == 0) button.SetToolTip(name + "\n" + id + "\nNie posiadasz tego przedmiotu. Znajdź jego recepturę w zakładce Wytwarzanie.");
                }
            }
            void Choose(JObject item)
            {
                string id = (string)item["id"];
                if ((bool?)view["busy"] == true) { LocalMessage("Poczekaj na zakończenie poprzedniej operacji.", null); return; }
                if ((bool?)item["selectable"] == false)
                { LocalMessage("Ten materiał służy do wytwarzania. Wybierz recepturę w zakładce Wytwarzanie.", null); return; }
                if (Engine(item) == "MC" && (bool?)view["connected"] == false)
                { LocalMessage("Poczekaj na ponowne połączenie Minecrafta.", null); return; }
                if (Owned(id) <= 0) { LocalMessage("Nie posiadasz tego przedmiotu. Możesz wyszukać jego recepturę.", null); return; }
                if (select == null) { LocalMessage("Wybór przedmiotu jest chwilowo niedostępny.", null); return; }
                try { select(SelectionReference(item,tab==1)); CloseScreen(); }
                catch (Exception error) { LocalMessage("Nie udało się wybrać przedmiotu.", error); }
            }
            static string SelectionReference(JObject item,bool inventoryTab)
            {
                string stackKey=(string)item["stackKey"];
                return inventoryTab && !String.IsNullOrEmpty(stackKey) ? "stack:"+stackKey : (string)item["id"];
            }
            void BuildRecipes()
            {
                JObject chosen = null;
                foreach (JObject recipe in filtered) if ((string)recipe["id"] == selectedRecipe) { chosen = recipe; break; }
                if (chosen == null) { chosen = filtered[page * RecipePageSize]; selectedRecipe = (string)chosen["id"]; }
                for (int offset = 0; offset < RecipePageSize && page * RecipePageSize + offset < filtered.Count; offset++)
                {
                    JObject recipe = filtered[page * RecipePageSize + offset]; string id = (string)recipe["id"];
                    var button = new ActionButton(new Vector2(-.231f, -.161f + offset * .059f), new Vector2(.416f, .052f));
                    button.Selected = id == selectedRecipe; button.Name = "SEMC_Recipe_" + offset;
                    button.ButtonClicked += delegate { selectedRecipe = id; dirty = true; };
                    button.SetToolTip(DisplayName(recipe) + "\n" + id); AddBody(button);
                    JObject output = FirstOutput(recipe); string icon = output == null ? null : Icon(output);
                    if (icon != null) AddChildImage(button, icon, new Vector2(-.181f, 0), new Vector2(.027f, .036f));
                    AddChildLabel(button, DisplayName(recipe), new Vector2(-.153f, -.006f), .51f, White, .29f, MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_CENTER);
                    bool possible = (bool?)recipe["craftable"] ?? (bool?)recipe["canCraft"] ?? false;
                    string availability = possible ? "Dostępna" : (bool?)recipe["supported"] == false ? "Wymaga specjalnej mechaniki" : "Brak składników / stanowiska";
                    AddChildLabel(button, Engine(recipe) + "  ·  " + availability, new Vector2(-.153f, .014f), .35f, possible ? Mint : Muted, .33f, MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_CENTER);
                }
                BuildRecipeDetails(chosen);
            }
            void BuildRecipeDetails(JObject recipe)
            {
                Label(DisplayName(recipe), new Vector2(.017f, -.164f), .64f, White, true, MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_CENTER, .414f);
                var outputs = recipe["outputs"] as JArray; string outputText = "Wynik: ";
                if (outputs != null) foreach (JObject output in outputs) { if (outputText != "Wynik: ") outputText += ", "; outputText += Format(Number(output["amount"] ?? output["count"])) + " × " + DisplayName(output); }
                Label(outputText, new Vector2(.017f, -.123f), .48f, Mint, true, MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_CENTER, .414f);
                string station = (string)recipe["station"];
                if (!String.IsNullOrEmpty(station)) Label("Stanowisko: " + station, new Vector2(.017f, -.092f), .43f, Muted, true, MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_CENTER, .414f);
                Label("SKŁADNIKI", new Vector2(.017f, -.051f), .42f, Muted, true);
                JArray inputs = GroupIngredients(recipe["inputs"] as JArray ?? recipe["ingredients"] as JArray); int line = 0;
                if (inputs != null) foreach (JObject input in inputs)
                {
                    JArray alternatives = input["alternatives"] as JArray;
                    string id = alternatives != null && alternatives.Count > 0 ? (string)alternatives[0] : (string)input["id"];
                    decimal required = Number(input["amount"] ?? input["count"]), available = input["available"] != null ? Number(input["available"]) : OwnedAlternatives(input, id);
                    string name = DisplayName(input); if (String.IsNullOrEmpty((string)input["name"]) && id != null) name = ItemName(id);
                    if (alternatives != null && alternatives.Count > 1) name += " lub zamiennik";
                    string ingredient = Format(required) + " × " + name + "  (masz " + Format(available) + ")";
                    if (line < 9) Label(ingredient, new Vector2(.017f, -.014f + line * .029f), .45f, available >= required ? White : new Vector4(.95f, .66f, .48f, 1), true, MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_CENTER, .414f);
                    line++;
                }
                if (line > 9) Label("+ " + (line - 9) + " dalszych składników", new Vector2(.017f, .246f), .41f, Muted, true);
                string reason = (string)recipe["reason"];
                bool enabled = ((bool?)recipe["craftable"] ?? (bool?)recipe["canCraft"] ?? false) && (bool?)view["busy"] != true;
                if (!enabled && !String.IsNullOrEmpty(reason)) Label(reason, new Vector2(.017f, .273f), .41f, Muted, true, MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_CENTER, .414f);
                string recipeId = (string)recipe["id"];
                var once = Button("Wytwórz ×1", new Vector2(.112f, .316f), new Vector2(.19f, .043f), delegate { Craft(recipeId, 1); }, true);
                var ten = Button("Wytwórz ×10", new Vector2(.329f, .316f), new Vector2(.19f, .043f), delegate { Craft(recipeId, 10); }, true);
                once.Enabled = ten.Enabled = enabled && craft != null;
                once.SetToolTip("Wykonaj recepturę jeden raz; liczba produktów jest pokazana powyżej.");
                ten.SetToolTip("Wykonaj recepturę dziesięć razy, jeżeli wystarczy zasobów.");
            }
            void Craft(string id, int count)
            {
                if (craft == null || (bool?)view["busy"] == true) return;
                try { craft(id, count); nextRefresh = 0; dirty = true; }
                catch (Exception error) { LocalMessage("Nie udało się wytworzyć przedmiotu.", error); }
            }
            decimal OwnedAlternatives(JObject input, string fallback)
            {
                JArray alternatives = input["alternatives"] as JArray;
                if (alternatives == null) return Owned(fallback);
                decimal amount = 0; var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (JToken alternative in alternatives) { string id = (string)alternative; if (id != null && seen.Add(id)) amount += Owned(id); }
                return amount;
            }
            // Shaped recipes may repeat the same ingredient in several cells. Combine
            // their labels only; native planning and reservation retain the exact grid.
            static JArray GroupIngredients(JArray inputs)
            {
                var result=new JArray();
                if(inputs==null)return result;
                var groups=new Dictionary<string,JObject>(StringComparer.Ordinal);
                int anonymous=0;
                foreach(JObject input in inputs)
                {
                    var alternatives=input["alternatives"] as JArray;
                    var ids=new List<string>();
                    if(alternatives!=null)foreach(JToken option in alternatives){string id=(string)option;if(id!=null&&!ids.Contains(id))ids.Add(id);}
                    if(ids.Count==0 && input["id"]!=null)ids.Add((string)input["id"]);
                    ids.Sort(StringComparer.Ordinal);
                    string key=ids.Count==0 ? "anonymous:"+(anonymous++) : String.Join("\0",ids);
                    JObject grouped;
                    decimal amount=Number(input["amount"]??input["count"]);
                    if(groups.TryGetValue(key,out grouped))grouped["amount"]=Number(grouped["amount"])+amount;
                    else
                    {
                        grouped=(JObject)input.DeepClone();grouped["amount"]=amount;
                        groups[key]=grouped;result.Add(grouped);
                    }
                }
                return result;
            }
            decimal Owned(string id) { decimal amount; return id != null && owned.TryGetValue(id, out amount) ? amount : 0; }
            string ItemName(string id) { JObject item; return id != null && catalog.TryGetValue(id, out item) ? DisplayName(item) : Humanize(id); }
            string DisplayName(JObject item)
            {
                string name = (string)item["name"] ?? (string)item["displayName"];
                if (!String.IsNullOrEmpty(name)) return name;
                string id = (string)item["id"]; JObject found;
                if (id != null && catalog.TryGetValue(id, out found) && !ReferenceEquals(item, found)) return DisplayName(found);
                return Humanize(id);
            }
            string Icon(JObject item)
            {
                string id = (string)item["id"], icon = (string)item["icon"]; JObject found;
                if (String.IsNullOrEmpty(icon) && id != null && catalog.TryGetValue(id, out found)) icon = (string)found["icon"];
                return icons.Get(id, icon);
            }
            static JObject FirstOutput(JObject recipe) { JArray output = recipe["outputs"] as JArray; return output == null || output.Count == 0 ? null : output[0] as JObject; }
            static string Unit(JObject item) { string unit = (string)item["unit"]; return String.IsNullOrEmpty(unit) ? "" : " " + unit; }
            static string Engine(JObject item)
            {
                string engine = (string)item["engine"] ?? (string)item["source"] ?? "", id = (string)item["id"] ?? "";
                return engine.Equals("minecraft", StringComparison.OrdinalIgnoreCase) || engine.Equals("mc", StringComparison.OrdinalIgnoreCase) || id.StartsWith("minecraft:", StringComparison.Ordinal) ? "MC" : "SE";
            }
            static string Humanize(string id)
            {
                if (String.IsNullOrEmpty(id)) return "Przedmiot";
                int split = Math.Max(id.LastIndexOf(':'), id.LastIndexOf('/'));
                return id.Substring(split + 1).Replace('_', ' ');
            }
            static decimal Number(JToken token) { decimal value; return token != null && Decimal.TryParse(token.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value) ? value : 0; }
            static string Format(decimal value) { return value.ToString(value >= 10000 ? "0" : "0.###", CultureInfo.CurrentCulture); }
            void LocalMessage(string message, Exception error)
            { localStatus = message; localStatusUntil = BridgeIO.Now() + 5000; if (error != null) log("UNIFIED UI " + error.GetType().Name + ": " + error.Message); }
            void AddBody(MyGuiControlBase control) { body.Add(control); Controls.Add(control); }
            MyGuiControlLabel Label(string text, Vector2 position, float scale, Vector4 color, bool dynamic,
                MyGuiDrawAlignEnum align = MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_CENTER, float maxWidth = .85f)
            {
                var label = new MyGuiControlLabel(position: position, text: text, textScale: scale, colorMask: color, font: "White", originAlign: align,
                    isAutoScaleEnabled: true, maxWidth: maxWidth, minimumTextScale: Math.Min(scale, .33f));
                Passive(label); if (dynamic) AddBody(label); else Controls.Add(label); return label;
            }
            ActionButton Button(string text, Vector2 center, Vector2 size, Action action, bool dynamic)
            {
                var button = new ActionButton(center, size); button.ButtonClicked += delegate { action(); };
                AddChildLabel(button, text, Vector2.Zero, .53f, White, size.X - .014f);
                if (dynamic) AddBody(button); else Controls.Add(button); return button;
            }
            static void AddChildLabel(MyGuiControlButton parent, string text, Vector2 position, float scale, Vector4 color, float maxWidth,
                MyGuiDrawAlignEnum align = MyGuiDrawAlignEnum.HORISONTAL_CENTER_AND_VERTICAL_CENTER)
            {
                var label = new MyGuiControlLabel(position: position, text: text, textScale: scale, colorMask: color, font: "White", originAlign: align,
                    isAutoScaleEnabled: true, maxWidth: maxWidth, minimumTextScale: Math.Min(scale, .31f));
                Passive(label); parent.Elements.Add(label);
            }
            static void AddChildImage(MyGuiControlButton parent, string path, Vector2 position, Vector2 size)
            {
                var image = new MyGuiControlImage(position: position, size: size, backgroundColor: Vector4.One, textures: new[] { path });
                Passive(image); parent.Elements.Add(image);
            }
            static void Passive(MyGuiControlBase control) { control.CanHaveFocus = false; control.IsHitTestVisible = false; control.SkipForMouseTest = true; }
            sealed class ActionButton : MyGuiControlButton
            {
                public new bool Selected;
                public ActionButton(Vector2 position, Vector2 size) : base(position: position, activateOnMouseRelease: true)
                {
                    var texture = new MyGuiCompositeTexture(Blank);
                    CustomStyle = new StyleDefinition { NormalFont = "White", HighlightFont = "White", NormalTexture = texture, HighlightTexture = texture,
                        FocusTexture = texture, ActiveTexture = texture, SizeOverride = size, BackgroundColor = Vector4.One };
                    BorderEnabled = true; BorderSize = 1; BorderHighlightEnabled = false;
                }
                protected override void OnColorMaskChanged() { }
                public override void Draw(float alpha, float backgroundAlpha)
                {
                    bool hover = Enabled && (HasHighlight || HasFocus);
                    ColorMask = Selected ? new Vector4(.16f, .29f, .27f, 1) : hover ? new Vector4(.17f, .24f, .28f, 1) : new Vector4(.08f, .13f, .17f, 1);
                    BorderColor = Selected || hover ? Mint : new Vector4(.19f, .27f, .31f, 1);
                    base.Draw(alpha, backgroundAlpha);
                }
            }
        }
    }
}
