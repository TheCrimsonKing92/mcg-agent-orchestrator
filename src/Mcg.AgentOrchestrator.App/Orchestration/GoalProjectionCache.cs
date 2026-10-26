using Mcg.AgentOrchestrator.Core;
namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed class GoalProjectionCache
{
    private readonly Dictionary<GoalId, GoalProjectionCacheEntry> _entries = [];

    internal int Count => _entries.Count;

    internal GoalLifecycleState ResolveState(Goal goal, ConductorDriver driver)
    {
        var fingerprint = BuildFingerprint(goal);
        if (_entries.TryGetValue(goal.Id, out var entry)
            && string.Equals(entry.Fingerprint, fingerprint, StringComparison.Ordinal))
        {
            return entry.State;
        }

        var state = GoalLifecycle.ResolveState(goal, driver.GetFacts(goal));
        _entries[goal.Id] = new GoalProjectionCacheEntry(fingerprint, state);
        return state;
    }

    internal void Invalidate(GoalId goalId) => _entries.Remove(goalId);

    internal static string BuildFingerprint(Goal goal)
    {
        var taskParts = goal.Tasks
            .OrderBy(task => task.Id.Value, StringComparer.Ordinal)
            .Select(task =>
                string.Join(
                    ":",
                    task.Id.Value,
                    task.Status.ToString(),
                    task.LastDispatch is null ? "dispatch=none" : $"dispatch={task.LastDispatch.DispatchedAt.UtcTicks}:{task.LastDispatch.WorkerName}",
                    task.LastProcess is null ? "process=none" : $"process={task.LastProcess.IsRunning}:{task.LastProcess.CompletedAt?.UtcTicks}:{task.LastProcess.ExitCode}:{task.LastProcess.WasCancelled}",
                    task.LastVerification is null ? "verification=none" : $"verification={task.LastVerification.Succeeded}:{task.LastVerification.ExitCode}:{task.LastVerification.CompletedAt.UtcTicks}",
                    task.LastExecution is null ? "execution=none" : $"execution={task.LastExecution.StopReason}:{task.LastExecution.CompletedAt.UtcTicks}"));

        return string.Join("|", new[] { goal.Status.ToString(), $"timeline={goal.Timeline.Count}" }.Concat(taskParts));
    }
}

internal sealed record GoalProjectionCacheEntry(string Fingerprint, GoalLifecycleState State);
