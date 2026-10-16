using System;
using System.IO;
using Sandbox.Graphics.GUI;
using VRage.Game;
using VRage.Utils;
using VRageMath;

namespace SEMC.V2
{
    // A native modal screen owns focus and consumes its click, so selecting never places a block.
    public static class BlockPicker
    {
        static PickerScreen active;
        public static bool IsOpen { get { return active != null; } }

        public static void Open(GameplayInput input, string root, Action<string> log)
        {
            if (active != null || input == null) return;
            PickerScreen screen = new PickerScreen(input, root, log ?? delegate { });
            active = screen;
            screen.Closed += delegate(MyGuiScreenBase closed, bool unloading) { if (ReferenceEquals(active, closed)) active = null; };
            try { MyGuiSandbox.AddScreen(screen); }
            catch { active = null; throw; }
        }

        public static void Close(GameplayInput input)
        {
            PickerScreen screen = active;
            if (screen != null && ReferenceEquals(screen.Input, input))
            { active = null; screen.CloseScreenNow(); }
        }

        sealed class PickerScreen : MyGuiScreenBase
        {
            const string Blank = "Textures\\GUI\\Blank.dds";
            static readonly Vector4 White = new Vector4(.94f, .97f, .98f, 1);
            static readonly Vector4 Muted = new Vector4(.63f, .72f, .76f, 1);
            static readonly Vector4 Mint = new Vector4(.52f, .88f, .74f, 1);
            static readonly string[] Icons = {
                "dirt", "stone", "oak_planks", "oak_log", "glass", "cobblestone", "bricks", "gold_block",
                "redstone_block", "redstone_lamp", "redstone", "lever", "tnt_side", "crafting_table_front", "chest",
                "piston_top_sticky", "piston_top", "observer_front", "white_wool", "torch", "flint_and_steel",
                "oak_stairs", "oak_slab", "stone_bricks", "oak_door", "oak_fence", "ladder", "grass_block", "sand"
            };
            public readonly GameplayInput Input;
            readonly string root;
            readonly Action<string> log;

            public PickerScreen(GameplayInput input, string rootPath, Action<string> logger)
                : base(new Vector2(.5f), new Vector4(.035f, .065f, .085f, .98f), new Vector2(.92f, .79f),
                    isTopMostScreen: true, backgroundTexture: Blank)
            {
                Input = input; root = rootPath; log = logger;
                EnabledBackgroundFade = true;
                m_backgroundFadeColor = new Color(0, 0, 0, 180);
                CanHideOthers = false;
                CanBeHidden = false;
                CanHaveFocus = true;
                DrawMouseCursor = true;
                m_closeOnEsc = true;
                m_canShareInput = false;
                CloseButtonEnabled = true;
                RecreateControls(true);
            }

            public override string GetFriendlyName() { return "SEMC_MinecraftBlockPicker"; }

            public override void RecreateControls(bool constructor)
            {
                base.RecreateControls(constructor);
                if (Input == null) return;
                AddLabel("MINECRAFT", new Vector2(-.432f, -.349f), .98f, White);
                AddLabel("Wybierz blok lub przedmiot", new Vector2(-.432f, -.305f), .65f, Muted);
                AddLabel("29 przedmiotów", new Vector2(.432f, -.305f), .57f, Muted,
                    MyGuiDrawAlignEnum.HORISONTAL_RIGHT_AND_VERTICAL_CENTER);
                MyGuiControlButton selected = null;
                for (int i = 0; i < GameplayInput.ItemNames.Length && i < Icons.Length; i++)
                {
                    int item = i;
                    Vector2 center = new Vector2(-.392f + (i % 9) * .098f, -.189f + (i / 9) * .139f);
                    ItemButton button = new ItemButton(center, i == Input.SelectedItem);
                    button.Name = "MinecraftItem" + i;
                    button.SetToolTip(GameplayInput.ItemNames[i]);
                    button.ButtonClicked += delegate { Choose(item); };
                    Controls.Add(button);
                    if (i == Input.SelectedItem) selected = button;

                    string icon = Path.Combine(root, "render-assets", "icons", Icons[i] + ".dds");
                    if (File.Exists(icon))
                    {
                        MyGuiControlImage image = new MyGuiControlImage(position: new Vector2(0, -.015f),
                            size: new Vector2(.052f, .0693f), backgroundColor: Vector4.One, textures: new[] { icon });
                        Passive(image); button.Elements.Add(image);
                    }
                    // Keep all Polish names legible: two short lines rather than truncation.
                    string[] words = SplitName(GameplayInput.ItemNames[i]);
                    for (int line = 0; line < words.Length; line++)
                    {
                        MyGuiControlLabel label = new MyGuiControlLabel(position: new Vector2(0, .031f + line * .017f),
                            text: words[line], colorMask: i == Input.SelectedItem ? Mint : White,
                            textScale: .51f, font: "White", originAlign: MyGuiDrawAlignEnum.HORISONTAL_CENTER_AND_VERTICAL_CENTER,
                            isAutoScaleEnabled: true, maxWidth: .083f, minimumTextScale: .4f);
                        Passive(label); button.Elements.Add(label);
                    }
                }
                AddLabel("Kliknij, aby wybrać  ·  Esc — wróć do gry", new Vector2(-.432f, .351f), .56f, Muted);
                AddLabel("Wybrano: " + Input.SelectedItemName, new Vector2(.432f, .351f), .56f, Mint,
                    MyGuiDrawAlignEnum.HORISONTAL_RIGHT_AND_VERTICAL_CENTER);
                if (selected != null) FocusedControl = selected;
            }

            void Choose(int item)
            {
                try { Input.SelectItem(item); log("PICKER selected " + item + " " + GameplayInput.ItemNames[item]); }
                catch (Exception error) { log("PICKER " + error.GetType().Name + ": " + error.Message); }
                CloseScreen();
            }

            void AddLabel(string text, Vector2 position, float scale, Vector4 color,
                MyGuiDrawAlignEnum align = MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_CENTER)
            {
                MyGuiControlLabel label = new MyGuiControlLabel(position: position, text: text,
                    textScale: scale, colorMask: color, font: "White", originAlign: align);
                Passive(label); Controls.Add(label);
            }
            static void Passive(MyGuiControlBase control)
            { control.CanHaveFocus = false; control.IsHitTestVisible = false; control.SkipForMouseTest = true; }
            static string[] SplitName(string name)
            {
                if (name.Length < 12 || name.IndexOf(' ') < 0) return new[] { name };
                int split = name.LastIndexOf(' ', Math.Min(11, name.Length - 1));
                if (split < 0) split = name.IndexOf(' ');
                return new[] { name.Substring(0, split), name.Substring(split + 1) };
            }

            sealed class ItemButton : MyGuiControlButton
            {
                readonly bool selected;
                // Native base propagates a button tint into all child icons and labels.
                // This card owns its background colour; child artwork keeps its own colour.
                protected override void OnColorMaskChanged() { }
                public ItemButton(Vector2 position, bool isSelected)
                    : base(position: position, activateOnMouseRelease: true)
                {
                    selected = isSelected;
                    MyGuiCompositeTexture texture = new MyGuiCompositeTexture(Blank);
                    CustomStyle = new StyleDefinition {
                        NormalFont = "White", HighlightFont = "White",
                        NormalTexture = texture, HighlightTexture = texture, FocusTexture = texture, ActiveTexture = texture,
                        SizeOverride = new Vector2(.09f, .127f), BackgroundColor = Vector4.One
                    };
                    BorderEnabled = true; BorderSize = 1; BorderHighlightEnabled = false;
                }
                public override void Draw(float transitionAlpha, float backgroundTransitionAlpha)
                {
                    bool hover = HasHighlight || HasFocus;
                    ColorMask = selected ? new Vector4(.16f, .29f, .27f, 1) :
                        hover ? new Vector4(.17f, .24f, .28f, 1) : new Vector4(.08f, .13f, .17f, 1);
                    BorderColor = selected || hover ? Mint : new Vector4(.19f, .27f, .31f, 1);
                    base.Draw(transitionAlpha, backgroundTransitionAlpha);
                }
            }
        }
    }
}
