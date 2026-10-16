# SEMC v2 runtime protocol

Local bridge-v2.bin, 8 MiB, little endian. Minecraft is the block simulation
authority. SE owns camera, character and vehicle movement. World mapping is
rigid, one MC block = one SE metre; matrix maps integer block MIN corners.
Backend must run a separate SEMC world, not the user's original save.

MC header, protected by even/odd seqlock at byte8:

| Offset | Type | Field |
|---|---|---|
|0|int|magic 0x32434D53|
|4|int|version2|
|8|int|sequence (odd writer/even complete)|
|12|int|flags:1 world loaded,2 runtime active,4 window hidden|
|16|long|UTC Unix milliseconds|
|24,28,32|int|sizeX,sizeY,sizeZ; currently32 or opt-in64|
|36,40,44|int|origin MC x,y,z|
|48|int|palette revision|
|52|int|record count sizeX*sizeY*sizeZ (32768 or262144)|
|56|int|last command acknowledgement|
|60|int|command result:1 success,0 pending,-1 error|
|64|long|server game tick|
|72,80,88|double|MC player xyz|
|96,100|float|MC player yaw,pitch|
|104|int|record bytes=12|
|108|int|nonair cells|

Records begin4096. index=x+sizeX*(z+sizeZ*y). Each record:
int Minecraft raw blockstate ID (0 for air), int mapRGB, int flags
(1 nonair,2 full collision cube,4 has collision,8 fluid).
palette-v2.json atomically replaced; contains state ID, registryName,
properties, collision boxes (local0..1), block model/texture references.

SE mailbox starts4194304. One outstanding command at a time; host queues
commands locally. Write payload, memory barrier, then positive sequence.
Relative offsets:0 seq,4 op,8 x,12 y,16 z,20 itemIndex,
24 face (DOWN0 UP1 NORTH2 SOUTH3 WEST4 EAST5),28 flags (value1=pose valid),
32,36,40 float hit fractions xyz.48,56,64 double player xyz;
72,76 float yaw,pitch. These pose fields are applied only with flags value1.
Pose payload: +48,+56,+64 doubles player xyz; +72,+76 floats yaw,pitch.
Flags value1 (bit0) means this pose is valid for BREAK/PLACE/INTERACT;
value2 (bit1) requests secondary/sneak use. Op9 always applies the pose.
Coordinates are absolute MC cells; PLACE targets the cell to occupy.
Ops:1 BREAK(native),2 PLACE(native),3 SET_VIEW(origin xyz),4 STOP,
5 INTERACT(native),6 SELECT(itemIndex),7 HIDE(client),8 SHOW(client),
9 SET_PLAYER_POSE,10 PREPARE_CREATIVE,11 SAVE,12 SAVE_AND_QUIT,
13 CLICK_NATIVE_CONTAINER,14 CLOSE_NATIVE_CONTAINER.

Curated item indices (0-based): dirt, stone, oak_planks, oak_log, glass,
cobblestone, bricks, gold_block, redstone_block, redstone_lamp,
redstone_wire/item redstone, lever, tnt, crafting_table, chest,
sticky_piston, piston, observer, white_wool, torch.
Append-only index20: flint_and_steel. INTERACT with itemIndex20 invokes native
flint-and-steel use; other INTERACT commands use empty-hand block interaction.
The MC bridge must return the actual native resulting state after placement.

SE telemetry: mailbox+256 long heartbeat UTCms; +264 int status;
+268 int rendered cells; +272 int mode(enabled1); +276 int pending queue.

Snapshot10Hz target, command polling20Hz target. Both sides log runtime faults.
Neither side interprets missing snapshots as destructive world edits.
Palette/model formats can extend additively once root and agents agree.

Native entity/event sidecars:
- entities-v2.json, updated100ms: {version:2,timestamp,entities:[{uuid,
  type:"minecraft:tnt",x,y,z,fuseTicks,stateId,velocity:[vx,vy,vz]}]}.
- explosion-v2.json, atomically replaced on an actual native TNT explosion:
  {version:2,lastId,events:[{id,timestamp,type:"minecraft:tnt",uuid,dimension,
  x,y,z,power,evidence:"after-native-Level.explode"}]}.
  IDs are monotonic across backend restarts; retain latest256 events. Consumers
  initialize their processed-ID baseline to lastId when opening a new SE
  session, then apply only larger IDs. Never replay history into a newly loaded
  SE world. The hook executes after Level.explode inside PrimedTnt.explode;
  discarded/unloaded entities and TNT_EXPLODES=false emit no explosion event.

Append-only item indices21..28: oak_stairs, oak_slab, stone_bricks, oak_door,
oak_fence, ladder, grass_block, sand. No water-bucket index yet.

Optional streaming is enabled by mc-v2-streaming.flag. Native host pose9 (or
pose-bearing edits) recenters the 32-cube capture in 8-cell steps, with an
8-cell edge margin. Coordinates stay absolute; world blocks are persisted,
never deleted because they leave the current capture. Y clamps to native world
height. Unknown/unloaded chunks export state-1 and are not air/deletion claims.

Terrain support sidecar terrain-v2.json: {version:2,revision,timestamp,
originX,originZ,sizeX:32,sizeZ:32,heights:[absolute MC support top-face Y or null]}.
Index x+32*z. Only the guarded prepared backend accepts it. Four layers of
native barrier blocks provide invisible support for native entities. Ownership
is journaled in terrain-proxy-owned-v2.json; ordinary player blocks are never
replaced or deleted. Owned barriers are omitted from the exported block scene.
Only sampled columns are reconciled; support outside the new window persists.

Ops11 SAVE and12 SAVE_AND_QUIT require the prepared guarded backend.
SAVE flushes native player/world state. SAVE_AND_QUIT acknowledges after native
save and requests Minecraft's normal client exit, which closes the integrated
server and releases session.lock. It never calls forced process termination.

Capture64 is opt-in at agent startup via mc-v2-capture64.flag; otherwise32.
Header dimensions/count are authoritative. 64^3 records fit below the same
mailbox boundary. Streaming uses an edge margin SIZE/4 and 8-cell origin steps.
BREAK can target retained off-window absolute cells when streaming is enabled;
its native chunk is loaded if necessary. Native world bounds still apply.
Terrain sidecar dimensions are independent, bounded to1..64 on each axis.
The isolated managed gameDir is SEMC-Lab/minecraft-backend. Heartbeat loss
there saves and gracefully exits the backend. A backend in the user's normal
gameDir only shows its window for recovery, preserving the original behavior.
host-lifecycle-v2.json identifies the native SE process with version2,processId,
processStartedAt (UTCms). For the managed backend, a15s stale heartbeat only
causes save-and-quit once that process has exited or its start time differs by
over3s, preventing shutdown during a long SE world load. ProcessHandle is used;
no OS UI is inspected. Old hosts without identity retain heartbeat-only fallback.
The last valid identity survives a transient sidecar replacement/read failure.

Native container UI: menu-v2.json is exported every snapshot while prepared.
{version:2,timestamp,sequence,open,containerId,revision,commandAck,type,title,
slotCount,slots:[{index,x,y,itemId,count,maxStack,displayName,itemModelResource,
iconTextureHint,blockStateId?,active,mayPickup,slotMax}],carried:{item descriptor}}.
Empty item descriptor has itemId minecraft:air and count0. Native inventory
container0 exports open=false. Slot coordinates are native screen pixels.
Item model references point to installed26.3 asset definitions; texture is a hint.

Op13 CLICK: X=expectedcontainerId,Y=slotindex,Z=mousebutton(0left/1right),
Item=action(0PICKUP/1QUICK_MOVE). Server verifies current menu ID and stillValid,
then invokes native menu.clicked and broadcastChanges. Outside/drop clicks,
drag distribution and unsupported actions are rejected. Op14 CLOSE uses
X=expectedcontainerId, calls native closeContainer (returning carried items).
Stale IDs are rejected. A host UI should close if timestamps become stale.
SAVE_AND_QUIT closes a native menu before saving and exiting.

Persistent world identity: world-v2.json is published every500ms for the guarded
world, with version2,timestamp,worldId,worldPath,dimension,minecraftVersion,
paletteFingerprint,paletteRevision,saveCheckpoint,savedAt,serverTick.
worldId is a UUID persisted in the save's semc-bridge-world.json. Cache keys use
worldId+dimension+paletteFingerprint, never the JVM PID or paletteRevision.
The SHA256 palette fingerprint covers every native state ID, registry name and
sorted properties, so discovering additional states does not invalidate cache.
palette-v2.json preserves previously exported state IDs after restart only when
its Minecraft version and full fingerprint match; descriptors are regenerated
from native states. saveCheckpoint increments only after explicit SAVE11 or
SAVE_AND_QUIT12 completes native save; ordinary autosaves are not numbered.
savedAt is UTC milliseconds of that explicit checkpoint, or0 before the first.
Terrain ownership is tagged with the persistent worldId.

Native dynamic scene extension (entities-v2.json, every50ms while prepared):
Document adds worldId,dimension,serverTick,sampleIntervalMillis50,paletteRevision.
TNT entries preserve the original schema and add ageTicks and absolute AABB.
entities[] also contains type minecraft:falling_block with uuid,stateId,x,y,z,
ageTicks,fallTicks,startPos,velocity,boundingBox,collisionBoxes. Entity x/z are
native center coordinates; y is its bottom. Velocity is blocks per native tick.
AABB arrays use absolute Minecraft [minX,minY,minZ,maxX,maxY,maxZ].

movingBlocks[] contains native piston block entities within capture+64 cells:
{type:"minecraft:moving_piston",uuid,x,y,z,placeholderStateId,movedStateId,
progress,previousProgress,extending,source,direction,movementDirection,lastTicked,
offset,previousOffset,velocity,collisionBoxes,boundingBox?,renderParts}.
Piston x/y/z is the integer native block-entity cell. UUID is stable for that
world/dimension/cell. direction uses native DOWN0 UP1 NORTH2 SOUTH3 WEST4 EAST5.
Offsets/progress come from native getProgress/getXYZOff at interpolation0/1.
Velocity is current minus previous offset per tick. collisionBoxes comes from
the native block entity's current getCollisionShape, translated to absolute
coordinates; it includes a stationary base during retraction when applicable.
renderParts entries {stateId,x,y,z,moving} use absolute block minimum corners.
Their native state variants follow installed26.3 PistonHeadRenderer rules.
For a moving render part, previous position is current+previousOffset-offset.
For stationary parts it is unchanged. The host must suppress static
minecraft:moving_piston placeholders while using dynamic geometry, then restore
settled native snapshot states. Palette adds dynamicCollision:true for these
placeholder states. Distant unloaded chunks are never loaded by observation.
The host must check world identity/freshness and avoid extrapolating past native
progress1. Exporting this data alone does not implement SE physical pushing.
Piston entries additionally provide collisionPartsVerified and, when verified,
collisionParts:[{moving,velocity,collisionBoxes,previousCollisionBoxes}].
Each part separates a stationary retracting base from its translating block or
head. Previous boxes use native shape variants at previousProgress; all boxes
are absolute. Current union is checked against the direct native collision
shape before these parts are exported. Source-head shape topology may change
between samples; do not blindly pair box indices when counts/extents differ.
