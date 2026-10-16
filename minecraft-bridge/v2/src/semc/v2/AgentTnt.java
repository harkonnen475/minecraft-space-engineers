package semc.v2;
import java.lang.instrument.Instrumentation;
/** Fresh bootstrap enables retransformation without replacing already-loaded Agent. */
public final class AgentTnt {
    public static void agentmain(String args,Instrumentation instrumentation) throws Exception {
        TntHooks.install(instrumentation);
        Agent.agentmain(args,instrumentation);
    }
    public static void premain(String args,Instrumentation instrumentation) throws Exception {
        TntHooks.install(instrumentation);
        Agent.premain(args,instrumentation);
    }
}
