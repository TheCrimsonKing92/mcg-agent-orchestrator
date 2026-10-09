using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

/// <summary>
/// Owns one conductor dispatch's lifecycle hold and critical checkpoint callback.
/// A successful save releases its events and prevents rollback of committed state.
/// </summary>
internal sealed class CriticalDispatchLifecycleCheckpoint : IDisposable
{
    private readonly CriticalDispatchLifecycleEventGate _gate;
    private readonly IDisposable _hold;
    private readonly Action<AgentOrchestratorKernel, IReadOnlyCollection<GoalId>> _persist;
    private readonly Action<GoalId>? _onCommitted;

    private CriticalDispatchLifecycleCheckpoint(
        CriticalDispatchLifecycleEventGate gate, AgentOrchestratorKernel kernel, GoalId goalId,
        Action<AgentOrchestratorKernel, IReadOnlyCollection<GoalId>> persist,
        Action<GoalId>? onCommitted)
    {
        _gate = gate;
        _persist = persist;
        _onCommitted = onCommitted;
        _hold = gate.Hold(goalId, kernel);
    }

    internal bool HasCommitted { get; private set; }

    internal Action<AgentOrchestratorKernel, GoalId, TaskId, DispatchRecordCheckpointPhase> BeforeWorkerStart => Persist;

    internal static CriticalDispatchLifecycleCheckpoint? Begin(
        CriticalDispatchLifecycleEventGate gate, AgentOrchestratorKernel kernel, GoalId goalId,
        Action<AgentOrchestratorKernel, IReadOnlyCollection<GoalId>>? persist,
        Action<GoalId>? onCommitted) =>
        persist is null ? null : new(gate, kernel, goalId, persist, onCommitted);

    private void Persist(AgentOrchestratorKernel kernel, GoalId goalId, TaskId taskId, DispatchRecordCheckpointPhase phase)
    {
        _gate.CommitCheckpoint(goalId, () =>
        {
            ConductorBatchLoop.PersistCriticalDispatchStartOrThrow(_persist, kernel, goalId, taskId, phase);
            HasCommitted = true;
        });
        _onCommitted?.Invoke(goalId);
    }

    public void Dispose() => _hold.Dispose();
}
