using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Captures kernel mutation before the loop adds its own disposition telemetry.
internal static class GoalKernelChange
{
    internal static IReadOnlyDictionary<GoalId, string> CaptureAll(AgentOrchestratorKernel kernel) =>
        kernel.Goals.ToDictionary(goal => goal.Id, Capture);

    internal static IEnumerable<GoalId> ChangedSince(
        AgentOrchestratorKernel kernel,
        IReadOnlyDictionary<GoalId, string> baseline,
        IEnumerable<string> checkpointHeldGoalIds,
        IReadOnlyCollection<GoalId> persistedSweepGoalIds)
    {
        var held = checkpointHeldGoalIds.ToHashSet(StringComparer.Ordinal);
        return kernel.Goals
            .Where(goal => !held.Contains(goal.Id.Value) && !persistedSweepGoalIds.Contains(goal.Id) &&
                baseline.TryGetValue(goal.Id, out var before) &&
                !string.Equals(before, Capture(goal), StringComparison.Ordinal))
            .Select(goal => goal.Id);
    }

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
