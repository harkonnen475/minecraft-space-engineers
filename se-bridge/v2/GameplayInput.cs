using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using Sandbox.Game;
using Sandbox.Game.Entities.Character;
using Sandbox.ModAPI;
using VRage.Game;
using VRage.Game.ModAPI;
using VRage.Input;
using VRage.Audio;
using VRage.Utils;
using VRageMath;

namespace SEMC.V2
{
    // Call Update when a snapshot arrives and HandleInput from IHandleInputPlugin.HandleInput.
    // All coordinates exposed to the host as selections are snapshot-local; outgoing commands are absolute MC.
    public sealed class GameplayInput : IDisposable
    {
        public static readonly string[] ItemNames = {
            "Ziemia", "Kamień", "Dębowe deski", "Dębowy pień", "Szkło", "Bruk", "Cegły", "Złoto",
            "Blok redstone", "Lampa redstone", "Redstone", "Dźwignia", "TNT", "Stół rzemieślniczy", "Skrzynia",
            "Lepki tłok", "Tłok", "Obserwator", "Biała wełna", "Pochodnia", "Krzesiwo",
            "Dębowe schody", "Dębowa płyta", "Kamienne cegły", "Dębowe drzwi", "Dębowy płot", "Drabina", "Trawa", "Piasek"
        };
        readonly PhysicsWorld physics;
        readonly Action<BridgeCommand> issue;
        readonly Action<string> log;
        readonly MyStringId[] blockedControls = {
            MyControlsSpace.PRIMARY_TOOL_ACTION, MyControlsSpace.SECONDARY_TOOL_ACTION,
            MyControlsSpace.BUILD_SCREEN
        };
        readonly bool[] previousBlocks = new bool[3];
        readonly List<IHitInfo> aimHits = new List<IHitInfo>();
        readonly SortedDictionary<int,JObject> catalog = new SortedDictionary<int,JObject>();
        readonly Dictionary<string,int> catalogIds = new Dictionary<string,int>(StringComparer.Ordinal);
        readonly HashSet<string> ownedItems = new HashSet<string>(StringComparer.Ordinal);
        string preferredItemId;
        bool inventoryHeld, previousInventory;
        public Action OpenInventory;
        public Action OpenCrafting;
        public Action<string> RequestSelection;
        public Func<bool> EconomyBusy;
        const string Preferences = @"C:\Users\konra\Documents\SEMC-Lab\player-preferences-v2.json";
        Snapshot snapshot;
        MatrixD frame;
        bool enabled, controlsHeld, rollHeld, previousRoll;
        long nextStatus, nextAction, nextSound;
        Vector3D cameraPosition, cameraDirection;
        IHitInfo terrainHit;
        public bool Enabled { get { return enabled; } set { SetEnabled(value); } }
        public bool CanDraw { get { return enabled && controlsHeld; } }
        public MatrixD Frame { get { return frame; } }
        public int SelectedItem { get; private set; }
        public string SelectedItemName { get { return ItemName(SelectedItem); } }
        public string SelectedItemId { get { JObject item; return catalog.TryGetValue(SelectedItem,out item) ? (string)item["id"] : preferredItemId; } }
        public int ItemCount { get { return catalog.Count == 0 ? ItemNames.Length : catalog.Count; } }
        public bool SelectedIsBlock { get { if(SelectedItem<0)return false; JObject item; return !catalog.TryGetValue(SelectedItem,out item) ? SelectedItem != 20 : ((bool?)item["block"] ?? (bool?)item["placeable"] ?? item["blockStateId"] != null); } }
        public bool HasSelection { get; private set; }
        public bool HasPlacement { get; private set; }
        public Vector3I SelectionCell { get; private set; }
        public Vector3I PlacementCell { get; private set; }
        public Vector3I SelectionNormal { get; private set; }
        public Vector3 HitFractions { get; private set; }
        public Vector3D HitWorld { get; private set; }

        public GameplayInput(PhysicsWorld physicsWorld, Action<BridgeCommand> commandSink, Action<string> logger)
        {
            physics = physicsWorld; issue = commandSink; log = logger ?? delegate { };
            SelectedItem=-1;
            try
            {
                if (File.Exists(Preferences))
                {
                    JObject saved = JObject.Parse(BridgeIO.ReadSharedText(Preferences));
                    int selected = (int?)saved["selectedItem"] ?? 0;
                    preferredItemId = (string)saved["selectedItemId"];
                    if (selected >= -1 && selected < ItemNames.Length) SelectedItem = selected;
                }
            }
            catch (Exception error) { log("PREFERENCES read: " + error.Message); }
        }

        public void Update(Snapshot data, MatrixD worldFrame)
        {
            snapshot = data; frame = worldFrame;
        }

        public void SetCatalog(JArray entries)
        {
            if(entries == null || entries.Count == 0) return;
            catalog.Clear(); catalogIds.Clear();
            foreach(JObject entry in entries)
            {
                string id=(string)entry["id"];
                int index=(int?)entry["index"] ?? (int?)entry["mcIndex"] ?? -1;
                if(String.IsNullOrEmpty(id) || index<0 || index>100000 || (string)entry["source"]=="se") continue;
                catalog[index]=entry; catalogIds[id]=index;
            }
            int restored;
            SelectedItem=preferredItemId != null && catalogIds.TryGetValue(preferredItemId,out restored) ? restored : -1;
        }
        public void SetOwned(JArray items)
        {
            ownedItems.Clear();
            if(items==null)return;
            foreach(JObject item in items)
            {
                string id=(string)item["id"];
                if(!String.IsNullOrEmpty(id) && !id.StartsWith("se:",StringComparison.Ordinal) && ((decimal?)item["amount"] ?? (decimal?)item["count"] ?? 0)>0)
                    ownedItems.Add(id);
            }
        }
        // Native Minecraft is authoritative. Call on a fresh state snapshot or successful
        // native selection acknowledgement, never when a request is merely submitted.
        public void SyncNativeSelection(string itemId)
        {
            if(itemId!=null && itemId.StartsWith("se:",StringComparison.Ordinal))return;
            if(String.IsNullOrEmpty(itemId) || itemId=="minecraft:air")itemId=null;
            int index;
            int selected=itemId!=null && catalogIds.TryGetValue(itemId,out index) ? index : -1;
            bool changed=preferredItemId!=itemId || SelectedItem!=selected;
            preferredItemId=itemId;SelectedItem=selected;
            if(changed)SaveSelection();
        }
        public string ItemName(int index)
        {
            if(index<0)return "Pusta dłoń";
            JObject item;
            if(catalog.TryGetValue(index,out item)) return (string)item["name"] ?? (string)item["displayName"] ?? (string)item["id"];
            return index>=0 && index<ItemNames.Length ? ItemNames[index] : "Przedmiot " + index;
        }
        public JObject ItemDescriptor(int index)
        { JObject item; return catalog.TryGetValue(index,out item) ? item : null; }
        public void SelectById(string id)
        {
            int index;
            if(catalogIds.TryGetValue(id,out index)) { SelectItem(index); return; }
            if(RequestSelection!=null) RequestSelection(id);
        }

        public void SelectItem(int item)
        {
            if (item < 0 || (catalog.Count==0 ? item>=ItemNames.Length : !catalog.ContainsKey(item))) throw new ArgumentOutOfRangeException("item");
            JObject descriptor;
            if(RequestSelection != null)
            {
                if(catalog.TryGetValue(item,out descriptor))RequestSelection((string)descriptor["id"]);
                return;
            }
            SelectedItem = item;
            if(catalog.TryGetValue(item,out descriptor))preferredItemId=(string)descriptor["id"];
            SaveSelection();
            if (snapshot != null && snapshot.Active && MyAPIGateway.Session != null &&
                MyAPIGateway.Session.Player != null && MyAPIGateway.Session.Player.Character != null)
            {
                UpdateAim();
                Send(6, Vector3I.Zero, Vector3I.Up, new Vector3(.5f), "select");
            }
        }
        void SaveSelection()
        {
            try
            {
                string temporary = Preferences + ".tmp";
                JObject preferences = new JObject(); preferences["version"] = 3; preferences["selectedItem"] = SelectedItem; preferences["selectedItemId"] = preferredItemId;
                File.WriteAllText(temporary, preferences.ToString());
                if (File.Exists(Preferences)) File.Replace(temporary, Preferences, null); else File.Move(temporary, Preferences);
            }
            catch (Exception error) { log("PREFERENCES save: " + error.Message); }
        }

        public void PlayActionFeedback(BridgeCommand command, int result)
        {
            if (result != 1 || command == null || MyAPIGateway.Session == null || MyAPIGateway.Session.Player == null || BridgeIO.Now() < nextSound) return;
            MyGuiSounds sound;
            if (command.Description == "place") sound = MyGuiSounds.HudPlaceBlock;
            else if (command.Description == "break") sound = MyGuiSounds.HudDeleteBlock;
            else if (command.Description == "interact" || command.Description == "ignite") sound = MyGuiSounds.HudClick;
            else return;
            try
            {
                MyVisualScriptLogicProvider.PlayHudSoundLocal(sound, MyAPIGateway.Session.Player.IdentityId);
                nextSound = BridgeIO.Now() + 70;
            }
            catch (Exception error) { log("ACTION SOUND " + error.Message); }
        }

        public void HandleInput()
        {
            if (MyInput.Static == null || MyAPIGateway.Session == null || MyAPIGateway.Session.Player == null) return;
            bool gui = HasOpenMenu();
            bool onFoot = MyAPIGateway.Session.Player.Character != null && MyAPIGateway.Session.Player.Controller != null &&
                MyAPIGateway.Session.Player.Controller.ControlledEntity != null &&
                MyAPIGateway.Session.Player.Controller.ControlledEntity.Entity == MyAPIGateway.Session.Player.Character;
            bool canOpenInventory = OpenInventory != null && SnapshotFresh(BridgeIO.Now()) && onFoot && !gui && !MyInput.Static.IsAnyShiftKeyPressed();
            HoldInventoryControl(canOpenInventory);
            if(canOpenInventory && MyInput.Static.IsNewKeyPressed(MyKeys.I)) { OpenInventory(); return; }
            if (!gui && MyInput.Static.IsAnyCtrlKeyPressed() && MyInput.Static.IsAnyAltKeyPressed() && MyInput.Static.IsNewKeyPressed(MyKeys.B))
                SetEnabled(!enabled);
            if (!enabled) return;
            bool fresh = SnapshotFresh(BridgeIO.Now());
            if (!fresh)
            {
                HasSelection = HasPlacement = false;
                ReleaseControls();
                Status("Minecraft: oczekiwanie na swiat", 2000);
                return;
            }
            if (gui) { ReleaseControls(); HasSelection = HasPlacement = false; return; }
            // MC editing is only on foot. Cockpits retain their native ship controls.
            if (MyAPIGateway.Session.Player.Character == null ||
                MyAPIGateway.Session.Player.Controller == null ||
                MyAPIGateway.Session.Player.Controller.ControlledEntity == null ||
                MyAPIGateway.Session.Player.Controller.ControlledEntity.Entity != MyAPIGateway.Session.Player.Character)
            { ReleaseControls(); HasSelection = HasPlacement = false; return; }
            HoldControls();
            MyCharacter character = MyAPIGateway.Session.Player.Character as MyCharacter;
            if (character != null && MyAPIGateway.Session.Player.Character.EquippedTool != null)
                character.SwitchToWeapon((MyDefinitionId?)null, false);
            if (MyInput.Static.IsNewKeyPressed(MyKeys.G))
            {
                BlockPicker.Open(this, @"C:\Users\konra\Documents\SEMC-Lab", log);
                return;
            }
            int wheel = MyInput.Static.DeltaMouseScrollWheelValue();
            if (wheel != 0)
            {
                var indices=new List<int>();
                foreach(var entry in catalog)if(ownedItems.Contains((string)entry.Value["id"]))indices.Add(entry.Key);
                if(indices.Count>0)
                {
                    int at=indices.IndexOf(SelectedItem);
                    int next=at<0 ? (wheel>0?0:indices.Count-1) : (at+(wheel>0?1:indices.Count-1))%indices.Count;
                    SelectItem(indices[next]);
                }
                nextStatus = 0;
            }
            // Number keys provide familiar access to the first nine slots; wheel reaches the full palette.
            MyKeys[] numbers = { MyKeys.D1, MyKeys.D2, MyKeys.D3, MyKeys.D4, MyKeys.D5, MyKeys.D6, MyKeys.D7, MyKeys.D8, MyKeys.D9 };
            for (int i = 0; i < numbers.Length; i++) if (MyInput.Static.IsNewKeyPressed(numbers[i]))
            { SelectItem(i); nextStatus = 0; }
            UpdateAim();
            HoldInteractionControl(HasSelection);
            if (MyInput.Static.IsLeftMousePressed()) TryPrimary();
            else if (MyInput.Static.IsRightMousePressed()) TrySecondary();
            else if (MyInput.Static.IsNewKeyPressed(MyKeys.E)) TryInteract();
        }

        // The diagnostic endpoint and physical mouse/keyboard path share these exact mechanics.
        public bool TryPrimary()
        {
            if (!ReadyForAction()) return false;
            UpdateAim();
            if (!HasSelection)
            {
                if (terrainHit == null) return false;
                nextAction = BridgeIO.Now() + 250;
                if (!TerrainDig.TryDig(terrainHit, frame, log)) return false;
                PlayActionFeedback(new BridgeCommand { Description = "break" }, 1);
                return true;
            }
            Send(1, SelectionCell, SelectionNormal, HitFractions, "break");
            nextAction = BridgeIO.Now() + 120;
            return true;
        }
        public bool TrySecondary()
        {
            if (!ReadyForAction()) return false;
            UpdateAim();
            if(SelectedItem<0)return TryInteract();
            // Native items use their own interaction implementation, without per-item branches.
            if (!SelectedIsBlock)
            {
                if (!HasSelection) return false;
                Send(5, SelectionCell, SelectionNormal, HitFractions, "use-item");
                nextAction = BridgeIO.Now() + 150;
                return true;
            }
            if (!HasPlacement) return false;
            if (PlacementHitsPlayer(PlacementCell)) { Status("Nie można postawić bloku w postaci", 1000, true); return false; }
            Send(2, PlacementCell, SelectionNormal, HitFractions, "place");
            nextAction = BridgeIO.Now() + 120;
            return true;
        }
        public bool TryInteract()
        {
            if (!ReadyForAction()) return false;
            UpdateAim();
            if (!HasSelection) return false;
            Send(5, SelectionCell, SelectionNormal, HitFractions, "interact");
            nextAction = BridgeIO.Now() + 150;
            return true;
        }
        bool SnapshotFresh(long now)
        {
            return snapshot != null && snapshot.Active && snapshot.Timestamp > 0 &&
                snapshot.Timestamp <= now + 1000 && now - snapshot.Timestamp < 3000;
        }
        bool ReadyForAction()
        {
            long now = BridgeIO.Now();
            if (!enabled || !SnapshotFresh(now) || now < nextAction || (EconomyBusy!=null && EconomyBusy())) return false;
            if (MyAPIGateway.Session == null || MyAPIGateway.Session.Player == null || MyAPIGateway.Session.Player.Character == null) return false;
            if (HasOpenMenu()) return false;
            if (MyAPIGateway.Session.Player.Controller == null || MyAPIGateway.Session.Player.Controller.ControlledEntity == null ||
                MyAPIGateway.Session.Player.Controller.ControlledEntity.Entity != MyAPIGateway.Session.Player.Character) return false;
            if (MyInput.Static != null) HoldControls();
            return true;
        }

        static bool HasOpenMenu()
        {
            return BlockPicker.IsOpen || ContainerBridge.IsOpen || UnifiedInventoryScreen.IsOpen ||
                (MyAPIGateway.Gui != null && (MyAPIGateway.Gui.IsCursorVisible || MyAPIGateway.Gui.ChatEntryVisible));
        }

        public void UpdateAim()
        {
            HasSelection = HasPlacement = false;
            terrainHit = null;
            if (MyAPIGateway.Session == null || MyAPIGateway.Session.Player == null || MyAPIGateway.Session.Player.Character == null ||
                MyAPIGateway.Session.Camera == null || MyAPIGateway.Physics == null || snapshot == null) return;
            MatrixD camera = MyAPIGateway.Session.Camera.WorldMatrix;
            cameraPosition = camera.Translation;
            cameraDirection = camera.Forward;
            cameraDirection.Normalize();
            const double reach = 12.0;
            BlockHit block;
            bool mcHit = physics.TryRaycast(cameraPosition, cameraDirection, reach, out block);
            // Third-person camera rays may pass through our own character before reaching the target.
            aimHits.Clear();
            MyAPIGateway.Physics.CastRay(cameraPosition, cameraPosition + cameraDirection * reach, aimHits, 15);
            IHitInfo native = null;
            double nativeDistance = Double.MaxValue;
            foreach (IHitInfo hit in aimHits)
            {
                if (hit == null || hit.HitEntity == null || physics.Owns(hit.HitEntity) || hit.HitEntity == MyAPIGateway.Session.Player.Character) continue;
                double distance = Vector3D.Distance(cameraPosition, hit.Position);
                if (distance < nativeDistance) { native = hit; nativeDistance = distance; }
            }
            bool nativeObstacle = native != null;
            if (mcHit && block.Distance <= nativeDistance + .02)
            {
                HasSelection = true;
                SelectionCell = block.Cell; SelectionNormal = block.FaceNormal;
                HitFractions = block.LocalPoint; HitWorld = block.WorldPoint;
                PlacementCell = block.Cell + block.FaceNormal;
                HasPlacement = physics.InBounds(PlacementCell) && !physics.IsOccupied(PlacementCell);
                return;
            }
            if (!nativeObstacle || native.HitEntity == MyAPIGateway.Session.Player.Character) return;
            if (TerrainDig.CanDig(native)) terrainHit = native;
            Vector3D normal = physics.LocalDirection(native.Normal);
            SelectionNormal = DominantNormal(normal);
            Vector3D localHit = physics.LocalPoint(native.Position);
            PlacementCell = Vector3I.Floor(localHit + (Vector3D)SelectionNormal * .03);
            HitFractions = (Vector3)(localHit - (Vector3D)PlacementCell);
            HitWorld = native.Position;
            HasPlacement = physics.InBounds(PlacementCell) && !physics.IsOccupied(PlacementCell);
        }

        bool PlacementHitsPlayer(Vector3I cell)
        {
            if (MyAPIGateway.Session.Player.Character == null) return false;
            BoundingBoxD box = new BoundingBoxD((Vector3D)cell + new Vector3D(.02), (Vector3D)cell + new Vector3D(.98));
            MyOrientedBoundingBoxD placement = new MyOrientedBoundingBoxD(box, frame);
            var character = MyAPIGateway.Session.Player.Character;
            BoundingBox local = character.LocalAABB;
            MyOrientedBoundingBoxD body = new MyOrientedBoundingBoxD(new BoundingBoxD(local.Min, local.Max), character.WorldMatrix);
            return placement.Intersects(ref body);
        }

        void Send(int op, Vector3I cell, Vector3I normal, Vector3 hit, string description)
        {
            if (snapshot == null || issue == null) return;
            Vector3D localPlayer = physics.LocalPoint(MyAPIGateway.Session.Player.Character.GetPosition());
            Vector3D look = physics.LocalDirection(cameraDirection);
            float yaw = (float)(Math.Atan2(-look.X, look.Z) * 180.0 / Math.PI);
            float pitch = (float)(-Math.Asin(MathHelper.Clamp(look.Y, -1, 1)) * 180.0 / Math.PI);
            issue(new BridgeCommand {
                Operation = op, X = snapshot.X + cell.X, Y = snapshot.Y + cell.Y, Z = snapshot.Z + cell.Z,
                Item = SelectedItem, Face = Face(normal), Flags = description=="use-item" ? 5 : 1, HitX = MathHelper.Clamp(hit.X, 0, 1),
                HitY = MathHelper.Clamp(hit.Y, 0, 1), HitZ = MathHelper.Clamp(hit.Z, 0, 1),
                PlayerX = snapshot.X + localPlayer.X, PlayerY = snapshot.Y + localPlayer.Y, PlayerZ = snapshot.Z + localPlayer.Z,
                Yaw = yaw, Pitch = pitch, Description = description
            });
            log("INPUT " + description + " item=" + SelectedItem + " cell=" + cell);
        }

        public void SetEnabled(bool value)
        {
            if (enabled == value) return;
            enabled = value; nextStatus = 0;
            if (!enabled) { ReleaseControls(); HasSelection = HasPlacement = false; }
            log("BUILD MODE " + value);
            if (MyAPIGateway.Utilities != null)
                MyAPIGateway.Utilities.ShowNotification(value ? "Minecraft: tryb budowania" : "Space Engineers: sterowanie standardowe", 1800, "White");
        }
        void HoldControls()
        {
            if (controlsHeld) return;
            for (int i = 0; i < blockedControls.Length; i++)
            { previousBlocks[i] = MyInput.Static.IsControlBlocked(blockedControls[i]); MyInput.Static.SetControlBlock(blockedControls[i], true); }
            controlsHeld = true;
        }
        void ReleaseControls()
        {
            HoldInteractionControl(false);
            if (!controlsHeld || MyInput.Static == null) return;
            for (int i = 0; i < blockedControls.Length; i++) MyInput.Static.SetControlBlock(blockedControls[i], previousBlocks[i]);
            controlsHeld = false;
        }
        void HoldInteractionControl(bool active)
        {
            if (MyInput.Static == null) return;
            // E also rolls a native jetpack. Suppress only while an MC target can consume E.
            if (active && !rollHeld)
            { previousRoll = MyInput.Static.IsControlBlocked(MyControlsSpace.ROLL_RIGHT); MyInput.Static.SetControlBlock(MyControlsSpace.ROLL_RIGHT, true); rollHeld = true; }
            else if (!active && rollHeld)
            { MyInput.Static.SetControlBlock(MyControlsSpace.ROLL_RIGHT, previousRoll); rollHeld = false; }
        }
        void HoldInventoryControl(bool active)
        {
            if(MyInput.Static==null)return;
            if(active&&!inventoryHeld){previousInventory=MyInput.Static.IsControlBlocked(MyControlsSpace.INVENTORY);MyInput.Static.SetControlBlock(MyControlsSpace.INVENTORY,true);inventoryHeld=true;}
            else if(!active&&inventoryHeld){MyInput.Static.SetControlBlock(MyControlsSpace.INVENTORY,previousInventory);inventoryHeld=false;}
        }
        void Status(string text, int duration, bool force = false)
        {
            long now = BridgeIO.Now(); if (!force && now < nextStatus) return;
            nextStatus = now + duration;
            if (MyAPIGateway.Utilities != null) MyAPIGateway.Utilities.ShowNotification(text, duration, "White");
        }
        public static int Face(Vector3I normal)
        { return normal.Y < 0 ? 0 : normal.Y > 0 ? 1 : normal.Z < 0 ? 2 : normal.Z > 0 ? 3 : normal.X < 0 ? 4 : 5; }
        static Vector3I DominantNormal(Vector3D normal)
        {
            Vector3D abs = Vector3D.Abs(normal);
            if (abs.Y >= abs.X && abs.Y >= abs.Z) return new Vector3I(0, normal.Y < 0 ? -1 : 1, 0);
            if (abs.X >= abs.Z) return new Vector3I(normal.X < 0 ? -1 : 1, 0, 0);
            return new Vector3I(0, 0, normal.Z < 0 ? -1 : 1);
        }
        public void Dispose()
        {
            try { BlockPicker.Close(this); }
            finally { ReleaseControls(); HoldInventoryControl(false); enabled = false; }
        }
    }
}
