using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorBatchLoop
{
    private void EmitWatchProgress(
        IReadOnlyList<Goal> eligible,
        bool quiet,
        ConductorAutonomyPolicy policy,
        TimeSpan? watchInterval,
        TimeSpan? stallWarningThreshold,
        IReadOnlyDictionary<(string Worktree, string? BaseCommit), DispatchLiveChangeSnapshot> liveChangeSnapshots,
        int tick,
        List<string> tickLines)
    {
        foreach (var goal in eligible)
        {
            IReadOnlyList<string> lines;
            try
            {
                var activeTask = ConductorWatchProgressReporter.GetActiveTask(goal);
                var cachedLiveChanges = activeTask?.LastDispatch is { } dispatch &&
                    liveChangeSnapshots.TryGetValue((dispatch.WorkingDirectory, dispatch.BaseCommit), out var snapshot)
                        ? snapshot
                        : null;
                lines = _watchProgressReporter.BuildLines(goal, quiet, policy, watchInterval,
                    stallWarningThreshold, cachedLiveChanges);
            }
            catch (Exception exception)
            {
                EmitProgress($"WATCH_PROGRESS_FAILED tick={tick} goal={ShortGoalId(goal.Id.Value)} " +
                    $"exception={Sanitize(exception.GetType().Name)} message={SanitizeReason(exception.Message)}", tickLines);
                continue;
            }

            foreach (var line in lines)
                EmitProgress(line, tickLines);
        }
    }
}
