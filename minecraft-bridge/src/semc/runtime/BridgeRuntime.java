package semc.runtime;

import java.lang.invoke.MethodHandles;
import java.lang.invoke.VarHandle;
import java.nio.ByteOrder;
import java.nio.MappedByteBuffer;
import java.nio.channels.FileChannel;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.StandardOpenOption;
import java.time.Instant;
import java.util.UUID;
import java.util.concurrent.Executors;
import java.util.concurrent.ScheduledExecutorService;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicBoolean;
import net.minecraft.client.Minecraft;
import net.minecraft.client.multiplayer.ClientLevel;
import net.minecraft.core.BlockPos;
import net.minecraft.server.level.ServerPlayer;
import net.minecraft.world.level.block.Block;
import net.minecraft.world.level.block.Blocks;
import net.minecraft.world.level.block.state.BlockState;

/** No bytecode transformation. Client-thread reads, server-thread edits, mapped-file IPC. */
public final class BridgeRuntime implements AutoCloseable {
    private static final int SX = 12, SY = 8, SZ = 12, COUNT = SX * SY * SZ;
    private static final int MAILBOX = 32768;
    private static final VarHandle INTS = MethodHandles.byteBufferViewVarHandle(int[].class, ByteOrder.LITTLE_ENDIAN);
    private final Path root;
    private final FileChannel channel;
    private final MappedByteBuffer memory;
    private final ScheduledExecutorService timer;
    private final AtomicBoolean queued = new AtomicBoolean();
    private volatile boolean running = true;
    private volatile int acknowledgement, commandResult;
    private int sequence, lastCommand, originX, originY, originZ;
    private boolean hasOrigin, loggedFirst;
    private ClientLevel captureLevel;
    private long lastErrorLog;

    public BridgeRuntime(Path root) throws Exception {
        this.root = root;
        channel = FileChannel.open(root.resolve("bridge.bin"), StandardOpenOption.READ,
                StandardOpenOption.WRITE, StandardOpenOption.CREATE);
        if (channel.size() < 65536) {
            channel.position(65535);
            channel.write(java.nio.ByteBuffer.wrap(new byte[] {0}));
        }
        memory = channel.map(FileChannel.MapMode.READ_WRITE, 0, 65536);
        memory.order(ByteOrder.LITTLE_ENDIAN);
        sequence = memory.getInt(8) & ~1;
        acknowledgement = memory.getInt(84);
        lastCommand = acknowledgement;
        commandResult = memory.getInt(88);
        timer = Executors.newSingleThreadScheduledExecutor(r -> {
            Thread thread = new Thread(r, "SEMC telemetry scheduler");
            thread.setDaemon(true);
            return thread;
        });
    }

    public void start() {
        log("Bridge attached; 12x8x12 capture at 5 Hz; edits require enable-edits.flag; stop with mc-stop.flag or command 4");
        timer.scheduleAtFixedRate(() -> {
            if (!running) return;
            if (Files.exists(root.resolve("mc-stop.flag"))) { close(); return; }
            if (!queued.compareAndSet(false, true)) return;
            try {
                Minecraft.getInstance().execute(() -> {
                    try { if (running) sample(); }
                    catch (Throwable failure) { error(failure); }
                    finally { queued.set(false); }
                });
            } catch (Throwable failure) { queued.set(false); error(failure); }
        }, 0, 200, TimeUnit.MILLISECONDS);
    }

    private void sample() {
        Minecraft client = Minecraft.getInstance();
        boolean active = client.level != null && client.player != null && client.getSingleplayerServer() != null;
        if (active && client.level != captureLevel) {
            captureLevel = client.level;
            hasOrigin = false;
        }
        if (active && !hasOrigin) recenter(client);
        boolean stop = command(client, active);
        int[] records = new int[COUNT * 2];
        int solidCount = 0;
        if (active) {
            for (int y = 0; y < SY; y++) for (int z = 0; z < SZ; z++) for (int x = 0; x < SX; x++) {
                BlockPos pos = new BlockPos(originX + x, originY + y, originZ + z);
                if (!client.level.hasChunkAt(pos)) continue;
                BlockState state = client.level.getBlockState(pos);
                if (state.isAir() || !state.isSolid() || !state.getFluidState().isEmpty()
                        || !state.isCollisionShapeFullBlock(client.level, pos)) continue;
                int index = 2 * (x + SX * (z + SZ * y));
                records[index] = Block.getId(state);
                int color = state.getMapColor(client.level, pos).col;
                records[index + 1] = color == 0 ? 0x888888 : color & 0xffffff;
                solidCount++;
            }
        }
        int odd = sequence + 1;
        INTS.setVolatile(memory, 8, odd);
        VarHandle.fullFence();
        memory.putInt(0, 0x434D4553);
        memory.putInt(4, 1);
        memory.putInt(12, SX); memory.putInt(16, SY); memory.putInt(20, SZ);
        memory.putInt(24, originX); memory.putInt(28, originY); memory.putInt(32, originZ);
        memory.putInt(36, active && !stop ? 1 : 0);
        memory.putLong(40, System.currentTimeMillis());
        if (active) {
            memory.putDouble(48, client.player.getX()); memory.putDouble(56, client.player.getY()); memory.putDouble(64, client.player.getZ());
            memory.putFloat(72, client.player.getYRot()); memory.putFloat(76, client.player.getXRot());
        }
        memory.putInt(80, COUNT);
        memory.putInt(84, acknowledgement); memory.putInt(88, commandResult);
        for (int i = 0; i < records.length; i++) memory.putInt(128 + i * 4, records[i]);
        sequence += 2;
        INTS.setRelease(memory, 8, sequence);
        if (active && !loggedFirst) {
            loggedFirst = true;
            log("FIRST SNAPSHOT origin=" + originX + "," + originY + "," + originZ + " solid=" + solidCount
                    + " player=" + client.player.getX() + "," + client.player.getY() + "," + client.player.getZ()
                    + " paused=" + client.isPaused() + " edits=" + Files.exists(root.resolve("enable-edits.flag")));
        }
        if (stop) close();
    }

    private void recenter(Minecraft client) {
        originX = (int) Math.floor(client.player.getX()) - SX / 2;
        originY = (int) Math.floor(client.player.getY()) - 2;
        originZ = (int) Math.floor(client.player.getZ()) - SZ / 2;
        hasOrigin = true;
        log("Capture origin=" + originX + "," + originY + "," + originZ);
    }

    private boolean command(Minecraft client, boolean active) {
        int next = (int) INTS.getAcquire(memory, MAILBOX);
        if (next <= 0 || next <= lastCommand) return false;
        int operation = memory.getInt(MAILBOX + 4);
        int x = memory.getInt(MAILBOX + 8), y = memory.getInt(MAILBOX + 12), z = memory.getInt(MAILBOX + 16);
        VarHandle.acquireFence();
        if (next != (int) INTS.getAcquire(memory, MAILBOX)) return false;
        lastCommand = next;
        if (operation == 4) { ack(next, 1, "stop"); return true; }
        if (operation == 3) {
            if (active) { recenter(client); ack(next, 1, "recenter"); }
            else ack(next, -1, "no integrated world");
            return false;
        }
        if (operation != 1 && operation != 2) { ack(next, -1, "unknown operation"); return false; }
        if (!active || !hasOrigin || !Files.exists(root.resolve("enable-edits.flag"))) {
            ack(next, -1, "edits disabled or no integrated world"); return false;
        }
        if (!inside(x, y, z)) { ack(next, -1, "outside capture"); return false; }
        var server = client.getSingleplayerServer();
        var dimension = client.level.dimension();
        UUID playerId = client.player.getUUID();
        server.execute(() -> {
            try {
                if (!running || !Files.exists(root.resolve("enable-edits.flag"))) {
                    ack(next, -1, "bridge stopped or edits disabled"); return;
                }
                ServerPlayer player = server.getPlayerList().getPlayer(playerId);
                if (player == null || !server.isSingleplayer() || player.level().dimension() != dimension) {
                    ack(next, -1, "integrated player/world changed"); return;
                }
                BlockPos pos = new BlockPos(x, y, z);
                if (!player.level().hasChunkAt(pos)) { ack(next, -1, "chunk unavailable"); return; }
                BlockState replacement = operation == 1 ? Blocks.AIR.defaultBlockState() : Blocks.GOLD_BLOCK.defaultBlockState();
                boolean changed = player.level().setBlock(pos, replacement, 3, 512);
                boolean matches = player.level().getBlockState(pos).equals(replacement);
                ack(next, changed || matches ? 1 : -1, "operation=" + operation + " at=" + x + "," + y + "," + z);
            } catch (Throwable failure) { ack(next, -1, failure.toString()); error(failure); }
        });
        return false;
    }

    private boolean inside(int x, int y, int z) {
        return x >= originX && x < originX + SX && y >= originY && y < originY + SY && z >= originZ && z < originZ + SZ;
    }

    private void ack(int command, int result, String detail) {
        commandResult = result;
        acknowledgement = command;
        log("Command " + command + " result=" + result + " " + detail);
    }

    private void error(Throwable failure) {
        long now = System.currentTimeMillis();
        if (now - lastErrorLog > 5000) { lastErrorLog = now; log("ERROR " + failure); }
    }

    private synchronized void log(String message) {
        try { Files.writeString(root.resolve("mc-bridge.log"), Instant.now() + " " + message + "\n",
                StandardOpenOption.CREATE, StandardOpenOption.APPEND); }
        catch (Exception ignored) { }
    }

    @Override public void close() {
        if (!running) return;
        running = false;
        timer.shutdownNow();
        try { channel.close(); } catch (Exception ignored) { }
        log("Bridge stopped; no game code transformed");
    }
}
