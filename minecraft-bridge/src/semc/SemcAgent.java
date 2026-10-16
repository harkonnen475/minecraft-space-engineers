package semc;

import java.lang.instrument.Instrumentation;
import java.net.URL;
import java.net.URLClassLoader;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.StandardOpenOption;
import java.time.Instant;

/** Bootstrap stays in the system loader; each attachment gets fresh runtime classes. */
public final class SemcAgent {
    private static AutoCloseable current;
    private static URLClassLoader runtimeLoader;

    public static synchronized void agentmain(String args, Instrumentation instrumentation) throws Exception {
        Path root = Path.of(args == null || args.isBlank() ?
                "C:\\Users\\konra\\Documents\\SEMC-Lab" : args).toAbsolutePath();
        Files.createDirectories(root);
        try {
            if (current != null) current.close();
            if (runtimeLoader != null) runtimeLoader.close();
            ClassLoader gameLoader = null;
            for (Class<?> cls : instrumentation.getAllLoadedClasses()) {
                if (cls.getName().equals("net.minecraft.client.Minecraft")) {
                    gameLoader = cls.getClassLoader();
                    break;
                }
            }
            if (gameLoader == null) throw new IllegalStateException("Minecraft client class is not loaded");
            URL jar = SemcAgent.class.getProtectionDomain().getCodeSource().getLocation();
            runtimeLoader = new URLClassLoader(new URL[] { jar }, gameLoader) {
                @Override protected Class<?> loadClass(String name, boolean resolve) throws ClassNotFoundException {
                    synchronized (getClassLoadingLock(name)) {
                        Class<?> cls = findLoadedClass(name);
                        if (cls == null && name.startsWith("semc.runtime.")) cls = findClass(name);
                        if (cls == null) cls = super.loadClass(name, false);
                        if (resolve) resolveClass(cls);
                        return cls;
                    }
                }
            };
            Class<?> bridge = Class.forName("semc.runtime.BridgeRuntime", true, runtimeLoader);
            current = (AutoCloseable) bridge.getConstructor(Path.class).newInstance(root);
            bridge.getMethod("start").invoke(current);
        } catch (Throwable failure) {
            Files.writeString(root.resolve("mc-bridge.log"), Instant.now() + " ATTACH FAILED " + failure + "\n",
                    StandardOpenOption.CREATE, StandardOpenOption.APPEND);
            throw failure;
        }
    }
}
