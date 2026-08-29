using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorBatchLoop
{
    private static IReadOnlyCollection<GoalId> PersistSweepTerminalizations(
        TerminalGoalSweepResult? sweepResult,
        AgentOrchestratorKernel kernel,
        Func<AgentOrchestratorKernel, IReadOnlyCollection<GoalId>, IReadOnlyList<GoalSnapshotCheckpointResult>>? checkpointGoalTick,
        Action<AgentOrchestratorKernel, IReadOnlyCollection<GoalId>>? persistGoalTick,
        Dictionary<string, GoalSnapshotCheckpointResult> checkpointHeldGoals,
        int tick,
        List<string> tickLines,
        Action<TimeSpan>? busyWriteDelay)
    {
        var goalIds = sweepResult?.ExplicitlyTerminalizedGoalIds ?? [];
        if (goalIds.Count > 0 && checkpointGoalTick is not null)
        {
            ApplyCheckpointOutcomes(
                checkpointGoalTick(kernel, goalIds),
                goalIds,
                checkpointHeldGoals,
                tick,
                "sweep-terminalization",
                []);
        }
        else if (goalIds.Count > 0 && persistGoalTick is not null)
        {
            PersistGoalTickOrThrow(persistGoalTick, kernel, goalIds, tick, tickLines, busyWriteDelay);
        }

        return goalIds;
    }
}
