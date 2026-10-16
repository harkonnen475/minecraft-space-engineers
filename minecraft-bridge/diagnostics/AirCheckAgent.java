package semc.diagnostics;

import java.lang.instrument.Instrumentation;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.StandardOpenOption;
import java.time.Instant;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.TimeUnit;
import net.minecraft.client.Minecraft;
import net.minecraft.core.BlockPos;
import net.minecraft.core.registries.BuiltInRegistries;
import net.minecraft.world.level.block.state.BlockState;

/** One-shot, read-only checks on both real game threads. No bytecode changes. */
public final class AirCheckAgent {
    public static void main(String[] args) throws Exception {
        if (args.length != 6) throw new IllegalArgumentException("Usage: AirCheckAgent pid jar root x y z");
        var vm = com.sun.tools.attach.VirtualMachine.attach(args[0]);
        try { vm.loadAgent(Path.of(args[1]).toAbsolutePath().toString(), args[2]+"|"+args[3]+"|"+args[4]+"|"+args[5]); }
        finally { vm.detach(); }
        System.out.println("Read-only air check submitted; inspect mc-air-check.log");
    }
    public static void agentmain(String args, Instrumentation instrumentation) throws Exception {
        String[] pieces = args.split("\\|", -1);
        if (pieces.length != 4) throw new IllegalArgumentException("Expected root|x|y|z");
        Path root = Path.of(pieces[0]);
        BlockPos pos = new BlockPos(Integer.parseInt(pieces[1]), Integer.parseInt(pieces[2]), Integer.parseInt(pieces[3]));
        long started = System.nanoTime();
        CompletableFuture<Void> done = new CompletableFuture<>();
        Minecraft.getInstance().execute(() -> {
            try {
                Minecraft client = Minecraft.getInstance();
                if (client.level == null || client.player == null || client.getSingleplayerServer() == null)
                    throw new IllegalStateException("No integrated player/world");
                if (!client.level.hasChunkAt(pos)) throw new IllegalStateException("Client chunk is not loaded");
                BlockState state = client.level.getBlockState(pos);
                report(root, "CLIENT " + describe(state) + " at=" + pos.getX()+","+pos.getY()+","+pos.getZ()
                        + " paused=" + client.isPaused() + " delayMs=" + (System.nanoTime()-started)/1_000_000);
                var server = client.getSingleplayerServer();
                var uuid = client.player.getUUID();
                long queued = System.nanoTime();
                server.execute(() -> {
                    try {
                        var player = server.getPlayerList().getPlayer(uuid);
                        if (player == null || !player.level().hasChunkAt(pos)) throw new IllegalStateException("Server player/chunk unavailable");
                        BlockState serverState = player.level().getBlockState(pos);
                        report(root, "SERVER " + describe(serverState) + " at=" + pos.getX()+","+pos.getY()+","+pos.getZ()
                                + " paused=" + server.isPaused() + " queueDelayMs=" + (System.nanoTime()-queued)/1_000_000);
                        done.complete(null);
                    } catch (Throwable failure) { done.completeExceptionally(failure); }
                });
            } catch (Throwable failure) { done.completeExceptionally(failure); }
        });
        try { done.get(8, TimeUnit.SECONDS); }
        catch (java.util.concurrent.TimeoutException timeout) { report(root, "SERVER/CLIENT CHECK pending after 8 seconds; queued read may finish after unpausing"); }
    }

    private static String describe(BlockState state) {
        return "block=" + BuiltInRegistries.BLOCK.getKey(state.getBlock()) + " isAir=" + state.isAir()
                + " fluidEmpty=" + state.getFluidState().isEmpty();
    }

    private static synchronized void report(Path root, String text) throws java.io.IOException {
        Files.writeString(root.resolve("mc-air-check.log"), Instant.now() + " " + text + "\n",
                StandardOpenOption.CREATE, StandardOpenOption.APPEND);
    }
}
