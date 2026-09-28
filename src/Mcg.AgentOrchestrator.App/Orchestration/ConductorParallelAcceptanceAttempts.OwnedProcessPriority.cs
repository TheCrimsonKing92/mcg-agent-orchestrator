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
}
