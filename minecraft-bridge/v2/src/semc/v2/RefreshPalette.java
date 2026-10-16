package semc.v2;
import java.lang.instrument.Instrumentation;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.StandardOpenOption;
import net.minecraft.client.Minecraft;
/** Maintenance attach: publishes a new palette revision without restarting simulation. */
public final class RefreshPalette {
    public static void agentmain(String arguments,Instrumentation instrumentation)throws Exception {
        Path root=Path.of(arguments.split("\\|",2)[0]);
        var field=Agent.class.getDeclaredField("runtime");field.setAccessible(true);
        Object runtime=field.get(null);if(runtime==null)throw new IllegalStateException("No active backend");
        var dirty=runtime.getClass().getDeclaredField("paletteDirty");dirty.setAccessible(true);
        var server=Minecraft.getInstance().getSingleplayerServer();if(server==null)throw new IllegalStateException("No native server");
        server.execute(()->{try{
            dirty.setBoolean(runtime,true);
            Files.writeString(root.resolve("mc-v2.log"),java.time.Instant.now()+" MAINTENANCE palette revision refresh requested\n",StandardOpenOption.CREATE,StandardOpenOption.APPEND);
        }catch(Exception failure){throw new RuntimeException(failure);}});
    }
}
