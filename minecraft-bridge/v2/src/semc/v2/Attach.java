package semc.v2;
import java.nio.file.Path;
import com.sun.tools.attach.VirtualMachine;
public final class Attach {
    public static void main(String[] args) throws Exception {
        if (args.length != 3) throw new IllegalArgumentException("Usage: Attach pid jar lab-root");
        String jar = Path.of(args[1]).toAbsolutePath().toString();
        var vm = VirtualMachine.attach(args[0]);
        try { vm.loadAgent(jar, Path.of(args[2]).toAbsolutePath() + "|" + jar); }
        finally { vm.detach(); }
        System.out.println("SEMC v2 backend attached to PID " + args[0] + "; world changes remain gated");
    }
}
