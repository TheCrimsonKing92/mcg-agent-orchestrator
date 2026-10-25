using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorParallelAcceptanceAttemptCoordinator
{
    internal static void ApplyOwnedProcessPriority(
        ConductorParallelAcceptanceAttempt attempt,
        Func<bool> lowerCurrentProcess)
    {
        if (ResolveAttemptPolicy(attempt).AcceptanceAttemptBelowNormalPriority)
        {
            _ = lowerCurrentProcess();
        }
    }

    internal static void ApplyOwnedProcessExecutionQos(
        ConductorParallelAcceptanceAttempt attempt,
        Func<int, uint, uint, bool> setter)
    {
        if (!ResolveAttemptPolicy(attempt).AcceptanceChildrenOptOutExecutionThrottling) return;
        AcceptanceChildQos.Enable();
        try { _ = setter(Environment.ProcessId, 1, 0); }
        catch (Exception) { /* QoS is best-effort and must not change acceptance. */ }
    }
}
