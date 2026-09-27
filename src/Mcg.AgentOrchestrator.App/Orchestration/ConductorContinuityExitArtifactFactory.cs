namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class ConductorContinuityExitArtifactFactory
{
    internal static ConductorContinuityExitArtifact FromLoopSummary(
        BatchLoopSummary summary,
        bool delegateSelfRelaunchToSupervisor) =>
        new(
            summary.StopReason ?? "unknown",
            summary.Ticks,
            summary.Done,
            RestartRequested: string.Equals(summary.StopReason, "max-duration", StringComparison.Ordinal) ||
                delegateSelfRelaunchToSupervisor && string.Equals(
                    summary.StopReason, "self-relaunch-handoff", StringComparison.Ordinal),
            LandedGoals: summary.LandedGoals);
}
