using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorBatchLoop
{
    private readonly HashSet<GoalId> _landedThisTick = [];
    private readonly HashSet<GoalId> _landingTickGoalIds = [];

    private Func<AgentOrchestratorKernel, IReadOnlyCollection<GoalId>, IReadOnlyList<GoalSnapshotCheckpointResult>>?
        ResetLandingTickSave(
            Func<AgentOrchestratorKernel, IReadOnlyCollection<GoalId>, IReadOnlyList<GoalSnapshotCheckpointResult>>? checkpointGoalTick)
    {
        _landedThisTick.Clear();
        _landingTickGoalIds.Clear();
        _durableThisTick.Clear();
        return TrackTickDurableCheckpoints(checkpointGoalTick);
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
        bool relaunchPending,
        AgentOrchestratorKernel kernel,
        Func<AgentOrchestratorKernel, IReadOnlyCollection<GoalId>, IReadOnlyList<GoalSnapshotCheckpointResult>>? checkpointGoalTick,
        Action<AgentOrchestratorKernel, IReadOnlyCollection<GoalId>>? persistGoalTick,
        Action<AgentOrchestratorKernel>? persistTick,
        Dictionary<string, GoalSnapshotCheckpointResult> checkpointHeldGoals,
        int tick,
        Action<TimeSpan>? busyWriteDelay)
    {
        try
        {
            if (!relaunchPending || _landingTickGoalIds.Count == 0) return;
            var goalIds = _landingTickGoalIds.Where(id => !_durableThisTick.Contains(id)).ToArray();
            if (goalIds.Length == 0)
            {
                _landingTickGoalIds.Clear();
                return;
            }
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
        finally
        {
            _durableThisTick.Clear();
        }
    }
}
