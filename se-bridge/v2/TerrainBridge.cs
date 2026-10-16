using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using Sandbox.ModAPI;
using VRage.Game.ModAPI;
using VRageMath;

namespace SEMC.V2
{
    // The hidden Minecraft server needs native SE ground for gravity and redstone support.
    // Raycasts run on the game thread, spread across frames; only planet/asteroid terrain is sampled.
    public sealed class TerrainBridge
    {
        readonly string path;
        readonly Action<string> log;
        readonly List<IHitInfo> hits = new List<IHitInfo>();
        int originX, originZ, sizeX, sizeZ, baseY, cursor;
        int?[] heights;
        long nextSweep, revision;
        ulong previousHash;
        public TerrainBridge(string root, Action<string> logger)
        { path = Path.Combine(root, "terrain-v2.json"); log = logger; revision = BridgeIO.Now(); }
        public void Update(Snapshot data, MatrixD frame)
        {
            if (data == null) return;
            long now = BridgeIO.Now();
            bool moved = heights == null || originX != data.X || originZ != data.Z || sizeX != data.SizeX || sizeZ != data.SizeZ || baseY != data.Y;
            if (moved)
            {
                originX = data.X; originZ = data.Z; baseY = data.Y; sizeX = data.SizeX; sizeZ = data.SizeZ;
                heights = new int?[sizeX * sizeZ]; cursor = 0; nextSweep = 0;
            }
            if (now < nextSweep) return;
            MatrixD inverse = MatrixD.Invert(frame);
            int end = Math.Min(cursor + 16, heights.Length);
            for (; cursor < end; cursor++)
            {
                int x = originX + cursor % sizeX, z = originZ + cursor / sizeX;
                Vector3D from = Vector3D.Transform(new Vector3D(x + .5, baseY + data.SizeY + 16, z + .5), frame);
                Vector3D to = Vector3D.Transform(new Vector3D(x + .5, baseY - 32, z + .5), frame);
                hits.Clear(); MyAPIGateway.Physics.CastRay(from, to, hits, 15);
                double highest = Double.NegativeInfinity;
                foreach (IHitInfo hit in hits)
                {
                    if (hit.HitEntity == null || hit.HitEntity.GetType().Name.IndexOf("Voxel", StringComparison.Ordinal) < 0) continue;
                    double y = Vector3D.Transform(hit.Position, inverse).Y;
                    if (y > highest) highest = y;
                }
                heights[cursor] = Double.IsNegativeInfinity(highest) ? (int?)null : (int)Math.Floor(highest);
            }
            if (cursor < heights.Length) return;
            cursor = 0; nextSweep = now + 1500;
            ulong hash = 1469598103934665603UL;
            unchecked
            {
                hash = (hash ^ (uint)originX) * 1099511628211UL;
                hash = (hash ^ (uint)originZ) * 1099511628211UL;
                foreach (int? height in heights) hash = (hash ^ (uint)(height ?? Int32.MinValue)) * 1099511628211UL;
            }
            if (hash == previousHash) return;
            var document = new JObject();
            document["version"] = 2; document["revision"] = ++revision; document["timestamp"] = now;
            document["originX"] = originX; document["originZ"] = originZ; document["sizeX"] = sizeX; document["sizeZ"] = sizeZ;
            var array = new JArray(); foreach (int? height in heights) array.Add(height.HasValue ? new JValue(height.Value) : JValue.CreateNull());
            document["heights"] = array;
            string temporary = path + ".tmp";
            File.WriteAllText(temporary, document.ToString(Newtonsoft.Json.Formatting.None));
            if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
            previousHash = hash;
            log("TERRAIN sampled " + heights.Length + " columns origin=" + originX + "," + originZ);
        }
    }
}
