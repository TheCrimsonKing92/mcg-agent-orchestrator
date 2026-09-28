using System.Globalization;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorBatchLoop
{
    internal const string RelaunchDrainCapEnvironmentVariable = "MCG_ORCHESTRATOR_RELAUNCH_DRAIN_CAP_MINUTES";
    internal static readonly TimeSpan DefaultRelaunchDrainCap = TimeSpan.FromMinutes(3);

    internal static TimeSpan ResolveRelaunchDrainCap(string? raw) =>
        int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var minutes) && minutes > 0
            ? TimeSpan.FromMinutes(minutes)
            : DefaultRelaunchDrainCap;

    private sealed class SelfRelaunchDrainCapState(TimeSpan cap)
    {
        internal TimeSpan Cap { get; } = cap;
        internal bool DispatchHandlingChanged { get; private set; }
        internal bool Detached { get; set; }

        internal void NoteLanding(IReadOnlyCollection<string> changedFiles) =>
            DispatchHandlingChanged |= RepositoryChangeClassifier.TouchesDispatchResultHandling(changedFiles);

        internal void Reset()
        {
            DispatchHandlingChanged = false;
            Detached = false;
        }

        internal SelfRelaunchCapDecision Evaluate(
            AgentOrchestratorKernel kernel,
            string? onlyGoalId,
            HashSet<string> excludedGoals,
            HashSet<string> reapedGoals,
            TimeSpan elapsed)
        {
            if (elapsed < Cap) return new(false, false, string.Empty, 0);
            if (Detached) return new(false, true, string.Empty, 0);
            if (DispatchHandlingChanged) return new(false, false, " reason=dispatch-handling-changed", 0);

            var count = 0;
            foreach (var goal in kernel.Goals.Where(goal => onlyGoalId is null || goal.Id.Value == onlyGoalId))
            {
                foreach (var process in goal.Tasks.Select(task => task.LastProcess).OfType<TaskProcessRecord>()
                             .Where(process => process.IsRunning && !process.WasGracefullyDetachedByConductor))
                {
                    if (process.ProcessIdentityStartedAt is null ||
                        excludedGoals.Contains(goal.Id.Value) || IsTerminalGoal(goal) ||
                        reapedGoals.Contains(goal.Id.Value))
                        return new(false, false, " reason=identity-unproven", 0);
                    count++;
                }
            }

            return new(true, false, string.Empty, count);
        }
    }

    private readonly record struct SelfRelaunchCapDecision(bool Detach, bool AlreadyDetached, string DrainSuffix, int Count);

    private static TimeSpan GetWatchFallbackInterval(AgentOrchestratorKernel kernel, string? onlyGoalId, TimeSpan idleInterval) =>
        HasRunningDispatch(kernel, onlyGoalId) ? TimeSpan.FromSeconds(WatchStopPollIntervalSeconds) : idleInterval;

    private static bool HasRunningDispatch(AgentOrchestratorKernel kernel, string? onlyGoalId) =>
        kernel.Goals.Any(goal =>
            (onlyGoalId is null || goal.Id.Value == onlyGoalId) &&
            goal.Tasks.Any(task => task.LastProcess is { IsRunning: true }));

    private static int CountRunningDispatches(AgentOrchestratorKernel kernel, string? onlyGoalId) =>
        kernel.Goals
            .Where(goal => onlyGoalId is null || goal.Id.Value == onlyGoalId)
            .Sum(goal => goal.Tasks.Count(task => task.LastProcess is { IsRunning: true }));

    private static IReadOnlyList<string> GetRunningDispatchExitCodePaths(AgentOrchestratorKernel kernel, string? onlyGoalId) =>
        kernel.Goals
            .Where(goal => onlyGoalId is null || goal.Id.Value == onlyGoalId)
            .SelectMany(goal => goal.Tasks)
            .Select(task => task.LastProcess)
            .OfType<TaskProcessRecord>()
            .Where(process => process.IsRunning && !string.IsNullOrWhiteSpace(process.ExitCodePath))
            .Select(process => process.ExitCodePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
}
