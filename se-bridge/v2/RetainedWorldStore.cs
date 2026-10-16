using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using VRageMath;

namespace SEMC.V2
{
    public sealed class CachedTerrainChunk
    {
        // Absolute minimum corner; 8 cubed cells, x + 8 * (z + 8 * y).
        public Vector3I Minimum;
        public int[] States, Colors, Flags;
    }
    public sealed class RetainedWorldSnapshot
    {
        public string Identity;
        public long Revision;
        public CachedTerrainChunk[] Chunks;
    }

    // Last-observed cache, never a replacement for Minecraft's saved world or a fresh capture.
    // The host restores BOTH consumers before their first authoritative snapshot update.
    public sealed class RetainedWorldStore
    {
        const int Format = 1, MaxChunks = 32768, MaxBytes = 256 * 1024 * 1024;
        static readonly byte[] Magic = Encoding.ASCII.GetBytes("SEMCACH1");
        readonly string path;
        readonly Action<string> log;
        readonly byte[] identityBytes;
        long savedRevision = -1, observedRevision = -1, firstDirty, lastChanged;
        public string Identity { get; private set; }

        public RetainedWorldStore(string root, string identity, Action<string> logger)
        {
            if (identity == null || identity.Length != 64) throw new ArgumentException("A validated world identity is required.");
            Identity = identity; log = logger ?? delegate { };
            identityBytes = Enumerable.Range(0, 32).Select(i => Convert.ToByte(identity.Substring(i * 2, 2), 16)).ToArray();
            path = Path.Combine(root, "world-cache", "retained-" + identity + ".bin");
        }

        public static string ReadIdentity(string root, string seWorldPath, MatrixD absoluteFrame)
        {
            string file = Path.Combine(root, "world-v2.json");
            if (!File.Exists(file)) return null;
            try
            {
                var row = new JavaScriptSerializer().DeserializeObject(RenderFiles.ReadText(file)) as Dictionary<string, object>;
                string world = Field(row, "worldId"), dimension = Field(row, "dimension"), palette = Field(row, "paletteFingerprint");
                Guid id;
                if (!Guid.TryParse(world, out id) || String.IsNullOrWhiteSpace(dimension) || palette.Length != 64 ||
                    palette.Any(c => !Uri.IsHexDigit(c))) return null;
                string save = Path.GetFullPath(seWorldPath).TrimEnd('\\', '/').ToUpperInvariant();
                var text = new StringBuilder("SEMC retained world 1\n");
                text.Append(id.ToString("D")).Append('\n').Append(dimension).Append('\n').Append(palette.ToLowerInvariant()).Append('\n');
                text.Append(Field(row, "minecraftVersion")).Append('\n').Append(save).Append('\n');
                foreach (double value in new[] { absoluteFrame.M11,absoluteFrame.M12,absoluteFrame.M13,absoluteFrame.M14,
                    absoluteFrame.M21,absoluteFrame.M22,absoluteFrame.M23,absoluteFrame.M24,
                    absoluteFrame.M31,absoluteFrame.M32,absoluteFrame.M33,absoluteFrame.M34,
                    absoluteFrame.M41,absoluteFrame.M42,absoluteFrame.M43,absoluteFrame.M44 })
                    text.Append(value.ToString("R", CultureInfo.InvariantCulture)).Append(';');
                using (var sha = SHA256.Create()) return Hex(sha.ComputeHash(Encoding.UTF8.GetBytes(text.ToString())));
            }
            catch (IOException) { return null; }
            catch (ArgumentException) { return null; }
            catch (InvalidOperationException) { return null; }
        }
        static string Field(Dictionary<string, object> row, string key)
        { object value; return row != null && row.TryGetValue(key, out value) ? Convert.ToString(value, CultureInfo.InvariantCulture) : ""; }
        static string Hex(byte[] data) { return BitConverter.ToString(data).Replace("-", "").ToLowerInvariant(); }

        public RetainedWorldSnapshot Load()
        {
            if (!File.Exists(path)) return null;
            try
            {
                byte[] bytes;
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    if (stream.Length < 96 || stream.Length > MaxBytes) throw new InvalidDataException("Cache length is invalid.");
                    bytes = new byte[(int)stream.Length]; int offset = 0, read;
                    while (offset < bytes.Length && (read = stream.Read(bytes, offset, bytes.Length - offset)) > 0) offset += read;
                    if (offset != bytes.Length) throw new EndOfStreamException();
                }
                using (var sha = SHA256.Create())
                    if (!sha.ComputeHash(bytes, 0, bytes.Length - 32).SequenceEqual(bytes.Skip(bytes.Length - 32)))
                        throw new InvalidDataException("Cache checksum mismatch.");
                using (var stream = new MemoryStream(bytes, 0, bytes.Length - 32, false))
                using (var reader = new BinaryReader(stream))
                {
                    if (!reader.ReadBytes(8).SequenceEqual(Magic) || reader.ReadInt32() != Format || !reader.ReadBytes(32).SequenceEqual(identityBytes))
                        throw new InvalidDataException("Cache identity or format mismatch.");
                    reader.ReadInt64(); long revision = reader.ReadInt64(); int count = reader.ReadInt32();
                    if (count < 0 || count > MaxChunks) throw new InvalidDataException("Cache chunk count is invalid.");
                    var chunks = new CachedTerrainChunk[count]; var seen = new HashSet<Vector3I>();
                    for (int c = 0; c < count; c++)
                    {
                        Vector3I min = new Vector3I(reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32());
                        int cells = reader.ReadUInt16();
                        if ((min.X & 7) != 0 || (min.Y & 7) != 0 || (min.Z & 7) != 0 || !seen.Add(min) || cells < 1 || cells > 512)
                            throw new InvalidDataException("Cache chunk coordinates or cell count is invalid.");
                        var chunk = new CachedTerrainChunk { Minimum = min, States = new int[512], Colors = new int[512], Flags = new int[512] };
                        for (int n = 0; n < cells; n++)
                        {
                            int i = reader.ReadUInt16(), state = reader.ReadInt32(), color = reader.ReadInt32(), flags = reader.ReadInt32();
                            if (i >= 512 || (flags & 1) == 0 || state < 0 || chunk.Flags[i] != 0) throw new InvalidDataException("Cache cell is invalid.");
                            chunk.States[i] = state; chunk.Colors[i] = color; chunk.Flags[i] = flags;
                        }
                        chunks[c] = chunk;
                    }
                    if (stream.Position != stream.Length) throw new InvalidDataException("Unexpected cache trailing data.");
                    log("CACHE restored source chunks=" + count + " cells=" + chunks.Sum(c => c.Flags.Count(f => (f & 1) != 0)));
                    return new RetainedWorldSnapshot { Identity = Identity, Revision = revision, Chunks = chunks };
                }
            }
            catch (Exception error) { log("CACHE restore skipped: " + error.GetType().Name + ": " + error.Message); return null; }
        }

        public void SaveIfDue(MinecraftRenderer renderer, bool force = false)
        {
            if (renderer == null || renderer.CacheRevision == savedRevision) return;
            long now = DateTime.UtcNow.Ticks;
            if (observedRevision != renderer.CacheRevision)
            {
                observedRevision = renderer.CacheRevision; lastChanged = now;
                if (firstDirty == 0) firstDirty = now;
            }
            if (!force && now - lastChanged < TimeSpan.TicksPerSecond * 2 && now - firstDirty < TimeSpan.TicksPerSecond * 10) return;
            try
            {
                RetainedWorldSnapshot snapshot = renderer.CaptureRetained(); snapshot.Identity = Identity;
                Save(snapshot); savedRevision = snapshot.Revision; firstDirty = 0;
            }
            catch (Exception error)
            {
                // Preserve the previous valid file and avoid retrying IO on every simulation frame.
                firstDirty = lastChanged = now;
                log("CACHE save deferred: " + error.GetType().Name + ": " + error.Message);
            }
        }

        public void Save(RetainedWorldSnapshot snapshot)
        {
            if (snapshot == null || snapshot.Identity != Identity || snapshot.Chunks == null || snapshot.Chunks.Length > MaxChunks)
                throw new InvalidDataException("Cache snapshot identity or size is invalid.");
            byte[] bytes;
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(Magic); writer.Write(Format); writer.Write(identityBytes); writer.Write(DateTime.UtcNow.Ticks);
                writer.Write(snapshot.Revision); writer.Write(snapshot.Chunks.Length);
                foreach (CachedTerrainChunk chunk in snapshot.Chunks)
                {
                    int count = chunk.Flags.Count(f => (f & 1) != 0);
                    if (count < 1 || chunk.Flags.Length != 512 || chunk.States.Length != 512 || chunk.Colors.Length != 512)
                        throw new InvalidDataException("Cache snapshot contains an invalid chunk.");
                    writer.Write(chunk.Minimum.X); writer.Write(chunk.Minimum.Y); writer.Write(chunk.Minimum.Z); writer.Write((ushort)count);
                    for (int i = 0; i < 512; i++) if ((chunk.Flags[i] & 1) != 0)
                    { writer.Write((ushort)i); writer.Write(chunk.States[i]); writer.Write(chunk.Colors[i]); writer.Write(chunk.Flags[i]); }
                }
                writer.Flush(); bytes = stream.ToArray();
            }
            if (bytes.Length + 32 > MaxBytes) throw new InvalidDataException("Cache exceeds its 256 MiB persistence limit.");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.WriteThrough))
                using (var sha = SHA256.Create())
                { stream.Write(bytes, 0, bytes.Length); byte[] checksum = sha.ComputeHash(bytes); stream.Write(checksum, 0, checksum.Length); stream.Flush(true); }
                if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
