using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Newtonsoft.Json.Linq;
using Sandbox.Game.World;
using Sandbox.Graphics.GUI;
using Sandbox.ModAPI;
using VRage.Game;
using VRage.Game.ModAPI;
using VRageMath;

namespace SEMC.V2
{
    public sealed class GameBridge : IDisposable
    {
        const string Root = @"C:\Users\konra\Documents\SEMC-Lab";
        const string World = @"C:\Users\konra\AppData\Roaming\SpaceEngineers\Saves\76561198055314775\SEMC Lab - Bridge Test";
        BridgeIO bridge;
        PhysicsWorld physics;
        GameplayInput gameplay;
        MinecraftRenderer renderer;
        Hud hud;
        RuntimeControl control;
        ExplosionBridge explosions;
        TerrainBridge terrain;
        ContainerBridge containers;
        UnifiedInventory economy;
        RetainedWorldStore worldStore;
        RetainedWorldSnapshot restoreSnapshot;
        RemoteChunks remote;
        MovingBlockPhysics movingPhysics;
        int lastMovingBodies;
        Dictionary<int, BoundingBox[]> shapes = new Dictionary<int, BoundingBox[]>();
        HashSet<int> dynamicStates = new HashSet<int>();
        Dictionary<int, Vector3[]> stairStates = new Dictionary<int, Vector3[]>();
        object session;
        MatrixD absoluteFrame, frame;
        bool anchored, applied, requestedHide, disposed;
        int paletteRevision = -1, sx, sy, sz, originX, originY, originZ, rendered;
        ulong lastHash;
        long startedAt, nextRead, nextLog, nextError, nextHud, nextPose, nextCatalog;

        public void Init(object game)
        {
            startedAt = BridgeIO.Now();
            bridge = new BridgeIO(Root, Log);
            bridge.Acknowledged += delegate(BridgeCommand command, int result) {
                if (gameplay != null) gameplay.PlayActionFeedback(command, result);
                if (result == 1 && command.Operation == 1)
                {
                    Vector3I cell = new Vector3I(command.X, command.Y, command.Z);
                    if (physics != null) physics.ApplyConfirmedRemoval(cell);
                    if (renderer != null) renderer.ApplyConfirmedRemoval(cell);
                }
            };
            Log("INIT v2 full-metre native world; runtime=" + GetType().Assembly.ManifestModule.ModuleVersionId);
        }
        public void Update()
        {
            if (disposed) return;
            try { Tick(); }
            catch (Exception error)
            {
                if (BridgeIO.Now() > nextError) { Log("ERROR " + error); nextError = BridgeIO.Now() + 3000; }
            }
        }

        void Tick()
        {
            long now = BridgeIO.Now();
            Autoload(now);
            if (!Object.ReferenceEquals(session, MyAPIGateway.Session))
            {
                ClearWorld(); session = MyAPIGateway.Session;
                Log(session == null ? "SESSION unloaded" : "SESSION detected");
            }
            bool worldReady = MyAPIGateway.Session != null && MyAPIGateway.Session.Player != null && MyAPIGateway.Session.Player.Character != null && MyAPIGateway.Entities != null;
            if (!worldReady) { if (now >= nextRead) { nextRead = now + 100; bridge.Read(); bridge.Pump(false, 0); } return; }
            if (!MyAPIGateway.Session.OnlineMode.ToString().Equals("OFFLINE", StringComparison.OrdinalIgnoreCase)) return;
            string path = MySession.Static.CurrentPath;
            if (!String.Equals(Path.GetFullPath(path).TrimEnd('\\'), World, StringComparison.OrdinalIgnoreCase)) return;
            if(economy==null) economy=new UnifiedInventory(Root,Log);
            economy.Update(bridge.Connected);
            if(gameplay!=null && now>=nextCatalog)
            { nextCatalog=now+1000; gameplay.SetCatalog(economy.View["catalog"] as JArray); gameplay.SetOwned(economy.View["items"] as JArray); }
            if(gameplay!=null && !economy.Busy && (bool?)economy.View["connected"]==true)
                gameplay.SyncNativeSelection((string)economy.View["selectedItemId"]);
            if (now >= nextRead)
            {
                nextRead = now + 100;
                bool changed = bridge.Read();
                Snapshot data = bridge.Current;
                if (bridge.Connected && data != null)
                {
                    if (!anchored) { absoluteFrame = LoadOrCreateFrame(); anchored = true; }
                    string identity = RetainedWorldStore.ReadIdentity(Root, World, absoluteFrame);
                    if (identity == null) return;
                    if (worldStore != null && worldStore.Identity != identity)
                    {
                        Log("CACHE world identity changed; rebuilding consumers");
                        ClearWorld(); return;
                    }
                    if (worldStore == null)
                    {
                        worldStore = new RetainedWorldStore(Root, identity, Log);
                        restoreSnapshot = worldStore.Load();
                    }
                    frame = absoluteFrame;
                    frame.Translation = Vector3D.Transform(new Vector3D(data.X, data.Y, data.Z), absoluteFrame);
                    if (physics == null || sx != data.SizeX || sy != data.SizeY || sz != data.SizeZ)
                    {
                        if (restoreSnapshot == null && renderer != null) restoreSnapshot = renderer.CaptureRetained();
                        if (control != null) control.Dispose();
                        if (gameplay != null) gameplay.Dispose(); if (physics != null) physics.Dispose();
                        sx = data.SizeX; sy = data.SizeY; sz = data.SizeZ;
                        physics = new PhysicsWorld(sx, sy, sz, Log);
                        ConfigurePhysicsPalette();
                        gameplay = new GameplayInput(physics, Issue, Log);
                        gameplay.Enabled = true;
                        control = new RuntimeControl(physics, gameplay, Log);
                        ConfigureEconomy();
                        if (explosions == null) explosions = new ExplosionBridge(Root, Log);
                        if (terrain == null) terrain = new TerrainBridge(Root, Log);
                        if (containers == null) containers = new ContainerBridge(Root, Issue, Log);
                        ConfigureContainers();
                        if (hud != null) hud.Dispose(); hud = new Hud(Root, Log);
                        applied = false;
                    }
                    if (renderer == null) renderer = new MinecraftRenderer(Root, Log);
                    if (remote == null) remote = new RemoteChunks(Root, Log);
                    if (movingPhysics == null) movingPhysics = new MovingBlockPhysics(Root, Log);
                    bool paletteChanged = paletteRevision != data.PaletteRevision;
                    if (paletteChanged) LoadPalette(data.PaletteRevision);
                    if (restoreSnapshot != null)
                    {
                        physics.RestoreRetained(restoreSnapshot.Chunks, shapes, absoluteFrame);
                        renderer.RestoreRetained(restoreSnapshot.Chunks, absoluteFrame);
                        restoreSnapshot = null;
                    }
                    bool moved = !applied || originX != data.X || originY != data.Y || originZ != data.Z;
                    if (changed && (!applied || lastHash != data.ContentHash || moved || paletteChanged))
                    {
                        physics.Update(data.States, data.CellFlags, shapes, frame, new Vector3I(data.X, data.Y, data.Z));
                        renderer.Update(sx, sy, sz, data.States, data.Colors, data.CellFlags, frame, new Vector3I(data.X, data.Y, data.Z));
                        lastHash = data.ContentHash; originX = data.X; originY = data.Y; originZ = data.Z;
                        applied = true; rendered = renderer.RenderedCells;
                        Log("APPLIED cells=" + rendered + " colliders=" + physics.ColliderCount + " origin=" + data.X + "," + data.Y + "," + data.Z);
                    }
                    var updates = remote.Poll(data);
                    if (updates.Count > 0)
                    {
                        if (remote.RequiredPaletteRevision > paletteRevision) LoadPalette(remote.RequiredPaletteRevision);
                        physics.ApplyRemoteChunks(updates, shapes, absoluteFrame);
                        renderer.ApplyRemoteChunks(updates);
                        rendered = renderer.RenderedCells;
                        Log("REMOTE applied chunks=" + updates.Count + " retainedCells=" + rendered);
                    }
                    remote.UpdateInterests(renderer);
                    gameplay.Update(data, frame);
                    explosions.Update(absoluteFrame, physics);
                    if (!requestedHide)
                    {
                        bridge.Enqueue(new BridgeCommand { Operation = 7, Description = "background backend" });
                        requestedHide = true;
                    }
                    if (now >= nextLog)
                    {
                        Log("LIVE seq=" + data.Sequence + " age=" + (now - data.Timestamp) + " tick=" + data.Tick + " cells=" + rendered + " queue=" + bridge.PendingCount);
                        nextLog = now + 10000;
                    }
                }
                else if (physics == null) RestoreOfflineWorld();
            }
            if (physics != null && bridge.Connected)
            {
                physics.DrainDamageAbsolute(delegate(Vector3I cell, string reason) {
                    bridge.Enqueue(new BridgeCommand { Operation = 1, X = cell.X, Y = cell.Y, Z = cell.Z, Description = reason });
                });
            }
            bridge.Pump(gameplay != null && gameplay.Enabled, rendered);
            if (renderer != null && applied && bridge.Current != null)
                renderer.UpdateEntities(bridge.Current.X, bridge.Current.Y, bridge.Current.Z, frame);
            if (movingPhysics != null && anchored && physics != null)
            {
                movingPhysics.Update(absoluteFrame, physics);
                if (lastMovingBodies != movingPhysics.BodyCount)
                {
                    lastMovingBodies = movingPhysics.BodyCount;
                    Log("MOVING bodies=" + lastMovingBodies + " contacts=" + movingPhysics.ContactCount);
                }
            }
            if (bridge.Connected && anchored && bridge.Current != null)
            {
                if (terrain != null) terrain.Update(bridge.Current, absoluteFrame);
                if (now >= nextPose && bridge.PendingCount == 0)
                {
                    nextPose = now + 500;
                    MatrixD inverse = MatrixD.Invert(absoluteFrame);
                    Vector3D position = Vector3D.Transform(MyAPIGateway.Session.Player.Character.GetPosition(), inverse);
                    Vector3D look = Vector3D.TransformNormal(MyAPIGateway.Session.Camera.WorldMatrix.Forward, inverse);
                    bridge.Enqueue(new BridgeCommand { Operation = 9, Flags = 1,
                        PlayerX = position.X, PlayerY = position.Y, PlayerZ = position.Z,
                        Yaw = (float)(Math.Atan2(-look.X, look.Z) * 180 / Math.PI),
                        Pitch = (float)(-Math.Asin(MathHelper.Clamp(look.Y, -1, 1)) * 180 / Math.PI), Description = "pose" });
                }
            }
            if (control != null && bridge.Current != null) control.Poll(bridge.Current, absoluteFrame);
            if (bridge.LastResult != null && bridge.LastResult.Length > 0 && now >= nextHud)
            { MyAPIGateway.Utilities.ShowNotification(bridge.TakeNotice(), 1500, "Red"); nextHud = now + 2000; }
            if (hud != null) hud.Draw(gameplay, bridge.Current, bridge.Connected);
            if (containers != null) containers.Update(bridge.Connected);
            if (worldStore != null) worldStore.SaveIfDue(renderer);
        }

        void Issue(BridgeCommand command)
        {
            if (!bridge.Enqueue(command)) MyAPIGateway.Utilities.ShowNotification("Minecraft: oczekiwanie na połączenie", 1500, "White");
        }

        void RestoreOfflineWorld()
        {
            // Native grids may start falling before the background process reconnects.
            // Restore verified last-observed support even without a fresh capture.
            if (!File.Exists(Path.Combine(Root, "world-frame-v2.json")) ||
                !File.Exists(Path.Combine(Root, "palette-v2.json"))) return;
            absoluteFrame = LoadOrCreateFrame();
            string identity = RetainedWorldStore.ReadIdentity(Root, World, absoluteFrame);
            if (identity == null) return;
            var store = new RetainedWorldStore(Root, identity, Log);
            var saved = store.Load();
            if (saved == null) return;
            LoadPalette(-2);
            sx = sy = sz = 64;
            physics = new PhysicsWorld(sx, sy, sz, Log);
            ConfigurePhysicsPalette();
            physics.RestoreRetained(saved.Chunks, shapes, absoluteFrame);
            renderer = new MinecraftRenderer(Root, Log);
            renderer.RestoreRetained(saved.Chunks, absoluteFrame);
            remote = new RemoteChunks(Root, Log);
            movingPhysics = new MovingBlockPhysics(Root, Log);
            gameplay = new GameplayInput(physics, Issue, Log); gameplay.Enabled = true;
            control = new RuntimeControl(physics, gameplay, Log);
            ConfigureEconomy();
            explosions = new ExplosionBridge(Root, Log); terrain = new TerrainBridge(Root, Log);
            containers = new ContainerBridge(Root, Issue, Log); hud = new Hud(Root, Log);
            ConfigureContainers();
            anchored = true; worldStore = store; rendered = renderer.RenderedCells;
            Log("CACHE offline support restored cells=" + rendered);
        }
        public void HandleInput()
        {
            if (disposed || gameplay == null) return;
            try { gameplay.HandleInput(); }
            catch (Exception error) { if (BridgeIO.Now() >= nextError) { Log("INPUT ERROR " + error); nextError = BridgeIO.Now() + 3000; } }
        }

        void ConfigureEconomy()
        {
            if(economy==null) economy=new UnifiedInventory(Root,Log);
            gameplay.SetCatalog(economy.View["catalog"] as JArray);
            gameplay.RequestSelection=delegate(string id) { economy.Select(id); };
            economy.SelectionConfirmed=delegate(string id) {
                if(gameplay==null)return;
                bool nativeSE=id!=null && id.StartsWith("se:",StringComparison.Ordinal);
                if(!nativeSE) gameplay.SyncNativeSelection(id);
                gameplay.Enabled=!nativeSE;
            };
            gameplay.EconomyBusy=delegate { return economy==null || economy.Busy; };
            gameplay.OpenInventory=delegate { OpenEconomy(false); };
            gameplay.OpenCrafting=delegate { OpenEconomy(false); };
            control.Economy=economy;
            control.OpenEconomy=delegate { OpenEconomy(false); };
            BlockPicker.Configure(delegate { return economy.View; },SelectCatalogItem,CraftCatalogRecipe);
        }
        void SelectCatalogItem(string id)
        {
            if(id!=null && (id.StartsWith("se:",StringComparison.Ordinal) || id.StartsWith("stack:",StringComparison.Ordinal))) { if(economy!=null)economy.Select(id); }
            else if(gameplay!=null) gameplay.SelectById(id);
        }
        void ConfigureContainers()
        {
            containers.EconomyBusy=delegate { return economy==null || economy.Busy; };
            containers.OpenUnifiedCrafting=delegate {
                if(economy!=null) UnifiedInventoryScreen.OpenCrafting(Root,delegate{return economy.View;},SelectCatalogItem,CraftCatalogRecipe,Log);
            };
        }
        void CraftCatalogRecipe(string id,int amount)
        {
            if(economy==null)return;
            string status=economy.Craft(id,amount);
            if(!String.IsNullOrEmpty(status))MyAPIGateway.Utilities.ShowNotification(status,2500,"White");
        }
        void OpenEconomy(bool catalogOnly)
        {
            if(economy==null)return;
            UnifiedInventoryScreen.Open(Root,delegate{return economy.View;},SelectCatalogItem,CraftCatalogRecipe,Log,catalogOnly);
        }

        void LoadPalette(int revision)
        {
            string file = Path.Combine(Root, "palette-v2.json");
            if (!File.Exists(file)) return;
            JObject doc = JObject.Parse(BridgeIO.ReadSharedText(file));
            var next = new Dictionary<int, BoundingBox[]>();
            var nextDynamic = new HashSet<int>();
            var nextStairs = new Dictionary<int, Vector3[]>();
            foreach (JToken state in (JArray)doc["states"])
            {
                int id = (int)state["stateId"];
                string name = (string)state["registryName"];
                if ((bool?)state["dynamicCollision"] == true || name == "minecraft:moving_piston") nextDynamic.Add(id);
                JToken properties = state["properties"];
                Vector3[] vertices;
                if (properties != null && StairCollision.TryGetVertices(name, (string)properties["half"],
                    (string)properties["shape"], (string)properties["facing"], out vertices)) nextStairs[id] = vertices;
                var boxes = new List<BoundingBox>();
                foreach (JArray box in (JArray)state["collisionBoxes"])
                    boxes.Add(new BoundingBox(new Vector3((float)box[0], (float)box[1], (float)box[2]), new Vector3((float)box[3], (float)box[4], (float)box[5])));
                next[id] = boxes.ToArray();
            }
            shapes = next; dynamicStates = nextDynamic; stairStates = nextStairs; paletteRevision = revision;
            ConfigurePhysicsPalette();
            Log("PALETTE states=" + shapes.Count + " rev=" + revision);
        }
        void ConfigurePhysicsPalette()
        {
            if (physics == null) return;
            physics.SetDynamicCollisionStates(dynamicStates);
            physics.SetStairCollisionStates(stairStates);
        }

        MatrixD LoadOrCreateFrame()
        {
            string file = Path.Combine(Root, "world-frame-v2.json");
            if (File.Exists(file))
            {
                JObject value = JObject.Parse(File.ReadAllText(file));
                if ((string)value["world"] == World)
                {
                    var a = (JArray)value["matrix"];
                    MatrixD loaded = MatrixD.Identity;
                    loaded.Right = new Vector3D((double)a[0], (double)a[1], (double)a[2]);
                    loaded.Up = new Vector3D((double)a[3], (double)a[4], (double)a[5]);
                    loaded.Backward = new Vector3D((double)a[6], (double)a[7], (double)a[8]);
                    loaded.Translation = new Vector3D((double)a[9], (double)a[10], (double)a[11]);
                    Log("ANCHOR restored"); return loaded;
                }
            }
            MatrixD player = MyAPIGateway.Session.Player.Character.WorldMatrix;
            Vector3D up = player.Up;
            float influence;
            Vector3 gravity = MyAPIGateway.Physics.CalculateNaturalGravityAt(player.Translation, out influence);
            if (gravity.LengthSquared() > .01f) up = -Vector3D.Normalize((Vector3D)gravity);
            Vector3D forward = player.Forward - up * Vector3D.Dot(player.Forward, up);
            if (forward.LengthSquared() < .01) forward = Vector3D.Cross(player.Right, up);
            forward.Normalize();
            Vector3D ground = player.Translation;
            var hits = new List<IHitInfo>();
            MyAPIGateway.Physics.CastRay(player.Translation + up * 3, player.Translation - up * 8, hits, 15);
            double nearest = Double.MaxValue;
            foreach (IHitInfo hit in hits)
            {
                if (hit.HitEntity == null || hit.HitEntity == MyAPIGateway.Session.Player.Character) continue;
                double distance = Vector3D.DistanceSquared(hit.Position, player.Translation);
                if (distance < nearest) { nearest = distance; ground = hit.Position; }
            }
            MatrixD result = MatrixD.CreateWorld(ground, forward, up);
            result.Translation -= up * 240;
            JObject saved = new JObject(); saved["world"] = World;
            saved["matrix"] = new JArray(result.Right.X, result.Right.Y, result.Right.Z, result.Up.X, result.Up.Y, result.Up.Z,
                result.Backward.X, result.Backward.Y, result.Backward.Z, result.Translation.X, result.Translation.Y, result.Translation.Z);
            File.WriteAllText(file, saved.ToString());
            Log("ANCHOR new ground=" + ground); return result;
        }

        void Autoload(long now)
        {
            if (MyAPIGateway.Session != null || now < startedAt + 8000) return;
            string file = Path.Combine(Root, "load-v2-world.flag");
            if (!File.Exists(file)) return;
            MyGuiScreenBase focus = MyScreenManager.GetScreenWithFocus();
            if (focus == null || focus.GetType().FullName != "SpaceEngineers.Game.GUI.MyGuiScreenMainMenu" || focus.State.ToString() != "OPENED") return;
            if (!File.Exists(Path.Combine(World, "Sandbox.sbc"))) throw new FileNotFoundException("Missing SEMC world");
            File.Delete(file); Log("AUTOLOAD v2");
            MySessionLoader.LoadSingleplayerSession(World, delegate { Log("AUTOLOAD completed"); }, null, MyOnlineModeEnum.OFFLINE, 1, null, null);
        }
        void ClearWorld()
        {
            if (worldStore != null) worldStore.SaveIfDue(renderer, true);
            worldStore = null; restoreSnapshot = null;
            SafeDispose(control); SafeDispose(gameplay); gameplay = null;
            SafeDispose(physics); physics = null;
            SafeDispose(renderer); renderer = null;
            SafeDispose(hud); hud = null;
            SafeDispose(containers); containers = null;
            UnifiedInventoryScreen.Close();
            SafeDispose(economy); economy=null;
            SafeDispose(remote); remote = null;
            SafeDispose(movingPhysics); movingPhysics = null; lastMovingBodies = 0;
            applied = anchored = requestedHide = false; rendered = 0;
            paletteRevision = -1;
            control = null; explosions = null; terrain = null;
            if (bridge != null) bridge.ClearCommands();
        }
        static void SafeDispose(IDisposable value)
        { if (value != null) try { value.Dispose(); } catch(Exception error) { Log("CLEANUP " + value.GetType().Name + ": " + error.Message); } }
        public void Dispose()
        {
            if (disposed) return; disposed = true;
            ClearWorld(); if (bridge != null) bridge.Dispose();
            Log("DISPOSE complete");
        }
        static void Log(string message)
        { try { File.AppendAllText(Path.Combine(Root, "se-v2.log"), DateTime.UtcNow.ToString("O") + " " + message + Environment.NewLine); } catch { } }
    }
}
