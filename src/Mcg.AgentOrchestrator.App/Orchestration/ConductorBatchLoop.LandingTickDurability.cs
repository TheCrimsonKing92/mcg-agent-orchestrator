using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorBatchLoop
{
    private readonly HashSet<GoalId> _landedThisTick = [];
    private readonly HashSet<GoalId> _landingTickGoalIds = [];

    private void ResetLandingTickSave()
    {
        _landedThisTick.Clear();
        _landingTickGoalIds.Clear();
    }

    private void NoteLandingForTick(string goalId) => _landedThisTick.Add(new GoalId(goalId));

    private void CaptureLandingTickChanges(HashSet<GoalId> changedGoalIds, bool relaunchPending)
    {
        if (_landedThisTick.Count == 0) return;
        changedGoalIds.UnionWith(_landedThisTick);
        if (relaunchPending) _landingTickGoalIds.UnionWith(changedGoalIds);
        _landedThisTick.Clear();
    }

    private void SaveLandingTickBeforeRelaunch(
        AgentOrchestratorKernel kernel,
        Func<AgentOrchestratorKernel, IReadOnlyCollection<GoalId>, IReadOnlyList<GoalSnapshotCheckpointResult>>? checkpointGoalTick,
        Action<AgentOrchestratorKernel, IReadOnlyCollection<GoalId>>? persistGoalTick,
        Action<AgentOrchestratorKernel>? persistTick,
        Dictionary<string, GoalSnapshotCheckpointResult> checkpointHeldGoals,
        int tick,
        Action<TimeSpan>? busyWriteDelay)
    {
        if (_landingTickGoalIds.Count == 0) return;
        var goalIds = _landingTickGoalIds.ToArray();
        if (checkpointGoalTick is not null)
        {
            var durable = ApplyCheckpointOutcomes(
                checkpointGoalTick(kernel, goalIds), goalIds, checkpointHeldGoals,
                tick, "landing-state-save", []);
            if (durable.Count != goalIds.Length)
                throw new InvalidOperationException("Landing state checkpoint was held; refusing self-relaunch.");
        }
        else if (persistGoalTick is not null)
        {
            PersistGoalTickOrThrow(persistGoalTick, kernel, goalIds, tick, [], busyWriteDelay);
        }
        else if (!TryPersistTick(
                     persistTick, kernel, tick, ResolveGoalContext(goalIds, onlyGoalId: null),
                     "landing-state-save", [], busyWriteDelay))
        {
            throw new InvalidOperationException("Landing state save failed; refusing self-relaunch.");
        }
        _landingTickGoalIds.Clear();
    }
}
