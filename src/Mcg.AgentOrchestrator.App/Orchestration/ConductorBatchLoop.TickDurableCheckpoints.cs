using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorBatchLoop
{
    private readonly HashSet<GoalId> _durableThisTick = [];

    private Func<AgentOrchestratorKernel, IReadOnlyCollection<GoalId>, IReadOnlyList<GoalSnapshotCheckpointResult>>?
        TrackTickDurableCheckpoints(
            Func<AgentOrchestratorKernel, IReadOnlyCollection<GoalId>, IReadOnlyList<GoalSnapshotCheckpointResult>>? checkpoint)
    {
        if (checkpoint is null) return null;
        return (kernel, requested) =>
        {
            var outcomes = checkpoint(kernel, requested);
            _durableThisTick.ExceptWith(requested);
            var requestedSet = requested.ToHashSet();
            foreach (var outcome in outcomes)
            {
                var goalId = new GoalId(outcome.GoalId);
                if (outcome.IsDurable && requestedSet.Contains(goalId)) _durableThisTick.Add(goalId);
            }
            return outcomes;
        };
    }
}
