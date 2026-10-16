using System;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using VRageMath;

namespace SEMC.V2
{
    // Bind one reader to one native Minecraft identity. Recreate it when the host changes worlds.
    // Call on the game thread; the writer owns all bytes in the ring.
    public sealed class RemoteChunks : IDisposable
    {
        const int HeaderSize = 4096, Capacity = 256, SlotSize = 8192, Length = HeaderSize + Capacity * SlotSize;
        readonly string root;
        readonly Action<string> log;
        readonly byte[] header = new byte[HeaderSize], slot = new byte[SlotSize];
        FileStream stream;
        MemoryMappedFile mapping;
        MemoryMappedViewAccessor view;
        long cursor, epoch, nextOpen, nextIdentity, nextInterests, nextError;
        long interestRevision = DateTime.UtcNow.Ticks, sourceRevision = -1;
        bool hasEpoch, disposed, resend = true;
        Vector3I[] origins = new Vector3I[0];
        readonly HashSet<Vector3I> interestHistory = new HashSet<Vector3I>();
        int lastOverflowCount;
        public string WorldId { get; private set; }
        public string Dimension { get; private set; }
        public string PaletteFingerprint { get; private set; }
        public int RequiredPaletteRevision { get; private set; }
        public long LastSequence { get { return cursor; } }
        public long Epoch { get { return epoch; } }
        public int InterestCount { get { return origins.Length; } }
        public int Overruns { get; private set; }

        public RemoteChunks(string rootPath, Action<string> logger)
        { root = rootPath; log = logger ?? delegate { }; BindIdentity(); }

        bool BindIdentity()
        {
            if (WorldId != null) return true;
            long now = BridgeIO.Now(); if (now < nextIdentity) return false; nextIdentity = now + 1000;
            try
            {
                string path = Path.Combine(root, "world-v2.json"); if (!File.Exists(path)) return false;
                var row = new JavaScriptSerializer().DeserializeObject(RenderFiles.ReadText(path)) as Dictionary<string, object>;
                string world = Field(row, "worldId"), dimension = Field(row, "dimension"), fingerprint = Field(row, "paletteFingerprint");
                Guid parsed;
                if (!Guid.TryParse(world, out parsed) || String.IsNullOrWhiteSpace(dimension) || fingerprint.Length != 64 || fingerprint.Any(c => !Uri.IsHexDigit(c))) return false;
                WorldId = parsed.ToString("D"); Dimension = dimension; PaletteFingerprint = fingerprint.ToLowerInvariant();
                return true;
            }
            catch (Exception error) { Error("identity", error); return false; }
        }
        static string Field(Dictionary<string, object> row, string name)
        { object value; return row != null && row.TryGetValue(name, out value) ? Convert.ToString(value) : ""; }

        // Consumes at most 32 slots per call, even when some overlap the active capture.
        // RequiredPaletteRevision applies only to the returned chunks; load descriptors BEFORE applying them.
        public List<CachedTerrainChunk> Poll(Snapshot activeSnapshot = null, int maxChunks = 32)
        {
            var result = new List<CachedTerrainChunk>(); RequiredPaletteRevision = 0;
            if (disposed || !BindIdentity()) return result;
            try
            {
                if (!Open() || !ReadStable(0, 8, header)) return result;
                if (I32(header,0) != 0x324B4353 || I32(header,4) != 2 || I32(header,12) != Capacity ||
                    I32(header,16) != SlotSize || I32(header,20) != 8 || I32(header,24) != 12 || I32(header,28) != HeaderSize)
                    return result;
                if (!IdentityMatches()) return result;
                long latest = I64(header,32), incomingEpoch = I64(header,40), timestamp = I64(header,48);
                long now = BridgeIO.Now();
                if (latest < 0 || timestamp <= 0 || timestamp > now + 1000 || now - timestamp > 5000)
                { CloseMapping(); nextOpen = BridgeIO.Now() + 1000; return result; }
                if (!hasEpoch || epoch != incomingEpoch)
                {
                    epoch = incomingEpoch; hasEpoch = true; cursor = Math.Max(0, latest - Capacity);
                    RequestResend(false);
                }
                if (latest < cursor) { cursor = Math.Max(0, latest - Capacity); RequestResend(true); }
                if (latest - cursor > Capacity) { cursor = latest - Capacity; RequestResend(true); }
                int budget = Math.Max(1, Math.Min(32, maxChunks));
                var positions = new Dictionary<Vector3I, int>();
                for (int scanned = 0; scanned < budget && cursor < latest; scanned++)
                {
                    long sequence = cursor + 1;
                    long offset = HeaderSize + ((sequence - 1) % Capacity) * SlotSize;
                    if (!ReadStable(offset, 0, slot)) break;
                    long actual = I64(slot,8);
                    if (actual != sequence || I64(slot,56) != epoch)
                    {
                        if (actual > sequence) RequestResend(true);
                        break; // Never advance through an overwritten, incomplete or mismatched sample.
                    }
                    cursor = sequence;
                    if (I32(slot,36) != 512 || I32(slot,44) < 0 || (I32(slot,44) & 1) == 0) continue;
                    var minimum = new Vector3I(I32(slot,24), I32(slot,28), I32(slot,32));
                    if ((minimum.X & 7) != 0 || (minimum.Y & 7) != 0 || (minimum.Z & 7) != 0) continue;
                    // The main capture arrives more frequently. Preserve its covered cells, but
                    // still apply the remote part of a chunk crossing an unaligned capture edge.
                    bool maskActive = Overlaps(minimum, activeSnapshot);
                    var chunk = new CachedTerrainChunk { Minimum = minimum, States = new int[512], Colors = new int[512], Flags = new int[512] };
                    bool valid = true, hasKnown = false;
                    for (int i = 0; i < 512; i++)
                    {
                        int cell = 128 + i * 12;
                        chunk.States[i] = I32(slot,cell); chunk.Colors[i] = I32(slot,cell+4); chunk.Flags[i] = I32(slot,cell+8);
                        if (chunk.States[i] < -1) valid = false;
                        if (maskActive && Contains(activeSnapshot, (long)minimum.X + i % 8,
                            (long)minimum.Y + i / 64, (long)minimum.Z + (i / 8) % 8))
                        { chunk.States[i] = -1; chunk.Colors[i] = chunk.Flags[i] = 0; }
                        else if (chunk.States[i] >= 0 && chunk.Flags[i] >= 0) hasKnown = true;
                    }
                    if (!valid || !hasKnown) continue;
                    int index;
                    if (positions.TryGetValue(minimum, out index)) result[index] = chunk;
                    else { positions.Add(minimum, result.Count); result.Add(chunk); }
                    RequiredPaletteRevision = Math.Max(RequiredPaletteRevision, I32(slot,40));
                }
            }
            catch (Exception error) { Error("read", error); CloseMapping(); nextOpen = BridgeIO.Now() + 1000; }
            return result;
        }

        static bool Overlaps(Vector3I minimum, Snapshot active)
        {
            long now = BridgeIO.Now();
            return active != null && active.Active && active.Timestamp > 0 && active.Timestamp <= now + 1000 && now - active.Timestamp < 3000 &&
                (long)minimum.X < (long)active.X + active.SizeX && (long)minimum.X + 8 > active.X &&
                (long)minimum.Y < (long)active.Y + active.SizeY && (long)minimum.Y + 8 > active.Y &&
                (long)minimum.Z < (long)active.Z + active.SizeZ && (long)minimum.Z + 8 > active.Z;
        }
        static bool Contains(Snapshot active, long x, long y, long z)
        {
            return x >= active.X && x < (long)active.X + active.SizeX &&
                y >= active.Y && y < (long)active.Y + active.SizeY &&
                z >= active.Z && z < (long)active.Z + active.SizeZ;
        }
        bool IdentityMatches()
        {
            return String.Equals(Text(header,64,40), WorldId, StringComparison.OrdinalIgnoreCase) &&
                String.Equals(Text(header,104,64), Dimension, StringComparison.Ordinal) &&
                String.Equals(Text(header,168,64), PaletteFingerprint, StringComparison.OrdinalIgnoreCase);
        }
        bool ReadStable(long offset, int sequenceOffset, byte[] buffer)
        {
            int before = view.ReadInt32(offset + sequenceOffset); if ((before & 1) != 0) return false;
            Thread.MemoryBarrier(); view.ReadArray(offset, buffer, 0, buffer.Length); Thread.MemoryBarrier();
            return before == I32(buffer,sequenceOffset) && before == view.ReadInt32(offset + sequenceOffset);
        }
        bool Open()
        {
            if (view != null) return true;
            if (BridgeIO.Now() < nextOpen) return false;
            string path = Path.Combine(root, "chunks-v2.bin"); if (!File.Exists(path)) return false;
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length != Length) { CloseMapping(); nextOpen = BridgeIO.Now() + 1000; return false; }
            mapping = MemoryMappedFile.CreateFromFile(stream, null, Length, MemoryMappedFileAccess.Read, null, HandleInheritability.None, true);
            view = mapping.CreateViewAccessor(0, Length, MemoryMappedFileAccess.Read); return true;
        }

        // The host can call every frame; source snapshots are inspected at most once per second.
        // Only changed origin sets (or a ring overrun/epoch change) rewrite the interests file.
        public void UpdateInterests(MinecraftRenderer renderer)
        {
            if (disposed || renderer == null || !BindIdentity()) return;
            long now = BridgeIO.Now(); if (now < nextInterests) return; nextInterests = now + 1000;
            try
            {
                if (sourceRevision != renderer.CacheRevision)
                {
                    var centers = new HashSet<Vector3I>(renderer.CaptureRetainedOrigins());
                    var neighbours = new HashSet<Vector3I>();
                    foreach (Vector3I p in centers)
                    {
                        neighbours.Add(p + new Vector3I(8,0,0)); neighbours.Add(p - new Vector3I(8,0,0));
                        neighbours.Add(p + new Vector3I(0,8,0)); neighbours.Add(p - new Vector3I(0,8,0));
                        neighbours.Add(p + new Vector3I(0,0,8)); neighbours.Add(p - new Vector3I(0,0,8));
                    }
                    interestHistory.UnionWith(centers); interestHistory.UnionWith(neighbours);
                    // Keep temporarily empty chunks subscribed. At the bound, current structures
                    // and their moving/falling-block boundary take priority over empty history.
                    Vector3I[] incoming = interestHistory.OrderBy(p => centers.Contains(p) ? 0 : neighbours.Contains(p) ? 1 : 2)
                        .ThenBy(p => p.X).ThenBy(p => p.Y).ThenBy(p => p.Z).Take(8192).ToArray();
                    if (interestHistory.Count > 8192)
                    {
                        if (lastOverflowCount != interestHistory.Count) log("REMOTE interest limit reached; prioritizing retained structures and neighbouring chunks");
                        lastOverflowCount = interestHistory.Count;
                        interestHistory.Clear(); interestHistory.UnionWith(incoming);
                    }
                    // Changes in priority alone must not rewrite an otherwise identical set.
                    incoming = incoming.OrderBy(p => p.X).ThenBy(p => p.Y).ThenBy(p => p.Z).ToArray();
                    if (!origins.SequenceEqual(incoming)) { origins = incoming; resend = true; }
                    sourceRevision = renderer.CacheRevision;
                }
                if (!resend) return;
                interestRevision++;
                var row = new Dictionary<string, object> {
                    {"version",2},{"worldId",WorldId},{"dimension",Dimension},{"paletteFingerprint",PaletteFingerprint},
                    {"revision",interestRevision},{"origins",origins.Select(p => new[] {p.X,p.Y,p.Z}).ToArray()}
                };
                string path = Path.Combine(root, "retained-chunks-v2.json"), temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    byte[] bytes = new UTF8Encoding(false).GetBytes(new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024 }.Serialize(row));
                    using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    { file.Write(bytes,0,bytes.Length); file.Flush(true); }
                    if (File.Exists(path)) File.Replace(temporary,path,null); else File.Move(temporary,path);
                    resend = false;
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
            catch (Exception error) { Error("interests",error); }
        }
        void RequestResend(bool overrun)
        {
            resend = true; nextInterests = 0;
            if (overrun) { Overruns++; log("REMOTE ring overrun; requesting complete retained-chunk refresh"); }
        }
        void Error(string operation, Exception error)
        {
            long now = BridgeIO.Now(); if (now < nextError) return; nextError = now + 5000;
            log("REMOTE " + operation + ": " + error.GetType().Name + ": " + error.Message);
        }
        static int I32(byte[] bytes,int offset) { return BitConverter.ToInt32(bytes,offset); }
        static long I64(byte[] bytes,int offset) { return BitConverter.ToInt64(bytes,offset); }
        static string Text(byte[] bytes,int offset,int count)
        { int end = offset; while (end < offset + count && bytes[end] != 0) end++; return Encoding.UTF8.GetString(bytes,offset,end-offset); }
        void CloseMapping()
        {
            if (view != null) view.Dispose(); view = null;
            if (mapping != null) mapping.Dispose(); mapping = null;
            if (stream != null) stream.Dispose(); stream = null;
        }
        public void Dispose() { disposed = true; CloseMapping(); }
    }
}
