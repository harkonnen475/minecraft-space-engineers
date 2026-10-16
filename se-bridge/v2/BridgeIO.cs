using System;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Threading;
using System.Diagnostics;
using System.Web.Script.Serialization;

namespace SEMC.V2
{
    public sealed class Snapshot
    {
        public int Sequence, Flags, SizeX, SizeY, SizeZ, X, Y, Z, PaletteRevision, Count, Ack, Result;
        public long Timestamp, Tick;
        public int[] States, Colors, CellFlags;
        public ulong ContentHash;
        public bool Active { get { return (Flags & 3) == 3; } }
    }

    public sealed class BridgeCommand
    {
        public int Operation, X, Y, Z, Item, Face = 1, Flags;
        public float HitX = .5f, HitY = .5f, HitZ = .5f;
        public double PlayerX, PlayerY, PlayerZ;
        public float Yaw, Pitch;
        public string Description;
    }

    public sealed class BridgeIO : IDisposable
    {
        public const int Mailbox = 4194304, Length = 8388608;
        readonly string root;
        readonly Action<string> log;
        readonly Queue<BridgeCommand> commands = new Queue<BridgeCommand>();
        readonly Mutex mailboxMutex = new Mutex(false, @"Local\SEMC-V2-Mailbox");
        readonly byte[] bytes = new byte[4194304];
        FileStream stream;
        MemoryMappedFile mapping;
        MemoryMappedViewAccessor view;
        int sequence, inFlight, lastRead = -1;
        long sentAt;
        BridgeCommand pending;
        public Snapshot Current { get; private set; }
        public event Action<BridgeCommand, int> Acknowledged;
        public string LastResult { get; private set; }
        public int PendingCount { get { return commands.Count + (pending == null ? 0 : 1); } }
        public bool Connected { get { long now = Now(); return Current != null && Current.Active && Current.Timestamp > 0 && Current.Timestamp <= now + 1000 && now - Current.Timestamp < 3000; } }

        public BridgeIO(string root, Action<string> log)
        {
            this.root = root; this.log = log;
            using (var process = Process.GetCurrentProcess())
            {
                var identity = new Dictionary<string, object> {
                    {"version", 2}, {"processId", process.Id},
                    {"processStartedAt", new DateTimeOffset(process.StartTime.ToUniversalTime()).ToUnixTimeMilliseconds()}
                };
                string path = Path.Combine(root, "host-lifecycle-v2.json"), temporary = path + ".tmp";
                File.WriteAllText(temporary, new JavaScriptSerializer().Serialize(identity));
                if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
            }
        }

        public bool Read()
        {
            if (view == null)
            {
                string file = Path.Combine(root, "bridge-v2.bin");
                if (!File.Exists(file)) return false;
                stream = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
                if (stream.Length != Length) { stream.Dispose(); stream = null; return false; }
                mapping = MemoryMappedFile.CreateFromFile(stream, null, Length, MemoryMappedFileAccess.ReadWrite, null, HandleInheritability.None, true);
                view = mapping.CreateViewAccessor();
                sequence = Math.Max(view.ReadInt32(Mailbox), view.ReadInt32(56));
                log("MMF v2 connected");
            }
            int before = view.ReadInt32(8);
            if ((before & 1) != 0 || before == lastRead) return false;
            Thread.MemoryBarrier();
            view.ReadArray(0, bytes, 0, 4096);
            int count = BitConverter.ToInt32(bytes, 52), stride = BitConverter.ToInt32(bytes, 104);
            if (BitConverter.ToInt32(bytes, 0) != 0x32434D53 || BitConverter.ToInt32(bytes, 4) != 2 ||
                count < 1 || count > 300000 || stride != 12) return false;
            view.ReadArray(4096, bytes, 4096, count * 12);
            Thread.MemoryBarrier();
            if (before != view.ReadInt32(8)) return false;
            var data = new Snapshot {
                Sequence = before, Flags = BitConverter.ToInt32(bytes, 12), Timestamp = BitConverter.ToInt64(bytes, 16),
                SizeX = BitConverter.ToInt32(bytes, 24), SizeY = BitConverter.ToInt32(bytes, 28), SizeZ = BitConverter.ToInt32(bytes, 32),
                X = BitConverter.ToInt32(bytes, 36), Y = BitConverter.ToInt32(bytes, 40), Z = BitConverter.ToInt32(bytes, 44),
                PaletteRevision = BitConverter.ToInt32(bytes, 48), Count = count,
                Ack = BitConverter.ToInt32(bytes, 56), Result = BitConverter.ToInt32(bytes, 60), Tick = BitConverter.ToInt64(bytes, 64)
            };
            if ((long)data.SizeX * data.SizeY * data.SizeZ != count) return false;
            data.States = new int[count]; data.Colors = new int[count]; data.CellFlags = new int[count];
            ulong hash = 14695981039346656037UL;
            for (int i = 0; i < count; i++)
            {
                int offset = 4096 + i * 12;
                data.States[i] = BitConverter.ToInt32(bytes, offset);
                data.Colors[i] = BitConverter.ToInt32(bytes, offset + 4);
                data.CellFlags[i] = BitConverter.ToInt32(bytes, offset + 8);
                unchecked { hash = (hash ^ (uint)data.States[i]) * 1099511628211UL; hash = (hash ^ (uint)data.Colors[i]) * 1099511628211UL; hash = (hash ^ (uint)data.CellFlags[i]) * 1099511628211UL; }
            }
            data.ContentHash = hash;
            Current = data; lastRead = before;
            return true;
        }

        public bool Enqueue(BridgeCommand command)
        {
            if (!Connected || commands.Count >= 128) return false;
            if (command.Operation == 1)
            {
                if (pending != null && SameCell(pending, command)) return true;
                foreach (var c in commands) if (SameCell(c, command)) return true;
            }
            commands.Enqueue(command); return true;
        }
        static bool SameCell(BridgeCommand a, BridgeCommand b) { return a.Operation == b.Operation && a.X == b.X && a.Y == b.Y && a.Z == b.Z; }

        public void Pump(bool enabled, int rendered)
        {
            if (view == null) return;
            long now = Now();
            view.Write(Mailbox + 256, now);
            view.Write(Mailbox + 264, Connected ? 2 : 0);
            view.Write(Mailbox + 268, rendered);
            view.Write(Mailbox + 272, enabled ? 1 : 0);
            view.Write(Mailbox + 276, PendingCount);
            if (pending != null)
            {
                int ack = view.ReadInt32(56);
                if (ack == inFlight)
                {
                    int result = view.ReadInt32(60);
                    LastResult = result == 1 ? "" : "Minecraft nie wykonał tej czynności";
                    if (pending.Operation != 9 || result != 1) log("ACK seq=" + ack + " op=" + pending.Operation + " result=" + result + " cell=" + pending.X + "," + pending.Y + "," + pending.Z);
                    if (Acknowledged != null) Acknowledged(pending, result);
                    pending = null;
                }
                else if (now - sentAt > 5000)
                {
                    LastResult = "Brak odpowiedzi Minecrafta"; log("COMMAND timeout seq=" + inFlight);
                    pending = null; commands.Clear(); return;
                }
                else return;
            }
            if (!Connected || commands.Count == 0 || view.ReadInt32(Mailbox) != view.ReadInt32(56)) return;
            bool acquired = false;
            try { acquired = mailboxMutex.WaitOne(0); } catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) return;
            try
            {
            if (view.ReadInt32(Mailbox) != view.ReadInt32(56)) return;
            pending = commands.Dequeue();
            view.Write(Mailbox + 4, pending.Operation); view.Write(Mailbox + 8, pending.X); view.Write(Mailbox + 12, pending.Y); view.Write(Mailbox + 16, pending.Z);
            view.Write(Mailbox + 20, pending.Item); view.Write(Mailbox + 24, pending.Face); view.Write(Mailbox + 28, pending.Flags);
            view.Write(Mailbox + 32, pending.HitX); view.Write(Mailbox + 36, pending.HitY); view.Write(Mailbox + 40, pending.HitZ);
            view.Write(Mailbox + 48, pending.PlayerX); view.Write(Mailbox + 56, pending.PlayerY); view.Write(Mailbox + 64, pending.PlayerZ);
            view.Write(Mailbox + 72, pending.Yaw); view.Write(Mailbox + 76, pending.Pitch);
            Thread.MemoryBarrier();
            sequence = Math.Max(sequence, Math.Max(view.ReadInt32(Mailbox), view.ReadInt32(56)));
            inFlight = ++sequence; view.Write(Mailbox, inFlight); sentAt = now;
            }
            finally { mailboxMutex.ReleaseMutex(); }
        }

        public void ClearCommands() { commands.Clear(); pending = null; LastResult = null; }
        public string TakeNotice() { string value = LastResult; LastResult = null; return value; }
        public void Dispose()
        {
            if (view != null) { view.Write(Mailbox + 264, 0); view.Dispose(); }
            if (mapping != null) mapping.Dispose(); if (stream != null) stream.Dispose();
            view = null; mapping = null; stream = null;
            mailboxMutex.Dispose();
        }
        public static long Now() { return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); }
        public static string ReadSharedText(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream)) return reader.ReadToEnd();
        }
    }
}
