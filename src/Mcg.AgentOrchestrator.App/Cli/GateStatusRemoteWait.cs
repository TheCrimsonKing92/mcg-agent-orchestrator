using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class GateStatusRemoteWait
{
    internal static void PrintText(OrchestratorWorkspace workspace)
    {
        var observedAt = DateTimeOffset.UtcNow;
        GateStatusTextView.Print(GateHeartbeatArtifacts.ReadStableSlots(observedAt), observedAt,
            Count(workspace, observedAt));
    }

    internal static int Count(OrchestratorWorkspace? workspace, DateTimeOffset observedAt)
    {
        if (workspace is null) return 0;
        return ConductorAttemptHeartbeatScanner.CountLive(
            Path.Combine(workspace.OrchestratorDirectory, "acceptance-gate-attempts"), observedAt,
            ConductorInvariantThresholds.GateIdleWhileReady);
    }
}
