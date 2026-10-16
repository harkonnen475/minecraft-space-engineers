package semc.v2;

import java.lang.instrument.Instrumentation;
import java.net.URL;
import java.net.URLClassLoader;
import java.nio.file.Path;

/** V2 uses a separate bootstrap from the original laboratory bridge. */
public final class Agent {
    private static AutoCloseable runtime;
    private static URLClassLoader loader;
    public static void premain(String args, Instrumentation instrumentation) {
        Thread bootstrap = new Thread(() -> {
            for (int attempt=0;attempt<1200;attempt++) {
                try {
                    for (Class<?> type:instrumentation.getAllLoadedClasses()) {
                        if (type.getName().equals("net.minecraft.client.Minecraft")) {
                            Object client=type.getMethod("getInstance").invoke(null);
                            if(client==null||type.getField("gui").get(client)==null||type.getMethod("getWindow").invoke(client)==null)continue;
                            agentmain(args,instrumentation);
                            return;
                        }
                    }
                    Thread.sleep(500);
                } catch (Throwable failure) {
                    try { Thread.sleep(500); } catch (InterruptedException interrupted) { return; }
                }
            }
        },"SEMC v2 startup bootstrap");
        bootstrap.setDaemon(true);
        bootstrap.start();
    }
    public static synchronized void agentmain(String args, Instrumentation instrumentation) throws Exception {
        if (runtime != null) runtime.close();
        if (loader != null) loader.close();
        ClassLoader game = null;
        for (Class<?> type : instrumentation.getAllLoadedClasses())
            if (type.getName().equals("net.minecraft.client.Minecraft")) { game = type.getClassLoader(); break; }
        if (game == null) throw new IllegalStateException("Minecraft is not initialized");
        // Optional second argument selects a newly built runtime JAR on reattachment.
        // The bootstrap itself still requires a client restart when changed.
        String[] fields = args.split("\\|", 2);
        Path root = Path.of(fields[0]);
        URL code = fields.length == 2 ? Path.of(fields[1]).toUri().toURL() : Agent.class.getProtectionDomain().getCodeSource().getLocation();
        loader = new URLClassLoader(new URL[] { code }, game) {
            @Override protected Class<?> loadClass(String name, boolean resolve) throws ClassNotFoundException {
                synchronized (getClassLoadingLock(name)) {
                    Class<?> type = findLoadedClass(name);
                    if (type == null && name.startsWith("semc.v2.runtime.")) type = findClass(name);
                    if (type == null) type = super.loadClass(name, false);
                    if (resolve) resolveClass(type);
                    return type;
                }
            }
        };
        Class<?> type = Class.forName("semc.v2.runtime.Backend", true, loader);
        runtime = (AutoCloseable) type.getConstructor(Path.class).newInstance(root);
        type.getMethod("start").invoke(runtime);
    }
}
