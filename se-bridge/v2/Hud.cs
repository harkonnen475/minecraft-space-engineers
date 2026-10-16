using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Sandbox.Game.Gui;
using Sandbox.Game.GUI;
using Sandbox.Graphics;
using Sandbox.ModAPI;
using VRage.Game;
using VRage.Game.ObjectBuilders.Definitions;
using VRage.Utils;
using VRageMath;
using VRageRender;

namespace SEMC.V2
{
    // Native draw calls only: no GUI screen, input focus or persistent render entities.
    public sealed class Hud : IDisposable
    {
        const string Blank = "Textures\\GUI\\Blank.dds";
        static readonly MyStringId Square = MyStringId.GetOrCompute("Square");
        static readonly string[] TextureNames = {
            "dirt", "stone", "oak_planks", "oak_log", "glass", "cobblestone", "bricks", "gold_block",
            "redstone_block", "redstone_lamp", "redstone", "lever", "tnt_side", "crafting_table_front", "chest",
            "piston_top_sticky", "piston_top", "observer_front", "white_wool", "torch", "flint_and_steel",
            "oak_stairs", "oak_slab", "stone_bricks", "oak_door", "oak_fence", "ladder", "grass_block", "sand"
        };
        static readonly Color Mint = new Color(133, 225, 189);
        static readonly Color White = new Color(238, 244, 246);
        static readonly Color Muted = new Color(161, 178, 188);
        readonly string[] icons = new string[TextureNames.Length];
        readonly string[] numbers = new string[TextureNames.Length];
        readonly Action<string> log;
        bool wasVisible;
        long hintUntil, nextError;
        MyGuiScreenHudSpace nativeHud;
        MyHudQuestlog tutorial;
        bool toolbarWasVisible, tutorialWasVisible;
        static readonly FieldInfo HiddenToolbar = typeof(MyGuiScreenHudSpace).GetField("m_hiddenToolbar", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly FieldInfo NativeHudState = typeof(MyHud).GetField("m_hudState", BindingFlags.Static | BindingFlags.NonPublic);
        static readonly FieldInfo NativeStatGroups = typeof(MyGuiScreenHudSpace).GetField("m_statControls", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly FieldInfo NativeStatBindings = typeof(MyStatControls).GetField("m_bindings", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly Type NativeBinding = typeof(MyStatControls).GetNestedType("StatBinding", BindingFlags.Public | BindingFlags.NonPublic);
        static readonly FieldInfo NativeBindingControl = NativeBinding == null ? null : NativeBinding.GetField("Control", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        static readonly FieldInfo NativeBindingStyle = NativeBinding == null ? null : NativeBinding.GetField("Style", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        static readonly string[] BuilderCaptions = { "CUBE_BUILDER_CUBESIZE_MODE", "CUBE_DEFAULT_MOUNTPOINT", "USE_SYMMETRY", "BUILD_SCREEN", "COLOR_PICKER", "INVENTORY" };
        readonly Dictionary<IMyStatControl,Vector4> hiddenBuilderColors = new Dictionary<IMyStatControl,Vector4>();
        long nextStatScan;
        bool nativeStateOverridden;
        int previousNativeState;

        public Hud() : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "SEMC-Lab"), null) { }
        public Hud(string root, Action<string> logger = null)
        {
            log = logger ?? delegate { };
            for (int i = 0; i < TextureNames.Length; i++)
            {
                // The native GUI texture loader expects DDS. World color/metal maps have the wrong alpha for icons.
                string path = Path.Combine(root, "render-assets", "icons", TextureNames[i] + ".dds");
                icons[i] = File.Exists(path) ? path : null;
                numbers[i] = (i + 1).ToString();
            }
        }

        public void SetIcon(int item, string path)
        {
            if (item >= 0 && item < icons.Length) icons[item] = path;
        }

        public void Draw(GameplayInput input, Snapshot data, bool connected)
        {
            bool minecraftMenu = BlockPicker.IsOpen || ContainerBridge.IsOpen;
            if (minecraftMenu && input != null && input.Enabled && connected && data != null && data.Active)
            {
                // A Minecraft modal releases gameplay input, but must not reveal the replaced SE toolbar/tutorial.
                SuppressNativeClutter();
                return;
            }
            bool visible = input != null && input.CanDraw && connected && data != null && data.Active;
            if (visible && MyAPIGateway.Gui != null)
                visible = !MyAPIGateway.Gui.IsCursorVisible && !MyAPIGateway.Gui.ChatEntryVisible;
            if (!visible)
            {
                wasVisible = false;
                bool hybridMode = input != null && input.Enabled;
                RestoreNativeHud(!hybridMode);
                if (hybridMode) SuppressTutorial();
                return;
            }
            if (!wasVisible) { hintUntil = BridgeIO.Now() + 10000; wasVisible = true; }
            try
            {
                SuppressNativeClutter();
                DrawWorld(input);
                DrawPalette(input);
            }
            catch (Exception error)
            {
                if (BridgeIO.Now() >= nextError)
                { nextError = BridgeIO.Now() + 10000; log("HUD " + error.GetType().Name + ": " + error.Message); }
            }
        }

        void DrawWorld(GameplayInput input)
        {
            MatrixD world = input.Frame;
            if (input.HasSelection)
            {
                BoundingBoxD selected = new BoundingBoxD((Vector3D)input.SelectionCell - new Vector3D(.003),
                    (Vector3D)input.SelectionCell + new Vector3D(1.003));
                Color edge = new Color(238, 248, 255, 230);
                MySimpleObjectDraw.DrawTransparentBox(ref world, ref selected, ref edge,
                    MySimpleObjectRasterizer.Wireframe, 1, .008f, Square, Square, false, -1,
                    MyBillboard.BlendTypeEnum.Standard, 1f, null);
            }
            if (input.HasPlacement && input.SelectedItem != 20)
            {
                BoundingBoxD placement = new BoundingBoxD((Vector3D)input.PlacementCell + new Vector3D(.004),
                    (Vector3D)input.PlacementCell + new Vector3D(.996));
                Color edge = new Color(133, 225, 189, 145);
                MySimpleObjectDraw.DrawTransparentBox(ref world, ref placement, ref edge,
                    MySimpleObjectRasterizer.Wireframe, 1, .004f, Square, Square, false, -1,
                    MyBillboard.BlendTypeEnum.Standard, 1f, null);
            }
        }

        void DrawPalette(GameplayInput input)
        {
            Rectangle viewport = MyGuiManager.GetFullscreenRectangle();
            if (viewport.Width < 400 || viewport.Height < 300) return;
            float scale = MathHelper.Clamp(viewport.Height / 1080f, .65f, 1.5f);
            const int slots = 9;
            float slot = 50 * scale, gap = 5 * scale, pad = 14 * scale;
            float width = pad * 2 + slot * slots + gap * (slots - 1);
            float height = 104 * scale;
            float x = viewport.X + (viewport.Width - width) * .5f;
            // Replace the on-foot toolbar while leaving health, oxygen and jetpack indicators intact.
            float y = viewport.Y + viewport.Height - 160 * scale;
            Fill(x, y, width, height, new Color(10, 18, 24, 219));
            Fill(x, y, width, 2 * scale, new Color(91, 154, 140, 210));
            Text("MINECRAFT", x + pad, y + 9 * scale, .51f, Mint);
            Text(input.SelectedItemName, x + pad + 93 * scale, y + 7 * scale, .68f, White);
            Text(numbers[input.SelectedItem] + " / " + icons.Length, x + width - pad, y + 11 * scale, .49f, Muted,
                MyGuiDrawAlignEnum.HORISONTAL_RIGHT_AND_VERTICAL_TOP);

            int first = Math.Max(0, Math.Min(icons.Length - slots, input.SelectedItem - slots / 2));
            float rowY = y + 37 * scale;
            for (int j = 0; j < slots; j++)
            {
                int item = first + j;
                float cellX = x + pad + j * (slot + gap);
                bool selected = item == input.SelectedItem;
                Fill(cellX, rowY, slot, slot, selected ? new Color(41, 71, 70, 245) : new Color(30, 42, 50, 200));
                if (selected) Border(cellX, rowY, slot, slot, 2 * scale, Mint);
                string icon = icons[item];
                if (icon != null)
                {
                    // Unmodified Minecraft textures retain their recognizable pixel art.
                    Sprite(icon, cellX + 9 * scale, rowY + 7 * scale, 32 * scale, 32 * scale, Color.White);
                }
                else Text(GameplayInput.ItemNames[item].Substring(0, 1), cellX + slot * .5f, rowY + 8 * scale,
                    .8f, White, MyGuiDrawAlignEnum.HORISONTAL_CENTER_AND_VERTICAL_TOP);
                Text(numbers[item], cellX + 5 * scale, rowY + 35 * scale, .38f, selected ? Mint : Muted);
            }
            if (first > 0) Text("<", x + 4 * scale, rowY + 17 * scale, .5f, Muted);
            if (first + slots < icons.Length) Text(">", x + width - 11 * scale, rowY + 17 * scale, .5f, Muted);
            string hint = input.SelectedItem == 20 ? "LPM  kop   ·   PPM  podpal   ·   G  paleta   ·   rolka  wybór" : "LPM  kop   ·   PPM  buduj   ·   E  użyj   ·   G  paleta   ·   rolka  wybór";
            if (BridgeIO.Now() < hintUntil) hint += "   ·   Ctrl+Alt+B  tryb";
            Fill(x, y + height + 5 * scale, width, 25 * scale, new Color(10, 18, 24, 185));
            Text(hint, x + width * .5f, y + height + 9 * scale, .44f, White,
                MyGuiDrawAlignEnum.HORISONTAL_CENTER_AND_VERTICAL_TOP);
        }

        static void Text(string text, float x, float y, float scale, Color color,
            MyGuiDrawAlignEnum align = MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP)
        {
            Vector2 normalized = MyGuiManager.GetNormalizedCoordinateFromScreenCoordinate_FULLSCREEN(new Vector2(x, y));
            MyGuiManager.DrawString("White", text, normalized, scale, color, align, true, float.MaxValue, false);
        }
        static void Sprite(string texture, float x, float y, float width, float height, Color color)
        { MyGuiManager.DrawSpriteBatch(texture, new Rectangle((int)x, (int)y, Math.Max(1, (int)width), Math.Max(1, (int)height)), color, false, false); }
        static void Fill(float x, float y, float width, float height, Color color)
        { Sprite(Blank, x, y, width, height, color); }
        static void Border(float x, float y, float width, float height, float thickness, Color color)
        {
            Fill(x, y, width, thickness, color); Fill(x, y + height - thickness, width, thickness, color);
            Fill(x, y, thickness, height, color); Fill(x + width - thickness, y, thickness, height, color);
        }
        void SuppressNativeClutter()
        {
            MyGuiScreenHudSpace current = MyGuiScreenHudSpace.Static;
            if (nativeHud != current)
            {
                RestoreNativeHud(); nativeHud = current;
                if (nativeHud != null) toolbarWasVisible = HiddenToolbar == null || !(bool)HiddenToolbar.GetValue(nativeHud);
            }
            if (nativeHud != null) nativeHud.SetToolbarVisible(false);
            SuppressBuilderStats();
            // Native mode 2 retains suit/status instruments but omits keyboard shortcut captions.
            // Write only the runtime field: the public setter also changes the user's config.
            if (NativeHudState != null)
            {
                int state = MyHud.HudState;
                if (nativeStateOverridden && state != 2) nativeStateOverridden = false;
                if (!nativeStateOverridden && state == 1)
                { previousNativeState = state; NativeHudState.SetValue(null, 2); nativeStateOverridden = true; }
            }
            SuppressTutorial();
        }
        void SuppressBuilderStats()
        {
            if(nativeHud==null || NativeStatGroups==null || NativeStatBindings==null || NativeBindingControl==null || NativeBindingStyle==null)return;
            long now=BridgeIO.Now();
            if(now>=nextStatScan)
            {
                nextStatScan=now+1000;
                var found=new HashSet<IMyStatControl>();
                var groups=NativeStatGroups.GetValue(nativeHud) as IEnumerable;
                if(groups!=null)foreach(object group in groups)
                {
                    var bindings=NativeStatBindings.GetValue(group) as IEnumerable;
                    if(bindings==null)continue;
                    foreach(object binding in bindings)
                    {
                        var style=NativeBindingStyle.GetValue(binding) as MyObjectBuilder_StatVisualStyle;
                        if(!IsBuilderStyle(style))continue;
                        var control=NativeBindingControl.GetValue(binding) as IMyStatControl;
                        if(control==null)continue;
                        found.Add(control);
                        if(!hiddenBuilderColors.ContainsKey(control))hiddenBuilderColors.Add(control,GetStatColor(control));
                    }
                }
                // A resolution/HUD refresh can replace controls without replacing the HUD screen.
                var removed=new List<IMyStatControl>();
                foreach(var pair in hiddenBuilderColors)if(!found.Contains(pair.Key)){SetStatColor(pair.Key,pair.Value);removed.Add(pair.Key);}
                foreach(var control in removed)hiddenBuilderColors.Remove(control);
            }
            foreach(var pair in hiddenBuilderColors)
            { Vector4 color=pair.Value;color.W=0;SetStatColor(pair.Key,color); }
        }
        static bool IsBuilderStyle(MyObjectBuilder_StatVisualStyle style)
        {
            var image=style as MyObjectBuilder_ImageStatVisualStyle;
            if(image!=null)
            {
                string texture=image.Texture.ToString();
                return texture=="GridSizeSmall" || texture=="GridSizeLarge" || texture=="BlockRotate" || texture=="GridMirror";
            }
            var text=style as MyObjectBuilder_TextStatVisualStyle;
            if(text==null || text.Text==null)return false;
            foreach(string caption in BuilderCaptions)if(text.Text.Contains("{GAME_CONTROL:"+caption+"}"))return true;
            return false;
        }
        static Vector4 GetStatColor(IMyStatControl control)
        { var text=control as MyStatControlText;return text==null?control.ColorMask:text.TextColorMask; }
        static void SetStatColor(IMyStatControl control,Vector4 color)
        { var text=control as MyStatControlText;if(text==null)control.ColorMask=color;else text.TextColorMask=color; }
        void RestoreBuilderStats()
        {
            foreach(var pair in hiddenBuilderColors)SetStatColor(pair.Key,pair.Value);
            hiddenBuilderColors.Clear();nextStatScan=0;
        }
        void SuppressTutorial()
        {
            MyHudQuestlog currentTutorial = MyHud.Questlog;
            // Mission objectives created by visual scripting remain visible.
            if (currentTutorial != null && !currentTutorial.IsUsedByVisualScripting)
            {
                if (tutorial != currentTutorial)
                { if (tutorial != null) tutorial.Visible = tutorialWasVisible; tutorial = currentTutorial; tutorialWasVisible = tutorial.Visible; }
                tutorial.Visible = false;
            }
        }
        void RestoreNativeHud(bool restoreTutorial = true)
        {
            RestoreBuilderStats();
            if (nativeHud != null) nativeHud.SetToolbarVisible(toolbarWasVisible);
            if (restoreTutorial && tutorial != null) tutorial.Visible = tutorialWasVisible;
            if (nativeStateOverridden && NativeHudState != null && MyHud.HudState == 2) NativeHudState.SetValue(null, previousNativeState);
            nativeStateOverridden = false;
            nativeHud = null;
            if (restoreTutorial) tutorial = null;
        }
        public void Dispose() { wasVisible = false; RestoreNativeHud(); }
    }
}
