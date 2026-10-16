using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Newtonsoft.Json.Linq;
using Sandbox;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Entities;
using Sandbox.Game.Weapons;
using Sandbox.Game.EntityComponents;
using Sandbox.Game.SessionComponents;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using VRage;
using VRage.Game;
using VRage.Game.ModAPI;
using VRage.ObjectBuilders;
using VRage.ModAPI;
using VRageMath;

namespace SEMC.V2
{
    // Local diagnostic endpoint for the explicitly cloned offline test session.
    // Poll on the game thread; the file is a command mailbox, never a script.
    public sealed class RuntimeControl : IDisposable
    {
        const string Root = @"C:\Users\konra\Documents\SEMC-Lab";
        const string TestWorld = @"C:\Users\konra\AppData\Roaming\SpaceEngineers\Saves\76561198055314775\SEMC Lab - Bridge Test";
        readonly string commandFile = Path.Combine(Root, "runtime-control-v2.json");
        readonly string stateFile = Path.Combine(Root, "runtime-state-v2.json");
        readonly PhysicsWorld physics;
        readonly GameplayInput input;
        readonly Action<string> log;
        public UnifiedInventory Economy;
        public Action OpenEconomy;
        JObject economyResult;
        string lastId, lastAction = "ready", lastResult = "ready", lastError;
        long nextPoll, nextState, commandAt, quitAt;
        long motionUntil, pilotUntil;
        MyShipController motionShip;
        MyCharacter motionCharacter;
        MyCockpit pendingCockpit;
        MyCharacter pendingPilot;
        Vector3 motionInput;
        static RuntimeControl characterMotionOwner;
        object characterMotionHarmony;
        MethodInfo unpatchCharacterMotion;
        readonly string characterMotionPatchId = "semc.diagnostic.character." + Guid.NewGuid().ToString("N");
        long characterMotionOverrides, shipMotionOverrides;
        sealed class TestGrid
        {
            public string Name, Kind;
            public IMyCubeGrid Grid;
            public long Expires;
        }
        readonly List<TestGrid> testGrids = new List<TestGrid>();
        bool reportNearby, spawningRover;
        double nearbyRadius = 200;
        string roverStatus;
        readonly List<IMyCubeGrid> showcaseRover = new List<IMyCubeGrid>();
        JObject pendingRoverReset;
        MatrixD pendingRoverFrame;
        long roverResetDeadline;
        readonly List<long> resettingRoverIds = new List<long>();

        public RuntimeControl(PhysicsWorld physicsWorld, GameplayInput gameplay, Action<string> logger)
        {
            physics = physicsWorld; input = gameplay; log = logger ?? delegate { };
            try
            {
                if (File.Exists(stateFile)) lastId = (string)JObject.Parse(File.ReadAllText(stateFile))["id"];
            }
            catch { }
        }

        public void Poll(Snapshot data, MatrixD absoluteFrame)
        {
            long now = BridgeIO.Now();
            if (!IsTestWorld()) { StopMotion(); pendingCockpit = null; pendingPilot = null; return; }
            // Native movement inputs must be refreshed each frame, independently of mailbox polling.
            try { TickMotion(now); }
            catch (Exception error) { StopMotion(); log("CONTROL MOVEMENT " + error.Message); }
            if (now < nextPoll) return;
            nextPoll = now + 100;
            try
            {
                TickRoverReset(now);
                for (int i = testGrids.Count - 1; i >= 0; i--)
                {
                    TestGrid item = testGrids[i];
                    if (item.Grid == null || item.Grid.Closed || now >= item.Expires)
                    { CloseGrid(item); testGrids.RemoveAt(i); }
                }
                if (File.Exists(commandFile) && new FileInfo(commandFile).Length <= 65536)
                {
                    JObject command = JObject.Parse(File.ReadAllText(commandFile));
                    string id = command["id"] == null ? null : command["id"].ToString();
                    if (!String.IsNullOrEmpty(id) && id.Length <= 128 && id != lastId)
                    {
                        lastId = id; commandAt = now; lastAction = ((string)command["action"] ?? "").ToLowerInvariant();
                        lastError = null; lastResult = "ok";
                        try { Execute(command, data, absoluteFrame); }
                        catch (Exception error) { lastResult = "error"; lastError = error.Message; }
                        log("CONTROL id=" + lastId + " action=" + lastAction + " result=" + lastResult + (lastError == null ? "" : " " + lastError));
                        nextState = 0;
                    }
                }
                if (now >= nextState)
                {
                    nextState = now + 250;
                    WriteState(data, absoluteFrame, now);
                }
                if (quitAt != 0 && now >= quitAt)
                {
                    quitAt = 0;
                    MySandboxGame.ExitThreadSafe();
                }
            }
            catch (IOException) { } // An atomic replacement or partially written command is retried next poll.
            catch (Newtonsoft.Json.JsonException) { }
            catch (Exception error)
            {
                if (now >= nextState) { nextState = now + 3000; log("CONTROL ERROR " + error); }
            }
        }

        void Execute(JObject command, Snapshot data, MatrixD frame)
        {
            MyCharacter character = MyAPIGateway.Session.Player.Character as MyCharacter;
            switch (lastAction)
            {
                case "inspect": return;
                case "economy":
                    if(Economy==null)throw new InvalidOperationException("Wspólny ekwipunek jeszcze się ładuje.");
                    economyResult=Economy.Execute(command);
                    return;
                case "openinventory":
                    RequireOnFoot(character);
                    if(OpenEconomy==null)throw new InvalidOperationException("Inventory UI unavailable.");
                    OpenEconomy();return;
                case "closeinventory":
                    UnifiedInventoryScreen.Close();return;
                case "openpicker":
                    RequireOnFoot(character);
                    if (!input.Enabled) throw new InvalidOperationException("Enable Minecraft build mode before opening its palette.");
                    BlockPicker.Open(input, Root, log); return;
                case "enterrover": EnterRover(command, character); return;
                case "exitrover":
                    StopMotion();
                    MyCockpit seated = ControlledEntity() as MyCockpit;
                    if (seated == null || seated.Pilot != character) throw new InvalidOperationException("The local character is not controlling a cockpit.");
                    if (!seated.RemovePilot()) throw new InvalidOperationException("Native cockpit could not find an exit position.");
                    return;
                case "drive": StartMotion(command, character, true); return;
                case "walk": StartMotion(command, character, false); return;
                case "stopmotion": StopMotion(); return;
                case "refillfood":
                case "refillhealth":
                case "refillvitals":
                    if (character == null || character.StatComp == null) throw new InvalidOperationException("Character stats unavailable.");
                    if (lastAction != "refillhealth" && character.StatComp.Food != null) character.StatComp.Food.Value = character.StatComp.Food.MaxValue;
                    if (lastAction != "refillfood" && character.StatComp.Health != null) character.StatComp.Health.Value = character.StatComp.Health.MaxValue;
                    return;
                case "nearbygrids":
                    nearbyRadius = Math.Max(1, Math.Min(2000, (double?)command["radius"] ?? 200)); reportNearby = true; return;
                case "showcaserover": SpawnShowcaseRover(command, frame); return;
                case "resetshowcaserover": ResetShowcaseRover(command, frame); return;
                case "showcaseramp": SpawnShowcaseRamp(frame); return;
                case "recoverrover": RecoverRover(command, frame); return;
                case "roverspeed": SetShowcaseRoverSpeed(command); return;
                case "refillrover": RefillRover(command); return;
                case "refillsuit":
                    if (character == null) throw new InvalidOperationException("Character unavailable.");
                    if (character.SuitBattery != null) character.SuitBattery.DebugSetBatteryLevel(.00001f);
                    if (character.OxygenComponent != null)
                    {
                        MyDefinitionId oxygen = MyResourceDistributorComponent.OxygenId, hydrogen = MyResourceDistributorComponent.HydrogenId;
                        character.OxygenComponent.UpdateStoredGasLevel(ref oxygen, 1f);
                        character.OxygenComponent.UpdateStoredGasLevel(ref hydrogen, 1f);
                    }
                    return;
                case "daylight":
                    Vector3 localSun = new Vector3((float?)command["x"] ?? .3f, (float?)command["y"] ?? .8f, (float?)command["z"] ?? .5f);
                    if (!Finite(localSun.X) || !Finite(localSun.Y) || !Finite(localSun.Z) || localSun.LengthSquared() < .01f) throw new ArgumentException("Invalid sun direction.");
                    Vector3 sun = Vector3.Normalize((Vector3)Vector3D.TransformNormal(localSun, frame));
                    MySession.Static.Settings.EnableSunRotation = false;
                    MySector.SunProperties.BaseSunDirectionNormalized = sun;
                    MySector.SunProperties.SunDirectionNormalized = sun;
                    MySectorWeatherComponent weatherComponent = MySession.Static.GetComponent<MySectorWeatherComponent>();
                    if (weatherComponent != null)
                    {
                        var weather = (VRage.Game.ObjectBuilders.MyObjectBuilder_SectorWeatherComponent)weatherComponent.GetObjectBuilder();
                        weather.BaseSunDirection = sun; weather.SunDirectionNormalized = sun;
                        weatherComponent.Init(weather);
                    }
                    return;
                case "spawngrid": SpawnGrid(command, frame); return;
                case "gridvelocity": SetGridVelocity(FindGrid(command), command, frame); return;
                case "movegrid":
                    IMyCubeGrid moving = FindGrid(command);
                    MatrixD movingPose = moving.WorldMatrix; movingPose.Translation = Vector3D.Transform(ReadPoint(command), frame);
                    moving.Teleport(movingPose);
                    if (moving.Physics != null) moving.Physics.ClearSpeed();
                    return;
                case "drill":
                    foreach (IMyShipDrill drill in FindGrid(command).GetFatBlocks<IMyShipDrill>()) drill.Enabled = (bool?)command["enabled"] ?? true;
                    return;
                case "cleargrids":
                    string removeName = (string)command["gridName"];
                    for (int i = testGrids.Count - 1; i >= 0; i--)
                        if (removeName == null || testGrids[i].Name == removeName) { CloseGrid(testGrids[i]); testGrids.RemoveAt(i); }
                    return;
                case "setmode":
                    input.SetEnabled((bool?)command["enabled"] ?? (bool?)command["value"] ?? false); return;
                case "selectitem":
                case "select":
                    input.SelectItem((int?)command["item"] ?? (int?)command["index"] ?? 0); return;
                case "break":
                    if (!input.TryPrimary()) throw new InvalidOperationException("No reachable Minecraft block or native terrain, build mode unavailable, or action is cooling down.");
                    return;
                case "place":
                    if (!input.TrySecondary()) throw new InvalidOperationException("No valid empty placement cell, build mode unavailable, or action is cooling down.");
                    return;
                case "interact":
                    if (!input.TryInteract()) throw new InvalidOperationException("No reachable Minecraft block to interact with, build mode unavailable, or action is cooling down.");
                    return;
                case "teleport":
                    StopMotion();
                    RequireOnFoot(character);
                    MatrixD pose = character.WorldMatrix;
                    pose.Translation = Vector3D.Transform(ReadPoint(command), frame);
                    character.Teleport(pose, null, false);
                    if (character.Physics != null) character.Physics.ClearSpeed();
                    if ((bool?)command["disableJetpack"] == true && character.JetpackComp != null)
                        character.JetpackComp.TurnOnJetpack(false, false, false);
                    else if ((bool?)command["enableJetpack"] == true && character.JetpackComp != null)
                    {
                        character.JetpackComp.TurnOnJetpack(true, false, false);
                        character.JetpackComp.EnableDampeners(true);
                    }
                    if ((bool?)command["firstPerson"] == true) FirstPerson(character);
                    return;
                case "aim":
                    RequireOnFoot(character);
                    if ((bool?)command["firstPerson"] != false) FirstPerson(character);
                    Aim(character, Vector3D.Transform(ReadPoint(command), frame), frame.Up);
                    return;
                case "nativesavesession":
                case "save":
                    if (MySession.Static.IsSaveInProgress) throw new InvalidOperationException("A native save is already in progress.");
                    if (!MySession.Static.Save(null, null)) throw new InvalidOperationException("Native session save returned false.");
                    return;
                case "quit":
                    StopMotion();
                    if (MySession.Static.IsSaveInProgress) throw new InvalidOperationException("Wait for the native save before quitting.");
                    lastResult = "quitting"; quitAt = BridgeIO.Now() + 400; return;
                default: throw new ArgumentException("Unknown diagnostic action: " + lastAction);
            }
        }

        static IMyEntity ControlledEntity()
        {
            var player = MyAPIGateway.Session == null ? null : MyAPIGateway.Session.Player;
            return player == null || player.Controller == null || player.Controller.ControlledEntity == null ? null : player.Controller.ControlledEntity.Entity;
        }
        void EnterRover(JObject command, MyCharacter character)
        {
            RequireOnFoot(character);
            if (command["entityId"] == null) throw new ArgumentException("enterrover requires an explicit cockpit entityId.");
            IMyEntity entity;
            if (!MyAPIGateway.Entities.TryGetEntityById((long)command["entityId"], out entity) || !(entity is MyCockpit))
                throw new ArgumentException("Native cockpit entityId was not found.");
            MyCockpit cockpit = (MyCockpit)entity;
            if (cockpit.Pilot != null && cockpit.Pilot != character) throw new InvalidOperationException("The cockpit already has a pilot.");
            if (Vector3D.DistanceSquared(cockpit.WorldMatrix.Translation, character.WorldMatrix.Translation) > 400)
                throw new InvalidOperationException("Stand within 20 metres of the requested cockpit.");
            StopMotion();
            ((IMyCockpit)cockpit).AttachPilot(character);
            pendingCockpit = cockpit; pendingPilot = character; pilotUntil = BridgeIO.Now() + 2000;
            roverStatus = "boarding";
        }
        void StartMotion(JObject command, MyCharacter character, bool drive)
        {
            int duration = (int?)command["durationMs"] ?? 1000;
            if (duration < 1 || duration > 3000) throw new ArgumentException("durationMs must be between 1 and 3000.");
            float forward = (float?)command["forward"] ?? 0;
            float side = (float?)command[drive ? "steer" : "strafe"] ?? 0;
            if (!Finite(forward) || !Finite(side) || Math.Abs(forward) > 1 || Math.Abs(side) > 1)
                throw new ArgumentException("Movement axes must be finite values from -1 to 1.");
            StopMotion();
            if (drive)
            {
                MyCockpit cockpit = ControlledEntity() as MyCockpit;
                if (cockpit == null || cockpit.Pilot != character || cockpit.CubeGrid.IsStatic)
                    throw new InvalidOperationException("Enter a mobile native cockpit before driving.");
                EnsureCharacterMotionOverride();
                motionShip = cockpit; characterMotionOwner = this; shipMotionOverrides = 0;
                ((IMyShipController)cockpit).HandBrake = false;
            }
            else
            {
                RequireOnFoot(character);
                EnsureCharacterMotionOverride();
                motionCharacter = character; characterMotionOwner = this; characterMotionOverrides = 0;
            }
            // Matches native W/S and A/D movement: forward is local negative Z, right is positive X.
            motionInput = new Vector3(side, 0, -forward);
            motionUntil = BridgeIO.Now() + duration;
            TickMotion(BridgeIO.Now());
        }
        void TickMotion(long now)
        {
            if (pendingCockpit != null)
            {
                if (pendingCockpit.Closed || pendingPilot == null || pendingPilot.Closed || now >= pilotUntil)
                { roverStatus = "boarding failed or timed out"; pendingCockpit = null; pendingPilot = null; }
                else if (pendingCockpit.Pilot == pendingPilot)
                {
                    pendingCockpit.GiveControlToPilot();
                    roverStatus = ControlledEntity() == pendingCockpit ? "pilot controlling rover" : "pilot attached";
                    pendingCockpit = null; pendingPilot = null;
                }
            }
            if (motionUntil == 0) return;
            IMyEntity target = motionShip != null ? (IMyEntity)motionShip : motionCharacter;
            if (now >= motionUntil || target == null || target.Closed || ControlledEntity() != target ||
                (MyAPIGateway.Gui != null && (MyAPIGateway.Gui.IsCursorVisible || MyAPIGateway.Gui.ChatEntryVisible)))
            { StopMotion(); return; }
            if (motionShip != null) motionShip.MoveAndRotate(motionInput, Vector2.Zero, 0);
            else motionCharacter.MoveAndRotate(motionInput, Vector2.Zero, 0);
        }
        void StopMotion()
        {
            if (ReferenceEquals(characterMotionOwner, this)) characterMotionOwner = null;
            MyShipController ship = motionShip; MyCharacter character = motionCharacter;
            motionShip = null; motionCharacter = null; motionUntil = 0; motionInput = Vector3.Zero;
            if (ship != null && !ship.Closed)
            {
                ship.MoveAndRotate(Vector3.Zero, Vector2.Zero, 0);
                ship.MoveAndRotateStopped();
                ((IMyShipController)ship).HandBrake = true;
            }
            if (character != null && !character.Closed)
            {
                character.MoveAndRotate(Vector3.Zero, Vector2.Zero, 0);
                character.MoveAndRotateStopped();
            }
        }

        void EnsureCharacterMotionOverride()
        {
            if (characterMotionHarmony != null) return;
            // Native input later in the frame can overwrite queued MoveAndRotate indicators.
            // Override only the actively controlled instance during an explicit bounded walk/drive test.
            Assembly harmonyAssembly = null;
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                if (assembly.GetType("HarmonyLib.Harmony", false) != null) { harmonyAssembly = assembly; break; }
            if (harmonyAssembly == null) harmonyAssembly = Assembly.LoadFrom(Path.Combine(Root, "tools", "Pulsar", "Libraries", "Legacy", "0Harmony.dll"));
            Type harmonyType = harmonyAssembly.GetType("HarmonyLib.Harmony", true);
            Type harmonyMethodType = harmonyAssembly.GetType("HarmonyLib.HarmonyMethod", true);
            object harmony = Activator.CreateInstance(harmonyType, new object[] { characterMotionPatchId });
            MethodInfo prefix = typeof(RuntimeControl).GetMethod("OverrideCharacterMovement", BindingFlags.Static | BindingFlags.NonPublic);
            object descriptor = Activator.CreateInstance(harmonyMethodType, new object[] { prefix });
            MethodInfo original = typeof(MyCharacter).GetMethod("MoveAndRotate", new[] { typeof(Vector3), typeof(Vector2), typeof(float) });
            MethodInfo shipOriginal = typeof(MyShipController).GetMethod("MoveAndRotate", new[] { typeof(Vector3), typeof(Vector2), typeof(float) });
            MethodInfo shipConsume = typeof(MyShipController).GetMethod("MoveAndRotate", Type.EmptyTypes);
            MethodInfo patch = harmonyType.GetMethod("Patch", new[] { typeof(MethodBase), harmonyMethodType, harmonyMethodType, harmonyMethodType, harmonyMethodType });
            if (original == null || shipOriginal == null || shipConsume == null || patch == null) throw new MissingMethodException("Native diagnostic movement patch API is unavailable.");
            unpatchCharacterMotion = harmonyType.GetMethod("UnpatchAll", new[] { typeof(string) });
            try
            {
                patch.Invoke(harmony, new[] { (object)original, descriptor, null, null, null });
                MethodInfo shipPrefix = typeof(RuntimeControl).GetMethod("OverrideShipMovement", BindingFlags.Static | BindingFlags.NonPublic);
                object shipDescriptor = Activator.CreateInstance(harmonyMethodType, new object[] { shipPrefix });
                patch.Invoke(harmony, new[] { (object)shipOriginal, shipDescriptor, null, null, null });
                MethodInfo consumePrefix = typeof(RuntimeControl).GetMethod("BeforeShipMovementConsumed", BindingFlags.Static | BindingFlags.NonPublic);
                object consumeDescriptor = Activator.CreateInstance(harmonyMethodType, new object[] { consumePrefix });
                patch.Invoke(harmony, new[] { (object)shipConsume, consumeDescriptor, null, null, null });
            }
            catch
            {
                if (unpatchCharacterMotion != null) unpatchCharacterMotion.Invoke(harmony, new object[] { characterMotionPatchId });
                throw;
            }
            characterMotionHarmony = harmony;
            log("CONTROL native character/ship movement overrides installed (inactive except bounded walk/drive commands)");
        }
        static void OverrideCharacterMovement(MyCharacter __instance, ref Vector3 __0, ref Vector2 __1, ref float __2)
        {
            RuntimeControl owner = characterMotionOwner;
            if (owner == null || owner.motionCharacter != __instance || owner.motionUntil <= BridgeIO.Now() ||
                !IsTestWorld() || ControlledEntity() != __instance ||
                (MyAPIGateway.Gui != null && (MyAPIGateway.Gui.IsCursorVisible || MyAPIGateway.Gui.ChatEntryVisible))) return;
            __0 = owner.motionInput; __1 = Vector2.Zero; __2 = 0;
            owner.characterMotionOverrides++;
        }

        static void OverrideShipMovement(MyShipController __instance, ref Vector3 __0, ref Vector2 __1, ref float __2)
        {
            RuntimeControl owner = characterMotionOwner;
            if (!ShipMotionActive(owner, __instance)) return;
            __0 = owner.motionInput; __1 = Vector2.Zero; __2 = 0;
            owner.shipMotionOverrides++;
        }
        static bool ShipMotionActive(RuntimeControl owner, MyShipController ship)
        {
            return owner != null && owner.motionShip == ship && owner.motionUntil > BridgeIO.Now() &&
                IsTestWorld() && ControlledEntity() == ship &&
                (MyAPIGateway.Gui == null || (!MyAPIGateway.Gui.IsCursorVisible && !MyAPIGateway.Gui.ChatEntryVisible));
        }
        static void BeforeShipMovementConsumed(MyShipController __instance)
        {
            RuntimeControl owner = characterMotionOwner;
            if (!ShipMotionActive(owner, __instance)) return;
            // Native no-argument MoveAndRotate consumes indicators into wheel/thruster systems.
            // Refresh through its normal input API just before consumption, after any input clearing.
            __instance.MoveAndRotate(owner.motionInput, Vector2.Zero, 0);
        }

        void SpawnShowcaseRover(JObject command, MatrixD frame)
        {
            if (spawningRover || pendingRoverReset != null) throw new InvalidOperationException("The rover prefab is still spawning or resetting.");
            foreach (IMyCubeGrid grid in showcaseRover) if (grid != null && !grid.Closed) throw new InvalidOperationException("A showcase rover already exists. Use recoverrover with its entityId.");
            string recordFile = Path.Combine(Root, "showcase-rover-v2.json");
            if (File.Exists(recordFile))
            {
                JObject saved = JObject.Parse(File.ReadAllText(recordFile));
                JArray ids = saved["entityIds"] as JArray;
                if (ids != null) foreach (JToken id in ids)
                {
                    IMyEntity existing;
                    if (MyAPIGateway.Entities.TryGetEntityById((long)id, out existing) && existing is IMyCubeGrid && !existing.Closed)
                        throw new InvalidOperationException("A saved showcase rover already exists. Inspect nearbygrids and use recoverrover.");
                }
            }
            Vector3D ground = Vector3D.Transform(ReadPoint(command), frame);
            double clearance = (double?)command["clearance"] ?? 2.5;
            if (!Finite(clearance) || clearance < 0 || clearance > 5) throw new ArgumentException("clearance must be between 0 and 5 metres.");
            Vector3D position = ground + frame.Up * clearance;
            if (Vector3D.DistanceSquared(position, MyAPIGateway.Session.Player.Character.GetPosition()) < 36)
                throw new InvalidOperationException("Stand at least 6 metres from the rover spawn point.");
            showcaseRover.Clear(); spawningRover = true; roverStatus = "spawning";
            long owner = MyAPIGateway.Session.Player.IdentityId;
            MyAPIGateway.PrefabManager.SpawnPrefab(showcaseRover, "RespawnRover", position, (Vector3)frame.Forward, (Vector3)frame.Up,
                Vector3.Zero, Vector3.Zero, "Rover", SpawningOptions.RotateFirstCockpitTowardsDirection | SpawningOptions.SetAuthorship,
                owner, true, delegate {
                    MyAPIGateway.Utilities.InvokeOnGameThread(delegate {
                        try
                        {
                            if (!IsTestWorld())
                            {
                                foreach (IMyCubeGrid grid in showcaseRover) if (grid != null && !grid.Closed) grid.Close();
                                roverStatus = "cancelled: session changed"; return;
                            }
                            JArray ids = new JArray();
                            foreach (IMyCubeGrid grid in showcaseRover)
                            {
                                if (grid == null || grid.Closed) continue;
                                grid.Save = true;
                                ParkGrid(grid);
                                ids.Add(grid.EntityId);
                            }
                            JObject saved = new JObject(); saved["world"] = TestWorld; saved["entityIds"] = ids;
                            File.WriteAllText(recordFile, saved.ToString());
                            if ((bool?)command["prepare"] == true)
                            {
                                foreach (IMyCubeGrid grid in showcaseRover)
                                {
                                    bool hasCockpit = false;
                                    foreach (IMyCockpit cockpit in grid.GetFatBlocks<IMyCockpit>()) { hasCockpit = true; break; }
                                    if (!hasCockpit) continue;
                                    JObject prepare = new JObject(); prepare["entityId"] = grid.EntityId;
                                    prepare["speedKmh"] = (float?)command["speedKmh"] ?? 8f;
                                    RefillRover(prepare); SetShowcaseRoverSpeed(prepare); break;
                                }
                            }
                            roverStatus = "ready"; reportNearby = true;
                            log("SHOWCASE ROVER ready grids=" + showcaseRover.Count + " owner=" + owner);
                        }
                        catch (Exception error) { roverStatus = "error: " + error.Message; log("SHOWCASE ROVER " + error); }
                        finally { spawningRover = false; }
                    });
                });
        }
        void ResetShowcaseRover(JObject command, MatrixD frame)
        {
            if (spawningRover || pendingRoverReset != null) throw new InvalidOperationException("A rover spawn or reset is already in progress.");
            if (command["entityId"] == null) throw new ArgumentException("resetshowcaserover requires the recorded main-grid entityId.");
            IMyEntity entity;
            if (!MyAPIGateway.Entities.TryGetEntityById((long)command["entityId"], out entity) || !(entity is IMyCubeGrid))
                throw new ArgumentException("The recorded showcase rover was not found.");
            IMyCubeGrid main = (IMyCubeGrid)entity; RequireShowcaseRover(main);
            Vector3D position = Vector3D.Transform(ReadPoint(command), frame);
            if (Vector3D.DistanceSquared(position, MyAPIGateway.Session.Player.Character.GetPosition()) < 36)
                throw new InvalidOperationException("Stand at least 6 metres from the replacement rover spawn point.");
            float speed = (float?)command["speedKmh"] ?? 8f;
            if (!Finite(speed) || speed < 1 || speed > 30) throw new ArgumentException("speedKmh must be between 1 and 30.");
            string recordFile = Path.Combine(Root, "showcase-rover-v2.json");
            JObject record = JObject.Parse(File.ReadAllText(recordFile));
            var ids = new HashSet<long>();
            foreach (JToken id in (JArray)record["entityIds"]) ids.Add((long)id);
            if (ids.Count < 1 || ids.Count > 8) throw new InvalidOperationException("Unexpected showcase rover record size.");
            JArray cleanup = command["cleanupEntityIds"] as JArray;
            if (command["cleanupEntityIds"] != null && cleanup == null) throw new ArgumentException("cleanupEntityIds must be an array.");
            if (cleanup != null)
            {
                if (cleanup.Count > 2) throw new ArgumentException("Only the two explicitly identified fixture debris grids may be cleaned up.");
                foreach (JToken item in cleanup)
                {
                    long id = (long)item;
                    // These exact split grids were positively identified in the live fixture test.
                    // Never infer ownership from proximity, display name, or the player's ownership alone.
                    if (id != 81788548520545205L && id != 119570795854881622L)
                        throw new ArgumentException("Cleanup entityId was not explicitly identified as showcase rover debris.");
                    ids.Add(id);
                }
            }
            var group = new List<IMyCubeGrid>(); MyAPIGateway.GridGroups.GetGroup(main, GridLinkTypeEnum.Physical, group);
            foreach (IMyCubeGrid grid in group) if (!ids.Contains(grid.EntityId))
                throw new InvalidOperationException("The rover has an attached grid that is not part of the original showcase fixture.");
            var old = new List<IMyCubeGrid>();
            foreach (long id in ids)
            {
                IMyEntity item;
                if (!MyAPIGateway.Entities.TryGetEntityById(id, out item) || item.Closed) continue;
                IMyCubeGrid grid = item as IMyCubeGrid;
                if (grid == null || grid.IsStatic || grid.GridSizeEnum != MyCubeSize.Small)
                    throw new InvalidOperationException("The recorded fixture contains an unexpected entity.");
                foreach (IMyCockpit cockpit in grid.GetFatBlocks<IMyCockpit>()) if (cockpit.Pilot != null)
                    throw new InvalidOperationException("Leave the showcase rover before resetting it.");
                old.Add(grid);
            }
            string backup = Path.Combine(Root, "backups", "rover-reset-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"));
            Directory.CreateDirectory(backup);
            File.Copy(recordFile, Path.Combine(backup, "showcase-rover-v2.json"), false);
            // Every native builder is saved successfully before the first old grid is closed.
            foreach (IMyCubeGrid grid in old)
            {
                MyObjectBuilder_CubeGrid builder = grid.GetObjectBuilder() as MyObjectBuilder_CubeGrid;
                if (builder == null)
                    throw new IOException("Cannot back up the old rover; reset was not started.");
                // Native path-based serializer is restricted to mod storage. Serialize its same native
                // object builder in memory, then write to the explicitly owned local plugin workspace.
                string xml = MyAPIGateway.Utilities.SerializeToXML(builder);
                if (String.IsNullOrWhiteSpace(xml)) throw new IOException("Native rover serialization returned no backup; reset was not started.");
                File.WriteAllText(Path.Combine(backup, "grid-" + grid.EntityId + ".sbc"), xml, System.Text.Encoding.UTF8);
            }
            StopMotion(); showcaseRover.Clear(); resettingRoverIds.Clear();
            foreach (IMyCubeGrid grid in old) { resettingRoverIds.Add(grid.EntityId); ParkGrid(grid); grid.Close(); }
            pendingRoverReset = (JObject)command.DeepClone();
            pendingRoverReset["clearance"] = 0; pendingRoverReset["prepare"] = true; pendingRoverReset["speedKmh"] = speed;
            pendingRoverFrame = frame; roverResetDeadline = BridgeIO.Now() + 10000;
            roverStatus = "reset: closing old fixture";
            log("ROVER RESET backed up " + old.Count + " native grids to " + backup + "; waiting for closure");
        }
        void TickRoverReset(long now)
        {
            if (pendingRoverReset == null) return;
            foreach (long id in resettingRoverIds)
            {
                IMyEntity entity;
                if (MyAPIGateway.Entities.TryGetEntityById(id, out entity) && !entity.Closed)
                {
                    if (now < roverResetDeadline) return;
                    pendingRoverReset = null; roverStatus = "reset timed out waiting for native grid closure";
                    lastResult = "error"; lastError = roverStatus; log("ROVER RESET " + roverStatus); return;
                }
            }
            JObject command = pendingRoverReset; pendingRoverReset = null; resettingRoverIds.Clear();
            try { SpawnShowcaseRover(command, pendingRoverFrame); }
            catch (Exception error) { roverStatus = "reset spawn failed: " + error.Message; lastResult = "error"; lastError = roverStatus; log("ROVER RESET " + error); }
        }
        void SpawnShowcaseRamp(MatrixD frame)
        {
            const string displayName = "Podjazd garażu";
            string recordFile = Path.Combine(Root, "showcase-ramp-v2.json");
            if (File.Exists(recordFile))
            {
                JObject saved = JObject.Parse(File.ReadAllText(recordFile));
                if ((string)saved["world"] != TestWorld) throw new InvalidOperationException("The stored ramp belongs to a different session.");
                IMyEntity existing;
                if (MyAPIGateway.Entities.TryGetEntityById((long)saved["entityId"], out existing) && existing is IMyCubeGrid && !existing.Closed)
                { reportNearby = true; log("SHOWCASE RAMP already exists entity=" + existing.EntityId); return; }
            }
            Vector3D localMin = new Vector3D(-10, 237.5, -5), localMax = new Vector3D(-5, 240, 0);
            MatrixD inverse = MatrixD.Invert(frame);
            Vector3D character = Vector3D.Transform(MyAPIGateway.Session.Player.Character.GetPosition(), inverse);
            BoundingBoxD exclusion = new BoundingBoxD(localMin - new Vector3D(2), localMax + new Vector3D(2));
            if (exclusion.Contains(character) != ContainmentType.Disjoint)
                throw new InvalidOperationException("Stand outside the garage approach before creating its ramp.");
            var entities = new HashSet<IMyEntity>();
            MyAPIGateway.Entities.GetEntities(entities, delegate(IMyEntity item) { return item is IMyCubeGrid && !item.Closed; });
            BoundingBoxD worldBounds = new MyOrientedBoundingBoxD(new BoundingBoxD(localMin, localMax), frame).GetAABB();
            foreach (IMyEntity entity in entities)
            {
                IMyCubeGrid grid = (IMyCubeGrid)entity;
                if (grid.DisplayName == displayName && grid.IsStatic)
                { WriteRampRecord(recordFile, grid, frame); reportNearby = true; log("SHOWCASE RAMP recovered record entity=" + grid.EntityId); return; }
                if (worldBounds.Intersects(grid.WorldAABB))
                    throw new InvalidOperationException("Move native grids away from the garage approach before creating its ramp.");
            }
            foreach (string subtype in new[] { "LargeBlockArmorSlope2Base", "LargeBlockArmorSlope2Tip" })
            {
                Sandbox.Definitions.MyCubeBlockDefinition definition;
                if (!Sandbox.Definitions.MyDefinitionManager.Static.TryGetCubeBlockDefinition(new MyDefinitionId(typeof(MyObjectBuilder_CubeBlock), subtype), out definition) ||
                    definition.CubeSize != MyCubeSize.Large || definition.Size != Vector3I.One)
                    throw new InvalidOperationException("The installed native armor ramp definition is unavailable: " + subtype);
            }
            MyObjectBuilder_CubeGrid builder = CreateShowcaseRampBuilder(frame, MyAPIGateway.Session.Player.IdentityId);
            IMyCubeGrid ramp = MyAPIGateway.Entities.CreateFromObjectBuilderAndAdd(builder) as IMyCubeGrid;
            if (ramp == null) throw new InvalidOperationException("Native garage ramp creation returned no grid.");
            ramp.Save = true;
            WriteRampRecord(recordFile, ramp, frame);
            reportNearby = true;
            log("SHOWCASE RAMP ready entity=" + ramp.EntityId + " blocks=4 boundsMC=(-10,237.5,-5)..(-5,240,0) uphill=-Z; native steel armor, persistent static grid");
        }
        static MyObjectBuilder_CubeGrid CreateShowcaseRampBuilder(MatrixD frame, long owner)
        {
            MatrixD matrix = frame;
            // Large-grid cell zero is its block center, not its minimum corner.
            matrix.Translation = Vector3D.Transform(new Vector3D(-8.75, 238.75, -3.75), frame);
            var builder = new MyObjectBuilder_CubeGrid {
                GridSizeEnum = MyCubeSize.Large, IsStatic = true, CreatePhysics = true,
                DestructibleBlocks = true, Editable = true, DisplayName = "Podjazd garażu",
                PersistentFlags = MyPersistentEntityFlags2.InScene,
                PositionAndOrientation = new MyPositionAndOrientation(matrix),
                CubeBlocks = new List<MyObjectBuilder_CubeBlock>()
            };
            for (int x = 0; x < 2; x++) for (int z = 0; z < 2; z++)
                builder.CubeBlocks.Add(new MyObjectBuilder_CubeBlock {
                    SubtypeName = z == 0 ? "LargeBlockArmorSlope2Base" : "LargeBlockArmorSlope2Tip",
                    Min = new Vector3I(x, 0, z),
                    BlockOrientation = new SerializableBlockOrientation(Base6Directions.Direction.Forward, Base6Directions.Direction.Up),
                    BuildPercent = 1f, IntegrityPercent = 1f, Owner = owner,
                    ColorMaskHSV = new Color(105, 115, 125).ColorToHSVDX11()
                });
            return builder;
        }
        static void WriteRampRecord(string path, IMyCubeGrid grid, MatrixD frame)
        {
            JObject record = new JObject(); record["world"] = TestWorld; record["entityId"] = grid.EntityId;
            record["name"] = grid.DisplayName; record["created"] = BridgeIO.Now();
            record["minimumMC"] = new JArray(-10, 237.5, -5); record["maximumMC"] = new JArray(-5, 240, 0);
            File.WriteAllText(path, record.ToString());
        }

        void RecoverRover(JObject command, MatrixD frame)
        {
            if ((bool?)command["align"] == true)
                throw new InvalidOperationException("Physical-group alignment is disabled after an unstable live test. Use resetshowcaserover for a clean native prefab.");
            if (command["entityId"] == null) throw new ArgumentException("recoverrover requires an explicit native entityId from nearbygrids.");
            IMyEntity entity;
            if (!MyAPIGateway.Entities.TryGetEntityById((long)command["entityId"], out entity) || !(entity is IMyCubeGrid))
                throw new ArgumentException("Native rover entityId was not found.");
            IMyCubeGrid main = (IMyCubeGrid)entity;
            if (main.IsStatic || main.GridSizeEnum != MyCubeSize.Small) throw new InvalidOperationException("Recovery only accepts a mobile small-grid rover.");
            bool cockpit = false;
            foreach (IMyShipController controller in main.GetFatBlocks<IMyShipController>()) { cockpit = true; break; }
            if (!cockpit) throw new InvalidOperationException("Select the rover's main cockpit grid rather than a detached wheel.");
            Vector3D destination = Vector3D.Transform(ReadPoint(command), frame) + frame.Up * 2.5;
            if (Vector3D.DistanceSquared(destination, MyAPIGateway.Session.Player.Character.GetPosition()) < 36)
                throw new InvalidOperationException("Stand at least 6 metres from the recovery point.");
            // MyCubeGrid.Teleport moves its entire physical group and preserves orientation.
            // Calling it for every wheel would repeatedly translate the already moved rover.
            MatrixD newMain = main.WorldMatrix;
            newMain.Translation = destination;
            var group = new List<IMyCubeGrid>();
            MyAPIGateway.GridGroups.GetGroup(main, GridLinkTypeEnum.Physical, group);
            StopMotion();
            foreach (IMyCubeGrid grid in group) ParkGrid(grid);
            main.Teleport(newMain);
            foreach (IMyCubeGrid grid in group)
            {
                grid.Save = true; ParkGrid(grid);
            }
            roverStatus = "recovered"; reportNearby = true;
            log("ROVER recovered once entity=" + main.EntityId + " physicalGroup=" + group.Count + " targetMC=" + Vector3D.Transform(destination, MatrixD.Invert(frame)));
        }
        static void RequireShowcaseRover(IMyCubeGrid main)
        {
            string recordFile = Path.Combine(Root, "showcase-rover-v2.json");
            if (!File.Exists(recordFile)) throw new InvalidOperationException("This operation is limited to the recorded showcase rover.");
            JObject saved = JObject.Parse(File.ReadAllText(recordFile));
            if (!String.Equals((string)saved["world"], TestWorld, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The recorded showcase rover belongs to another world.");
            JArray ids = saved["entityIds"] as JArray;
            if (ids != null) foreach (JToken id in ids) if ((long)id == main.EntityId) return;
            throw new InvalidOperationException("The requested grid is not part of the recorded showcase rover.");
        }
        void SetShowcaseRoverSpeed(JObject command)
        {
            if (command["entityId"] == null) throw new ArgumentException("roverspeed requires an explicit main-grid entityId.");
            IMyEntity entity;
            if (!MyAPIGateway.Entities.TryGetEntityById((long)command["entityId"], out entity) || !(entity is IMyCubeGrid))
                throw new ArgumentException("The showcase rover grid was not found.");
            IMyCubeGrid main = (IMyCubeGrid)entity; RequireShowcaseRover(main);
            float speed = (float?)command["speedKmh"] ?? 15f;
            if (!Finite(speed) || speed < 1 || speed > 30) throw new ArgumentException("Showcase driving speed must be between 1 and 30 km/h.");
            var group = new List<IMyCubeGrid>(); MyAPIGateway.GridGroups.GetGroup(main, GridLinkTypeEnum.Physical, group);
            int count = 0;
            foreach (IMyCubeGrid grid in group)
                foreach (IMyMotorSuspension wheel in grid.GetFatBlocks<IMyMotorSuspension>())
                {
                    var native = wheel as Sandbox.Game.Entities.Cube.MyMotorSuspension;
                    if (native == null) continue;
                    native.SpeedLimit = speed; count++;
                }
            if (count == 0) throw new InvalidOperationException("The selected showcase grid has no native suspension wheels.");
            log("ROVER native wheel speed limit=" + speed + "km/h suspensions=" + count);
        }
        static void ParkGrid(IMyCubeGrid grid)
        {
            if (grid.Physics != null) grid.Physics.ClearSpeed();
            foreach (IMyShipController controller in grid.GetFatBlocks<IMyShipController>()) controller.HandBrake = true;
        }
        void RefillRover(JObject command)
        {
            if (command["entityId"] == null) throw new ArgumentException("refillrover requires an explicit native main-grid entityId.");
            IMyEntity entity;
            if (!MyAPIGateway.Entities.TryGetEntityById((long)command["entityId"], out entity) || !(entity is IMyCubeGrid)) throw new ArgumentException("Rover entityId was not found.");
            IMyCubeGrid main = (IMyCubeGrid)entity;
            if (main.IsStatic || main.GridSizeEnum != MyCubeSize.Small) throw new InvalidOperationException("Only mobile small-grid rovers can be prepared.");
            var group = new List<IMyCubeGrid>(); MyAPIGateway.GridGroups.GetGroup(main, GridLinkTypeEnum.Physical, group);
            int batteries = 0;
            foreach (IMyCubeGrid grid in group)
            {
                foreach (IMyFunctionalBlock block in grid.GetFatBlocks<IMyFunctionalBlock>()) block.Enabled = true;
                foreach (IMyBatteryBlock battery in grid.GetFatBlocks<IMyBatteryBlock>())
                {
                    MyBatteryBlock native = battery as MyBatteryBlock;
                    if (native != null) native.CurrentStoredPower = native.MaxStoredPower;
                    battery.ChargeMode = Sandbox.ModAPI.Ingame.ChargeMode.Auto;
                    batteries++;
                }
                ParkGrid(grid);
            }
            roverStatus = "prepared: " + batteries + " batteries";
        }

        void SpawnGrid(JObject command, MatrixD frame)
        {
            if (testGrids.Count >= 8) throw new InvalidOperationException("Clear existing diagnostic grids first (maximum 8).");
            string name = (string)command["gridName"] ?? "test";
            if (name.Length == 0 || name.Length > 60) throw new ArgumentException("gridName must contain 1 to 60 characters.");
            foreach (TestGrid item in testGrids) if (item.Name == name && !item.Grid.Closed) throw new ArgumentException("gridName is already in use.");
            string kind = ((string)command["kind"] ?? "projectile").ToLowerInvariant();
            if (kind != "projectile" && kind != "drill") throw new ArgumentException("kind must be projectile or drill.");
            Vector3D position = Vector3D.Transform(ReadPoint(command), frame);
            if (Vector3D.DistanceSquared(position, MyAPIGateway.Session.Player.Character.GetPosition()) < 9)
                throw new InvalidOperationException("Spawn the diagnostic grid at least 3 metres from the character.");
            MatrixD orientation = frame; orientation.Translation = position;
            if (command["forwardX"] != null)
            {
                Vector3D forward = new Vector3D((double)command["forwardX"], (double?)command["forwardY"] ?? 0, (double?)command["forwardZ"] ?? 0);
                if (!Finite(forward.X) || !Finite(forward.Y) || !Finite(forward.Z) || forward.LengthSquared() < .01) throw new ArgumentException("Invalid grid forward vector.");
                forward = Vector3D.Normalize(Vector3D.TransformNormal(forward, frame));
                Vector3D up = frame.Up - forward * Vector3D.Dot(frame.Up, forward);
                if (up.LengthSquared() < .01) up = frame.Backward - forward * Vector3D.Dot(frame.Backward, forward);
                orientation = MatrixD.CreateWorld(position, forward, Vector3D.Normalize(up));
            }
            MyObjectBuilder_CubeGrid builder = new MyObjectBuilder_CubeGrid();
            builder.GridSizeEnum = MyCubeSize.Small;
            builder.IsStatic = (bool?)command["static"] ?? kind == "drill";
            builder.CreatePhysics = true; builder.DestructibleBlocks = true; builder.Editable = true;
            builder.DisplayName = "SEMC diagnostic grid / " + name;
            builder.PersistentFlags = MyPersistentEntityFlags2.InScene;
            builder.PositionAndOrientation = new MyPositionAndOrientation(orientation);
            builder.CubeBlocks = new List<MyObjectBuilder_CubeBlock>();
            if (kind == "projectile")
            {
                for (int y = -1; y <= 1; y++) for (int z = -1; z <= 1; z++) for (int x = -1; x <= 1; x++)
                    builder.CubeBlocks.Add(new MyObjectBuilder_CubeBlock {
                        SubtypeName = "SmallHeavyBlockArmorBlock", Min = new Vector3I(x, y, z),
                        BuildPercent = 1f, IntegrityPercent = 1f, ColorMaskHSV = new Color(217, 156, 70).ColorToHSVDX11()
                    });
            }
            else
            {
                MyObjectBuilder_CubeBlock battery = NewFunctional("MyObjectBuilder_BatteryBlock", "SmallBlockBatteryBlock", new Vector3I(0, 0, 0));
                var stored = battery.GetType().GetField("CurrentStoredPower");
                if (stored != null) stored.SetValue(battery, 1f);
                builder.CubeBlocks.Add(battery);
                MyObjectBuilder_CubeBlock drill = NewFunctional("MyObjectBuilder_Drill", "SmallBlockDrill", new Vector3I(0, 0, -6));
                ((MyObjectBuilder_FunctionalBlock)drill).Enabled = (bool?)command["enabled"] ?? true;
                builder.CubeBlocks.Add(drill);
            }
            IMyCubeGrid grid = MyAPIGateway.Entities.CreateFromObjectBuilderAndAdd(builder) as IMyCubeGrid;
            if (grid == null) throw new InvalidOperationException("Native grid creation returned no grid.");
            grid.Save = false;
            grid.OnGridSplit += OnGridSplit;
            testGrids.Add(new TestGrid { Grid = grid, Name = name, Kind = kind, Expires = BridgeIO.Now() + Math.Max(10, Math.Min(600, (int?)command["lifetimeSeconds"] ?? 120)) * 1000 });
            if (command["vx"] != null || command["vy"] != null || command["vz"] != null) SetGridVelocity(grid, command, frame);
            log("TEST GRID created id=" + grid.EntityId + " name=" + name + " kind=" + kind + " static=" + grid.IsStatic);
        }
        static MyObjectBuilder_CubeBlock NewFunctional(string type, string subtype, Vector3I min)
        {
            MyObjectBuilder_CubeBlock block = (MyObjectBuilder_CubeBlock)MyObjectBuilderSerializer.CreateNewObject(MyObjectBuilderType.Parse(type), subtype);
            block.Min = min; block.BuildPercent = block.IntegrityPercent = 1f;
            block.Owner = MyAPIGateway.Session.Player.IdentityId;
            ((MyObjectBuilder_FunctionalBlock)block).Enabled = true;
            return block;
        }
        IMyCubeGrid FindGrid(JObject command)
        {
            string name = (string)command["gridName"] ?? "test";
            foreach (TestGrid item in testGrids) if (item.Name == name && !item.Grid.Closed) return item.Grid;
            throw new ArgumentException("Unknown diagnostic gridName: " + name);
        }
        static void SetGridVelocity(IMyCubeGrid grid, JObject command, MatrixD frame)
        {
            if (grid.IsStatic || grid.Physics == null) throw new InvalidOperationException("Diagnostic grid is static or has no physics.");
            Vector3D speed = new Vector3D((double?)command["vx"] ?? 0, (double?)command["vy"] ?? 0, (double?)command["vz"] ?? 0);
            if (!Finite(speed.X) || !Finite(speed.Y) || !Finite(speed.Z) || speed.Length() > 40) throw new ArgumentException("Diagnostic grid speed must be at most 40 m/s.");
            grid.Physics.LinearVelocity = (Vector3)Vector3D.TransformNormal(speed, frame);
            grid.Physics.AngularVelocity = Vector3.Zero;
        }
        void OnGridSplit(IMyCubeGrid original, IMyCubeGrid child)
        {
            TestGrid source = testGrids.Find(delegate(TestGrid item) { return item.Grid == original; });
            if (source == null || child == null) return;
            child.Save = false; child.OnGridSplit += OnGridSplit;
            testGrids.Add(new TestGrid { Grid = child, Name = source.Name + "-split-" + child.EntityId, Kind = source.Kind, Expires = source.Expires });
        }
        void CloseGrid(TestGrid item)
        {
            if (item.Grid == null) return;
            item.Grid.OnGridSplit -= OnGridSplit;
            if (!item.Grid.Closed) item.Grid.Close();
        }
        public void Dispose()
        {
            pendingRoverReset = null; resettingRoverIds.Clear();
            try { StopMotion(); }
            catch (Exception error) { log("CONTROL stop during cleanup " + error.Message); }
            pendingCockpit = null; pendingPilot = null;
            if (characterMotionHarmony != null)
            {
                try { if (unpatchCharacterMotion != null) unpatchCharacterMotion.Invoke(characterMotionHarmony, new object[] { characterMotionPatchId }); }
                catch (Exception error) { log("CONTROL motion unpatch " + error.Message); }
                characterMotionHarmony = null;
            }
            foreach (TestGrid item in testGrids)
            {
                try { CloseGrid(item); }
                catch (Exception error) { log("CONTROL grid cleanup " + error.Message); }
            }
            testGrids.Clear();
        }

        static void RequireOnFoot(MyCharacter character)
        {
            if (character == null || MyAPIGateway.Session.Player.Controller == null || MyAPIGateway.Session.Player.Controller.ControlledEntity == null ||
                MyAPIGateway.Session.Player.Controller.ControlledEntity.Entity != character)
                throw new InvalidOperationException("Character must be controlled on foot.");
        }
        static void FirstPerson(MyCharacter character)
        {
            MyAPIGateway.Session.SetCameraController(MyCameraControllerEnum.Entity, character, null);
            character.IsInFirstPersonView = true;
        }
        static void Aim(MyCharacter character, Vector3D target, Vector3D fallbackUp)
        {
            float influence;
            Vector3 gravity = MyAPIGateway.Physics.CalculateNaturalGravityAt(character.WorldMatrix.Translation, out influence);
            Vector3D up = gravity.LengthSquared() > .001f ? -Vector3D.Normalize((Vector3D)gravity) : fallbackUp;
            // Native head angles are in degrees; positive local X looks upward.
            character.HeadLocalYAngle = 0;
            character.SetHeadLocalXAngle(0);
            Vector3D head = character.GetHeadMatrix(true, true, false, true, false).Translation;
            Vector3D direction = target - head;
            if (direction.LengthSquared() < .001) throw new ArgumentException("Aim target is too close to the head.");
            direction.Normalize();
            if (character.JetpackComp != null && character.JetpackComp.TurnedOn)
            {
                Vector3D flightUp = up - direction * Vector3D.Dot(up, direction);
                if (flightUp.LengthSquared() < .001) flightUp = character.WorldMatrix.Right;
                character.WorldMatrix = MatrixD.CreateWorld(character.WorldMatrix.Translation, direction, Vector3D.Normalize(flightUp));
                return;
            }
            Vector3D forward = direction - up * Vector3D.Dot(direction, up);
            if (forward.LengthSquared() < .0001) forward = character.WorldMatrix.Forward;
            forward -= up * Vector3D.Dot(forward, up);
            forward.Normalize();
            character.WorldMatrix = MatrixD.CreateWorld(character.WorldMatrix.Translation, forward, up);
            character.SetHeadLocalXAngle((float)(Math.Asin(MathHelper.Clamp(Vector3D.Dot(direction, up), -1, 1)) * 180 / Math.PI));
        }
        static Vector3D ReadPoint(JObject command)
        {
            if (command["x"] == null || command["y"] == null || command["z"] == null)
                throw new ArgumentException("Absolute Minecraft x, y and z are required.");
            Vector3D point = new Vector3D((double)command["x"], (double)command["y"], (double)command["z"]);
            if (!Finite(point.X) || !Finite(point.Y) || !Finite(point.Z) || Math.Abs(point.X) > 30000000 || Math.Abs(point.Z) > 30000000 || Math.Abs(point.Y) > 10000)
                throw new ArgumentException("Invalid Minecraft target coordinates.");
            return point;
        }
        static bool Finite(double value) { return !Double.IsNaN(value) && !Double.IsInfinity(value); }
        static bool IsTestWorld()
        {
            if (MyAPIGateway.Session == null || MyAPIGateway.Session.Player == null || MyAPIGateway.Session.Player.Character == null || MySession.Static == null) return false;
            return MyAPIGateway.Session.OnlineMode == MyOnlineModeEnum.OFFLINE &&
                String.Equals(Path.GetFullPath(MySession.Static.CurrentPath).TrimEnd('\\'), TestWorld, StringComparison.OrdinalIgnoreCase);
        }

        void WriteState(Snapshot data, MatrixD frame, long now)
        {
            MyCharacter character = MyAPIGateway.Session.Player.Character as MyCharacter;
            MatrixD inverse = MatrixD.Invert(frame);
            JObject value = new JObject();
            value["id"] = lastId; value["action"] = lastAction; value["result"] = lastResult;
            value["error"] = lastError; value["timestamp"] = now; value["commandTimestamp"] = commandAt;
            value["world"] = MySession.Static.CurrentPath;
            value["snapshotSequence"] = data == null ? 0 : data.Sequence;
            value["snapshotAgeMs"] = data == null ? -1 : now - data.Timestamp;
            value["colliderCount"] = physics.ColliderCount;
            value["retainedPhysicsChunks"] = physics.RetainedChunkCount;
            value["cachedPhysicsCells"] = physics.CachedCellCount;
            value["buildMode"] = input.Enabled; value["hudActive"] = input.CanDraw;
            value["equippedToolType"] = character == null || character.EquippedTool == null ? null : character.EquippedTool.GetType().Name;
            value["selectedItem"] = input.SelectedItem; value["selectedItemName"] = input.SelectedItemName;
            value["selectedItemId"]=input.SelectedItemId;value["catalogCount"]=input.ItemCount;
            value["economyBusy"]=Economy!=null&&Economy.Busy;
            value["inventoryOpen"]=UnifiedInventoryScreen.IsOpen;
            if(lastAction=="economy" && economyResult!=null)value["economy"]=economyResult;
            value["saving"] = MySession.Static.IsSaveInProgress;
            value["roverStatus"] = roverStatus;
            value["pickerOpen"] = BlockPicker.IsOpen;
            value["motionActive"] = motionUntil > now;
            value["motionRemainingMs"] = Math.Max(0, motionUntil - now);
            value["motionInput"] = Vector(motionInput);
            value["characterMotionOverrides"] = characterMotionOverrides;
            value["shipMotionOverrides"] = shipMotionOverrides;
            IMyEntity controlled = ControlledEntity();
            value["controlledEntityId"] = controlled == null ? 0 : controlled.EntityId;
            value["controlledEntityType"] = controlled == null ? null : controlled.GetType().Name;
            MyShipController controlledShip = controlled as MyShipController;
            if (controlledShip != null)
            {
                value["controlledGridId"] = controlledShip.CubeGrid.EntityId;
                value["controlledGridPositionMC"] = Vector(Vector3D.Transform(controlledShip.CubeGrid.WorldMatrix.Translation, inverse));
                value["controlledGridVelocityMC"] = Vector(controlledShip.CubeGrid.Physics == null ? Vector3D.Zero : Vector3D.TransformNormal(controlledShip.CubeGrid.Physics.LinearVelocity, inverse));
                value["controlledMoveIndicator"] = Vector(((IMyShipController)controlledShip).MoveIndicator);
                value["handBrake"] = ((IMyShipController)controlledShip).HandBrake;
            }
            value["sunDirectionMC"] = Vector(Vector3D.TransformNormal(MySector.SunProperties.SunDirectionNormalized, inverse));
            value["sunRotation"] = MySession.Static.Settings.EnableSunRotation;
            if (reportNearby)
            {
                JArray nativeGrids = new JArray();
                Vector3D center = MyAPIGateway.Session.Player.Character.GetPosition();
                var entities = new HashSet<IMyEntity>();
                MyAPIGateway.Entities.GetEntities(entities, delegate(IMyEntity entity) {
                    return entity is IMyCubeGrid && !entity.Closed && Vector3D.DistanceSquared(entity.GetPosition(), center) <= nearbyRadius * nearbyRadius;
                });
                foreach (IMyEntity entity in entities)
                {
                    IMyCubeGrid grid = (IMyCubeGrid)entity;
                    JObject entry = new JObject(); entry["name"] = grid.DisplayName; entry["entityId"] = grid.EntityId;
                    entry["positionMC"] = Vector(Vector3D.Transform(grid.GetPosition(), inverse));
                    entry["velocityMC"] = Vector(grid.Physics == null ? Vector3D.Zero : Vector3D.TransformNormal(grid.Physics.LinearVelocity, inverse));
                    var blocks = new List<IMySlimBlock>(); grid.GetBlocks(blocks, null); entry["blockCount"] = blocks.Count;
                    entry["static"] = grid.IsStatic; entry["saved"] = grid.Save; entry["size"] = grid.GridSizeEnum.ToString();
                    JArray cockpits = new JArray();
                    foreach (IMyShipController controller in grid.GetFatBlocks<IMyShipController>())
                    {
                        JObject cockpit = new JObject(); cockpit["entityId"] = controller.EntityId;
                        cockpit["working"] = controller.IsWorking; cockpit["handBrake"] = controller.HandBrake;
                        cockpit["positionMC"] = Vector(Vector3D.Transform(controller.GetPosition(), inverse)); cockpits.Add(cockpit);
                    }
                    entry["cockpits"] = cockpits;
                    JArray batteries = new JArray();
                    foreach (IMyBatteryBlock battery in grid.GetFatBlocks<IMyBatteryBlock>())
                    {
                        JObject info = new JObject(); info["entityId"] = battery.EntityId; info["enabled"] = battery.Enabled;
                        info["working"] = battery.IsWorking; info["currentMWh"] = battery.CurrentStoredPower; info["maxMWh"] = battery.MaxStoredPower;
                        info["chargeMode"] = battery.ChargeMode.ToString(); batteries.Add(info);
                    }
                    entry["batteries"] = batteries; nativeGrids.Add(entry);
                }
                value["nearbyGrids"] = nativeGrids; value["nearbyRadius"] = nearbyRadius;
            }
            JArray grids = new JArray();
            foreach (TestGrid item in testGrids)
            {
                IMyCubeGrid grid = item.Grid;
                if (grid == null || grid.Closed) continue;
                JObject record = new JObject();
                record["name"] = item.Name; record["kind"] = item.Kind; record["entityId"] = grid.EntityId;
                record["positionMC"] = Vector(Vector3D.Transform(grid.GetPosition(), inverse));
                record["velocityMC"] = Vector(grid.Physics == null ? Vector3D.Zero : Vector3D.TransformNormal(grid.Physics.LinearVelocity, inverse));
                record["physicsEnabled"] = grid.Physics != null && grid.Physics.Enabled;
                record["static"] = grid.IsStatic; record["save"] = grid.Save;
                var blocks = new List<IMySlimBlock>(); grid.GetBlocks(blocks, null); record["blockCount"] = blocks.Count;
                JArray drills = new JArray();
                foreach (IMyShipDrill api in grid.GetFatBlocks<IMyShipDrill>())
                {
                    MyShipDrill drill = api as MyShipDrill;
                    JObject details = new JObject(); details["enabled"] = api.Enabled; details["working"] = api.IsWorking;
                    if (drill != null && drill.DrillBase != null)
                    { details["cutOutMC"] = Vector(Vector3D.Transform(drill.DrillBase.CutOut.Sphere.Center, inverse)); details["cutOutRadius"] = drill.DrillBase.CutOut.Sphere.Radius; }
                    drills.Add(details);
                }
                record["drills"] = drills; grids.Add(record);
            }
            value["testGrids"] = grids;
            if (character != null)
            {
                MatrixD pose = character.WorldMatrix;
                value["positionSE"] = Vector(pose.Translation);
                value["positionMC"] = Vector(Vector3D.Transform(pose.Translation, inverse));
                value["forwardMC"] = Vector(Vector3D.TransformNormal(pose.Forward, inverse));
                value["upMC"] = Vector(Vector3D.TransformNormal(pose.Up, inverse));
                value["velocitySE"] = Vector(character.Physics == null ? Vector3D.Zero : (Vector3D)character.Physics.LinearVelocity);
                value["velocityMC"] = Vector(character.Physics == null ? Vector3D.Zero : Vector3D.TransformNormal(character.Physics.LinearVelocity, inverse));
                value["jetpack"] = character.JetpackComp != null && character.JetpackComp.TurnedOn;
                value["suitEnergy"] = character.SuitEnergyLevel;
                if (character.StatComp != null)
                {
                    value["health"] = character.StatComp.HealthRatio;
                    value["food"] = character.StatComp.FoodRatio;
                    if (character.StatComp.Health != null) value["healthValue"] = character.StatComp.Health.Value;
                    if (character.StatComp.Food != null) value["foodValue"] = character.StatComp.Food.Value;
                }
                if (character.OxygenComponent != null)
                {
                    value["suitOxygen"] = character.OxygenComponent.GetGasFillLevel(MyResourceDistributorComponent.OxygenId);
                    value["suitHydrogen"] = character.OxygenComponent.GetGasFillLevel(MyResourceDistributorComponent.HydrogenId);
                }
                value["firstPerson"] = character.IsInFirstPersonView;
                value["headPitchDegrees"] = character.HeadLocalXAngle;
                value["headYawDegrees"] = character.HeadLocalYAngle;
                value["movementState"] = character.GetCurrentMovementState().ToString();
                value["characterPhysicsEnabled"] = character.Physics != null && character.Physics.Enabled;
                var groundHits = new List<IHitInfo>();
                MyAPIGateway.Physics.CastRay(pose.Translation + frame.Up * 1.8, pose.Translation - frame.Up * 12, groundHits, 15);
                JArray ground = new JArray();
                foreach (IHitInfo hit in groundHits)
                {
                    if (hit.HitEntity == null || hit.HitEntity == character) continue;
                    JObject record = new JObject();
                    record["minecraftCollider"] = physics.Owns(hit.HitEntity);
                    record["entityId"] = hit.HitEntity.EntityId;
                    record["type"] = hit.HitEntity.GetType().Name;
                    record["positionMC"] = Vector(Vector3D.Transform(hit.Position, inverse));
                    record["normalMC"] = Vector(Vector3D.TransformNormal(hit.Normal, inverse));
                    ground.Add(record);
                }
                value["groundHits"] = ground;
            }
            if (MyAPIGateway.Session.Camera != null)
            {
                MatrixD camera = MyAPIGateway.Session.Camera.WorldMatrix;
                value["cameraPositionMC"] = Vector(Vector3D.Transform(camera.Translation, inverse));
                value["cameraForwardMC"] = Vector(Vector3D.TransformNormal(camera.Forward, inverse));
            }
            input.UpdateAim();
            value["hasSelection"] = input.HasSelection; value["hasPlacement"] = input.HasPlacement;
            if (input.HasSelection || input.HasPlacement) value["hitPositionMC"] = Vector(Vector3D.Transform(input.HitWorld, inverse));
            if (input.HasSelection) value["selectionLocal"] = Vector(input.SelectionCell);
            if (input.HasPlacement) value["placementLocal"] = Vector(input.PlacementCell);
            if (data != null)
            {
                Vector3I origin = new Vector3I(data.X, data.Y, data.Z);
                if (input.HasSelection) value["selectionMC"] = Vector(input.SelectionCell + origin);
                if (input.HasPlacement) value["placementMC"] = Vector(input.PlacementCell + origin);
            }
            // Atomic replacement keeps CLI readers from seeing truncated telemetry.
            string temporary = stateFile + ".tmp";
            File.WriteAllText(temporary, value.ToString());
            if (File.Exists(stateFile)) File.Replace(temporary, stateFile, null); else File.Move(temporary, stateFile);
        }
        static JArray Vector(Vector3D vector) { return new JArray(vector.X, vector.Y, vector.Z); }
    }
}
