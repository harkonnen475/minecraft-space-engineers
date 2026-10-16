using System;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Threading;
using Sandbox.ModAPI;
using Sandbox.Game.World;
using Sandbox.Graphics.GUI;
using VRage;
using VRage.ObjectBuilders;
using VRage.Game;
using VRage.Game.ModAPI;
using VRage.Plugins;
using VRage.ModAPI;
using VRageMath;

namespace SEMCLab
{
    public sealed class SEMCBridge : IPlugin
    {
        const string Root = @"C:\Users\konra\Documents\SEMC-Lab";
        const int SizeX = 12, SizeY = 8, SizeZ = 12, Count = 1152;
        readonly byte[] snapshot = new byte[32768];
        readonly int[] states = new int[Count];
        readonly int[] colors = new int[Count];
        readonly List<IMyCubeGrid> splitGrids = new List<IMyCubeGrid>();
        MemoryMappedFile map;
        MemoryMappedViewAccessor view;
        FileStream stream;
        IMyCubeGrid grid;
        MatrixD anchor;
        bool hasAnchor;
        object session;
        long nextTick, nextHud, nextLog, nextError, initializedAt, probeAfter;
        int lastSequence = -1, mirrored, updates;
        bool disposed;
        string status = "waiting for Minecraft";

        public void Init(object gameInstance)
        {
            initializedAt = Now();
            Log("INIT SE MC bridge v1.1; thread=" + Thread.CurrentThread.ManagedThreadId + "; no reverse edits");
        }

        public void Update()
        {
            if (disposed) return;
            long now = Now();
            if (now < nextTick) return;
            nextTick = now + 200;
            try { Tick(now); }
            catch (Exception error)
            {
                status = "bridge error; see se-bridge.log";
                if (now >= nextError) { Log(error.ToString()); nextError = now + 5000; }
            }
        }

        void Tick(long now)
        {
            TryAutoload(now);
            if (!Object.ReferenceEquals(session, MyAPIGateway.Session))
            {
                CloseGrid();
                hasAnchor = false;
                session = MyAPIGateway.Session;
                lastSequence = -1;
                Log(session == null ? "SESSION unloaded" : "SESSION detected");
            }
            bool worldReady = MyAPIGateway.Session != null && MyAPIGateway.Session.Player != null &&
                MyAPIGateway.Session.Player.Character != null && MyAPIGateway.Entities != null;
            bool local = worldReady && MyAPIGateway.Session.OnlineMode.ToString().Equals("OFFLINE", StringComparison.OrdinalIgnoreCase);
            if (view == null) TryOpen();
            if (view != null)
            {
                view.Write(32792, now);
                view.Write(32800, mirrored);
                view.Write(32804, local ? 2 : 0);
            }
            if (!worldReady) return;
            if (!local)
            {
                status = "disabled: offline singleplayer only";
                CloseGrid();
                ShowStatus(now);
                return;
            }
            if (File.Exists(Path.Combine(Root, "se-recenter.flag")))
            {
                File.Delete(Path.Combine(Root, "se-recenter.flag"));
                CloseGrid();
                hasAnchor = false;
                lastSequence = -1;
                Log("RECENTER requested");
            }
            if (view == null)
            {
                status = "waiting for bridge.bin";
                ShowStatus(now);
                return;
            }
            int sequence;
            if (!ReadSnapshot(out sequence))
            {
                status = "waiting for a valid MC snapshot";
                ShowStatus(now);
                return;
            }
            long timestamp = BitConverter.ToInt64(snapshot, 40);
            bool active = (BitConverter.ToInt32(snapshot, 36) & 1) != 0;
            if (!active || timestamp < now - 10000 || timestamp > now + 30000)
            {
                status = "Minecraft paused/disconnected; mirror frozen";
                view.Write(32804, 0);
                ShowStatus(now);
                return;
            }
            if (grid != null && (grid.Closed || grid.MarkedForClose))
            {
                grid = null;
                mirrored = 0;
                lastSequence = -1;
            }
            if (sequence != lastSequence || grid == null)
            {
                if (splitGrids.Count > 0) CloseGrid();
                if (grid == null) CreateGrid();
                else ApplyDiff();
                lastSequence = sequence;
                updates++;
            }
            status = "Minecraft LIVE: " + mirrored + " blocks | 0.5 m/block";
            view.Write(32800, mirrored);
            view.Write(32804, 2);
            if (now >= nextLog)
            {
                Log("SNAPSHOT seq=" + sequence + " blocks=" + mirrored + " updates=" + updates + " ageMs=" + (now - timestamp));
                nextLog = now + 10000;
            }
            ShowStatus(now);
            if (grid != null && now >= probeAfter &&
                (probeAfter > 0 || File.Exists(Path.Combine(Root, "se-probe.flag"))))
            {
                probeAfter = 0;
                string probeFlag = Path.Combine(Root, "se-probe.flag");
                if (File.Exists(probeFlag)) File.Delete(probeFlag);
                ProbeCollision();
            }
        }

        void TryAutoload(long now)
        {
            if (MyAPIGateway.Session != null || now < initializedAt + 8000) return;
            string flag = Path.Combine(Root, "load-test-world.flag");
            if (!File.Exists(flag)) return;
            MyGuiScreenBase focus = MyScreenManager.GetScreenWithFocus();
            if (focus == null || focus.GetType().FullName != "SpaceEngineers.Game.GUI.MyGuiScreenMainMenu" ||
                focus.State.ToString() != "OPENED") return;
            const string world = @"C:\Users\konra\AppData\Roaming\SpaceEngineers\Saves\76561198055314775\SEMC Lab - Bridge Test";
            if (!File.Exists(Path.Combine(world, "Sandbox.sbc")))
                throw new FileNotFoundException("SEMC test checkpoint missing", world);
            // Consume before entering the loader, so even a failed load cannot loop.
            File.Delete(flag);
            Log("AUTOLOAD " + world);
            MySessionLoader.LoadSingleplayerSession(world, delegate { Log("AUTOLOAD callback completed"); },
                null, MyOnlineModeEnum.OFFLINE, 1, null, null);
        }

        bool ReadSnapshot(out int sequence)
        {
            sequence = view.ReadInt32(8);
            if ((sequence & 1) != 0) return false;
            Thread.MemoryBarrier();
            view.ReadArray(0, snapshot, 0, snapshot.Length);
            Thread.MemoryBarrier();
            if (sequence != view.ReadInt32(8) || (sequence & 1) != 0) return false;
            return BitConverter.ToInt32(snapshot, 0) == 0x434D4553 &&
                BitConverter.ToInt32(snapshot, 4) == 1 &&
                BitConverter.ToInt32(snapshot, 12) == SizeX &&
                BitConverter.ToInt32(snapshot, 16) == SizeY &&
                BitConverter.ToInt32(snapshot, 20) == SizeZ &&
                BitConverter.ToInt32(snapshot, 80) == Count;
        }

        void TryOpen()
        {
            string path = Path.Combine(Root, "bridge.bin");
            if (!File.Exists(path)) return;
            stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length != 65536) { stream.Dispose(); stream = null; return; }
            map = MemoryMappedFile.CreateFromFile(stream, null, 65536, MemoryMappedFileAccess.ReadWrite, null, HandleInheritability.None, true);
            view = map.CreateViewAccessor(0, 65536, MemoryMappedFileAccess.ReadWrite);
            Log("MMF opened " + path);
        }

        void CreateGrid()
        {
            MyObjectBuilder_CubeGrid builder = new MyObjectBuilder_CubeGrid();
            builder.GridSizeEnum = MyCubeSize.Small;
            builder.IsStatic = true;
            builder.CreatePhysics = true;
            builder.DestructibleBlocks = false;
            builder.Editable = false;
            builder.DisplayName = "SEMC live Minecraft mirror";
            builder.PersistentFlags = MyPersistentEntityFlags2.InScene;
            if (!hasAnchor) { anchor = Placement(); hasAnchor = true; }
            builder.PositionAndOrientation = new MyPositionAndOrientation(anchor);
            builder.CubeBlocks = new List<MyObjectBuilder_CubeBlock>();
            mirrored = 0;
            for (int i = 0; i < Count; i++)
            {
                int state = BitConverter.ToInt32(snapshot, 128 + i * 8);
                int color = BitConverter.ToInt32(snapshot, 132 + i * 8);
                states[i] = state;
                colors[i] = color;
                if (state == 0) continue;
                builder.CubeBlocks.Add(Block(i, color));
                mirrored++;
            }
            if (mirrored == 0) return;
            grid = (IMyCubeGrid)MyAPIGateway.Entities.CreateFromObjectBuilderAndAdd(builder);
            grid.Save = false;
            grid.OnGridSplit += OnGridSplit;
            Log("GRID created id=" + grid.EntityId + " blocks=" + mirrored + " at=" + grid.GetPosition());
            Log("GRID physics=" + (grid.Physics == null ? "NULL" : ("enabled=" + grid.Physics.Enabled)) +
                " static=" + grid.IsStatic + " bounds=" + grid.WorldAABB);
            probeAfter = Now() + 2000;
            MyAPIGateway.Utilities.ShowNotification("SEMC: Minecraft mirror created 10 m ahead", 6000, "Green");
        }

        MatrixD Placement()
        {
            MatrixD player = MyAPIGateway.Session.Player.Character.WorldMatrix;
            Vector3D up = player.Up;
            float interference;
            Vector3 gravity = MyAPIGateway.Physics.CalculateNaturalGravityAt(player.Translation, out interference);
            if (gravity.LengthSquared() > 0.01f) up = -Vector3D.Normalize((Vector3D)gravity);
            Vector3D forward = player.Forward - up * Vector3D.Dot(player.Forward, up);
            if (forward.LengthSquared() < 0.01) forward = Vector3D.Cross(player.Right, up);
            forward.Normalize();
            // Grid Z is backward. Center the 6x4x6 m preview 10m ahead and 1m above the player's feet.
            MatrixD basis = MatrixD.CreateWorld(player.Translation + forward * 10 + up, forward, up);
            basis.Translation -= basis.Right * 2.75 + basis.Backward * 2.75;
            return basis;
        }

        static Vector3I Position(int i)
        {
            return new Vector3I(i % SizeX, i / (SizeX * SizeZ), (i / SizeX) % SizeZ);
        }

        static Vector3 Mask(int rgb)
        {
            return new Color((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb).ColorToHSVDX11();
        }

        static MyObjectBuilder_CubeBlock Block(int index, int rgb)
        {
            return new MyObjectBuilder_CubeBlock
            {
                SubtypeName = "SmallBlockArmorBlock",
                Min = Position(index),
                ColorMaskHSV = Mask(rgb),
                BuildPercent = 1f,
                IntegrityPercent = 1f
            };
        }

        void ApplyDiff()
        {
            int desiredCount = 0;
            for (int i = 0; i < Count; i++) if (BitConverter.ToInt32(snapshot, 128 + i * 8) != 0) desiredCount++;
            if (desiredCount == 0)
            {
                CloseGrid();
                Array.Clear(states, 0, states.Length);
                Array.Clear(colors, 0, colors.Length);
                Log("DIFF snapshot empty; closed ephemeral grid");
                return;
            }
            int changes = 0;
            // Add first. This prevents a relocating patch from destroying its last old block before new blocks exist.
            for (int i = 0; i < Count; i++)
            {
                int state = BitConverter.ToInt32(snapshot, 128 + i * 8);
                int color = BitConverter.ToInt32(snapshot, 132 + i * 8);
                if (state == 0 || (state == states[i] && color == colors[i])) continue;
                Vector3I pos = Position(i);
                IMySlimBlock current = grid.GetCubeBlock(pos);
                if (current == null)
                {
                    if (grid.AddBlock(Block(i, color), false) == null)
                        throw new InvalidOperationException("SE refused MC mirror block at " + pos);
                }
                else grid.ColorBlocks(pos, pos, Mask(color));
                states[i] = state;
                colors[i] = color;
                changes++;
            }
            for (int i = 0; i < Count; i++)
            {
                if (BitConverter.ToInt32(snapshot, 128 + i * 8) != 0 || states[i] == 0) continue;
                IMySlimBlock current = grid.GetCubeBlock(Position(i));
                if (current != null) grid.RemoveBlock(current, true);
                states[i] = 0;
                colors[i] = BitConverter.ToInt32(snapshot, 132 + i * 8);
                changes++;
            }
            mirrored = desiredCount;
            if (changes > 0) Log("DIFF changes=" + changes + " solid=" + mirrored);
        }

        void ProbeCollision()
        {
            if (grid == null || grid.Closed || grid.MarkedForClose) return;
            int highest = -1;
            for (int i = Count - 1; i >= 0; i--) if (states[i] != 0) { highest = i; break; }
            if (highest < 0) return;
            Vector3D center = Vector3D.Transform((Vector3D)Position(highest) * 0.5, grid.WorldMatrix);
            Vector3D from = center + grid.WorldMatrix.Up * 0.8;
            Vector3D to = center - grid.WorldMatrix.Up * 0.5;
            IHitInfo hit;
            bool found = MyAPIGateway.Physics.CastRay(from, to, out hit, 15);
            long hitId = found && hit != null && hit.HitEntity != null ? hit.HitEntity.EntityId : 0;
            Log("PROBE ray hit=" + found + " entity=" + hitId + " expected=" + grid.EntityId +
                " match=" + (hitId == grid.EntityId) + " at=" + (found && hit != null ? hit.Position.ToString() : "none") +
                " physics=" + (grid.Physics == null ? "NULL" : ("enabled=" + grid.Physics.Enabled)) +
                " static=" + grid.IsStatic + " bounds=" + grid.WorldAABB);
        }

        void OnGridSplit(IMyCubeGrid original, IMyCubeGrid part)
        {
            if (part == null || Object.ReferenceEquals(part, original)) return;
            part.Save = false;
            splitGrids.Add(part);
            // Rebuild from authoritative MC snapshot next update; never send inferred deletions to MC.
            lastSequence = -1;
            Log("SPLIT detected; ephemeral child=" + part.EntityId);
        }

        void CloseGrid()
        {
            if (grid != null)
            {
                grid.OnGridSplit -= OnGridSplit;
                if (!grid.Closed && !grid.MarkedForClose) grid.Close();
                grid = null;
            }
            foreach (IMyCubeGrid child in splitGrids)
                if (!child.Closed && !child.MarkedForClose) child.Close();
            splitGrids.Clear();
            mirrored = 0;
        }

        void ShowStatus(long now)
        {
            if (now < nextHud) return;
            nextHud = now + 3000;
            MyAPIGateway.Utilities.ShowNotification("SEMC: " + status, 2800, "White");
        }

        static long Now() { return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); }
        static void Log(string text)
        {
            try { File.AppendAllText(Path.Combine(Root, "se-bridge.log"), DateTime.UtcNow.ToString("o") + " " + text + Environment.NewLine); }
            catch { }
        }

        public void Dispose()
        {
            disposed = true;
            try { CloseGrid(); } catch { }
            if (view != null) view.Dispose();
            if (map != null) map.Dispose();
            if (stream != null) stream.Dispose();
            Log("DISPOSE");
        }
    }
}
