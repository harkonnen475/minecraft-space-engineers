using System;
using System.Collections.Generic;
using VRageMath;

namespace SEMC.V2
{
    // Session-local source data for observed absolute chunks. Unknown cells never erase known cells.
    internal sealed class TerrainCache
    {
        internal const int Side = 8;
        internal sealed class Cells
        {
            internal readonly int[] States = new int[512], Colors = new int[512], Flags = new int[512];
            internal int Count;
        }
        internal readonly Dictionary<Vector3I, Cells> Chunks = new Dictionary<Vector3I, Cells>();
        internal int Count { get; private set; }
        internal long Revision { get; private set; }
        internal static Vector3I ChunkAt(Vector3I p)
        { return new Vector3I(Floor(p.X), Floor(p.Y), Floor(p.Z)); }
        static int Floor(int v) { return (int)Math.Floor(v / 8.0) * 8; }
        static int Index(int x, int y, int z) { return x + 8 * (z + 8 * y); }

        internal HashSet<Vector3I> Merge(int sx, int sy, int sz, int[] states, int[] colors, int[] flags, Vector3I origin)
        {
            var dirty = new HashSet<Vector3I>();
            Vector3I end = origin + new Vector3I(sx, sy, sz), first = ChunkAt(origin);
            for (int gy = first.Y; gy < end.Y; gy += 8)
            for (int gz = first.Z; gz < end.Z; gz += 8)
            for (int gx = first.X; gx < end.X; gx += 8)
            {
                Vector3I key = new Vector3I(gx, gy, gz); Cells cells;
                Chunks.TryGetValue(key, out cells); bool changed = false;
                for (int y = Math.Max(gy, origin.Y); y < Math.Min(gy + 8, end.Y); y++)
                for (int z = Math.Max(gz, origin.Z); z < Math.Min(gz + 8, end.Z); z++)
                for (int x = Math.Max(gx, origin.X); x < Math.Min(gx + 8, end.X); x++)
                {
                    int source = x - origin.X + sx * (z - origin.Z + sz * (y - origin.Y));
                    // Remote captures may include unloaded/unknown cells. They are not air.
                    if (states[source] < 0 || flags[source] < 0) continue;
                    bool present = states[source] != 0 && (flags[source] & 1) != 0;
                    if (cells == null) { if (!present) continue; cells = new Cells(); Chunks.Add(key, cells); }
                    int target = Index(x - gx, y - gy, z - gz);
                    int state = present ? states[source] : 0, color = present ? colors[source] : 0, flag = present ? flags[source] : 0;
                    if (cells.States[target] == state && cells.Colors[target] == color && cells.Flags[target] == flag) continue;
                    bool old = (cells.Flags[target] & 1) != 0;
                    if (old != present) { int delta = present ? 1 : -1; cells.Count += delta; Count += delta; }
                    cells.States[target] = state; cells.Colors[target] = color; cells.Flags[target] = flag; changed = true;
                }
                if (changed) Mark(dirty, key);
                if (cells != null && cells.Count == 0) Chunks.Remove(key);
            }
            if (dirty.Count != 0) Revision++;
            return dirty;
        }

        internal static void ValidateRemote(CachedTerrainChunk chunk)
        {
            if (chunk == null || chunk.States == null || chunk.Colors == null || chunk.Flags == null ||
                chunk.States.Length != 512 || chunk.Colors.Length != 512 || chunk.Flags.Length != 512 ||
                ChunkAt(chunk.Minimum) != chunk.Minimum)
                throw new ArgumentException("Remote terrain chunk requires aligned absolute coordinates and three 512-cell buffers.");
            for (int i = 0; i < 512; i++) if (chunk.States[i] < -1)
                throw new ArgumentException("Unknown remote terrain state must use -1.");
        }

        internal HashSet<Vector3I> Remove(Vector3I absoluteCell)
        {
            var dirty = new HashSet<Vector3I>(); Vector3I key = ChunkAt(absoluteCell); Cells cells;
            if (!Chunks.TryGetValue(key, out cells)) return dirty;
            Vector3I local = absoluteCell - key; int i = Index(local.X, local.Y, local.Z);
            if ((cells.Flags[i] & 1) == 0) return dirty;
            cells.States[i] = cells.Colors[i] = cells.Flags[i] = 0; cells.Count--; Count--;
            if (cells.Count == 0) Chunks.Remove(key);
            Mark(dirty, key); Revision++; return dirty;
        }
        static void Mark(HashSet<Vector3I> dirty, Vector3I key)
        {
            dirty.Add(key);
            dirty.Add(key + new Vector3I(8, 0, 0)); dirty.Add(key - new Vector3I(8, 0, 0));
            dirty.Add(key + new Vector3I(0, 8, 0)); dirty.Add(key - new Vector3I(0, 8, 0));
            dirty.Add(key + new Vector3I(0, 0, 8)); dirty.Add(key - new Vector3I(0, 0, 8));
        }
        internal void Halo(Vector3I key, int[] states, int[] colors, int[] flags)
        {
            for (int y = -1; y < 9; y++) for (int z = -1; z < 9; z++) for (int x = -1; x < 9; x++)
            {
                Vector3I p = key + new Vector3I(x, y, z), owner = ChunkAt(p); Cells cells;
                int target = x + 1 + 10 * (z + 1 + 10 * (y + 1));
                if (!Chunks.TryGetValue(owner, out cells)) { states[target] = colors[target] = flags[target] = 0; continue; }
                Vector3I local = p - owner; int source = Index(local.X, local.Y, local.Z);
                states[target] = cells.States[source]; colors[target] = cells.Colors[source]; flags[target] = cells.Flags[source];
            }
        }
        internal void Clear() { Chunks.Clear(); Count = 0; Revision++; }
    }
}
