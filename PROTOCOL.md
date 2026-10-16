# SEMC Lab bridge protocol v1

Prototype: live small block patch, not a full renderer or unified game.
Shared file: C:\Users\konra\Documents\SEMC-Lab\bridge.bin
Length: 65536 bytes. Little endian. File-backed shared memory, no force/fsync per frame.

Minecraft owns bytes 0..32767. Publish snapshot with sequence at offset 8: odd during writing, even after. Reader accepts only equal even sequence before/after copy. Header:
- 0 int magic 0x434D4553 (bytes SEMC)
- 4 int version 1
- 8 int snapshot sequence
- 12 int sizeX = 12
- 16 int sizeY = 8
- 20 int sizeZ = 12
- 24 int originX in Minecraft world
- 28 int originY
- 32 int originZ
- 36 int flags: bit0 world active
- 40 long Unix timestamp milliseconds
- 48,56,64 double player x,y,z
- 72,76 float yaw,pitch
- 80 int block count = 1152
- 84 int command acknowledgement sequence
- 88 int command result (0 none,1 success,-1 error)
- 128 block records, 8 bytes each: int stateId (0 means air; other ids only identity), int RGB 0xRRGGBB. Index x + sizeX*(z + sizeZ*y).

Space Engineers owns bytes 32768..65535. Command mailbox (at most one outstanding):
- 32768 int command sequence: write parameters first then sequence last, monotonic positive
- 32772 int operation: 1 remove block, 2 set demo gold block, 3 recenter capture near MC player, 4 stop MC bridge
- 32776,32780,32784 int x,y,z (absolute MC coords)
- 32792 long SE heartbeat Unix ms
- 32800 int SE mirrored block count
- 32804 int SE status (0 idle,1 connected,2 world loaded)

MC defaults to READ-ONLY exporting unless enable-edits.flag exists in SEMC-Lab. Destructive operations 1/2 require this flag and run on the integrated server thread; only coordinates within the current capture are allowed. Command 3 refreshes origin around player's feet (x-6,y-2,z-6), remains fixed until next command. Dimensions and palette layout immutable in v1. Initial origin is near first available player. Rescan ~5 Hz. Avoid liquids/foliage in physical mirror if state not solid (export as air for first slice).

SE mirror: one native small-grid armor block per solid MC block, scale 0.5m for initial proof. Place a fixed static grid in front of the local player once. Use color from snapshot. Keep Save=false; no persisted bridge entities. World modifications only in locally loaded singleplayer session. SE controls mirror existence; MC is authority on block states. Never infer player deletion from loading/unloading or bridge-applied updates. A selected explicit mirror test operation is preferable to automatic destructive callbacks until verified.
