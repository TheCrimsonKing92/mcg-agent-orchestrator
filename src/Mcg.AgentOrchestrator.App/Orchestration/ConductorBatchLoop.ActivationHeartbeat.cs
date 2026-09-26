namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorBatchLoop
{
    // Call only after the tick body completes and before any watch or idle sleep.
    private static void EmitActivationTickEnd(bool emitActivationHeartbeat, int tick)
    {
        if (emitActivationHeartbeat)
            EmitProgress($"TICK_END tick={tick} activation=true");
    }
}
