using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Captures driver-owned mutation before the loop adds its own disposition telemetry.
internal static class GoalKernelChange
{
    internal static string Capture(Goal goal) =>
        GoalProjectionCache.BuildFingerprint(goal) + "|" + string.Join("|", goal.Tasks
            .OrderBy(task => task.Id.Value, StringComparer.Ordinal)
            .Select(task => string.Join(":", task.Id.Value,
                task.VerificationHistory.Count,
                task.VerificationHistory.Sum(record => record.FindingEvidenceReceipts?.Count ?? 0),
                task.LatestRetryAt?.UtcTicks, task.PendingRetryCause)));

    internal static bool Changed(string before, AgentOrchestratorKernel kernel, GoalId goalId) =>
        kernel.Goals.FirstOrDefault(goal => goal.Id == goalId) is not { } current ||
        !string.Equals(before, Capture(current), StringComparison.Ordinal);
}
