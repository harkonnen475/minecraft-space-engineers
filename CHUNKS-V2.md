Remote retained-chunk refresh, protocol2

Minecraft is the sole writer of chunks-v2.bin; Space Engineers is the sole
writer of retained-chunks-v2.json. These files are separate from bridge-v2.bin.
Native refresh is read-only and never loads or generates a missing chunk.

Little-endian binary length:4096+256*8192=2,101,248 bytes.
Header offsets:
0 int magic0x324B4353;4 int version2;8 int seqlock;
12 int capacity256;16 int slotBytes8192;20 int chunkEdge8;
24 int cellRecordBytes12;28 int headerBytes4096;
32 long latestSequence;40 long writerEpoch;48 long timestampUTCms.
64 worldId UTF8[40];104 dimension UTF8[64];168 paletteFingerprint UTF8[64].
Strings are zero padded; the64-character SHA256 fills its entire field.

Slot offset=4096+((sequence-1)%256)*8192. Each slot:
0 int seqlock;4 reserved;8 long sequence;16 long timestampUTCms;
24/28/32 int absolute originXYZ;36 int count512;40 int paletteRevision;
44 int flags1 (complete sampled cube);48 long native serverTick;
56 long writerEpoch;128 onward512 cell records, each int stateId,mapRGB,flags.
Index=x+8*(z+8*y), matching the main snapshot layout. Cell state0 is known air.
Cell state-1 is unavailable and preserves prior host data for that cell.
Owned terrain barriers are exported as air, matching the main snapshot.

Each seqlock is odd during writes and even after release publication. Read a
matching even lock before/after copying. The slot commits before header latest
sequence advances. Require exact requested slot sequence and matching epoch.
Writer epoch changes on backend reattachment or world/dimension change. Bind
the header to worldId+dimension+paletteFingerprint from world-v2.json.
On ring overrun the host increments interests revision to request a full resend.
New native palette descriptors are committed before any referencing slot.

retained-chunks-v2.json is atomically replaced with:
{version:2,worldId,dimension,paletteFingerprint,revision,origins:[[x,y,z],...]}
Revision is a signed64-bit integer; a new subscriber must use a new revision.
At most8192 origins, each aligned to8 and inside native world bounds. Native
reader checks it every500ms. Revision change forces every interest to resend.
The native server samples up to4 cubes per50ms, round robin, with priority for
directly edited cells and neighboring cube edges. Unchanged samples emit no
slots. Header timestamp still advances at least every500ms while prepared.

Refresh does not make distant Minecraft chunks tick. Redstone outside native
simulation distance retains vanilla inactive behavior. Moving-piston dynamic
block-entity collision shapes are not carried by this static state transport.
Sampled remote cubes may be older than the active main snapshot; the host
discards overlapping remote samples where its main capture is authoritative.
