package semc;

import com.sun.tools.attach.VirtualMachine;
import java.nio.file.Path;

public final class AttachMain {
    public static void main(String[] args) throws Exception {
        if (args.length != 3) throw new IllegalArgumentException("Usage: AttachMain <pid> <agent.jar> <lab-directory>");
        String jar = Path.of(args[1]).toAbsolutePath().toString();
        String root = Path.of(args[2]).toAbsolutePath().toString();
        VirtualMachine vm = VirtualMachine.attach(args[0]);
        try { vm.loadAgent(jar, root); }
        finally { vm.detach(); }
        System.out.println("Attached SEMC read-only-by-default bridge to PID " + args[0]);
    }
}
