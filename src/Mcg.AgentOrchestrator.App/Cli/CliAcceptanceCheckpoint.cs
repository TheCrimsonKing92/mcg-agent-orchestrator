using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

// Owns one invocation's target baseline. Reloaded non-target goals are observations only:
// neither progress nor critical saves may acquire persistence ownership of them.
internal sealed class CliAcceptanceCheckpoint
{
    private readonly ITransactionalOrchestratorStateRepository _repository;
    private readonly OrchestratorWorkspace _workspace;
    private readonly GoalId _goalId;
    private readonly Dictionary<string, GoalSnapshot> _baselines;
    private readonly Action<AgentOrchestratorKernel> _persist;
    private Dictionary<string, string> _readOnlyStates = new(StringComparer.Ordinal);

    internal CliAcceptanceCheckpoint(ITransactionalOrchestratorStateRepository repository,
        OrchestratorWorkspace workspace, AgentOrchestratorKernel loaded, GoalId goalId,
        GoalSnapshot baseline)
    {
        _repository = repository;
        _workspace = workspace;
        _goalId = goalId;
        _baselines = new(StringComparer.Ordinal) { [baseline.Id] = baseline };
        _persist = CliCommandCheckpoint.Create(repository, workspace, loaded, _baselines, RecordDurableBaseline);
    }

    internal AgentOrchestratorKernel Reload()
    {
        var reloaded = _repository.LoadAsync().GetAwaiter().GetResult();
        var snapshot = reloaded.ExportSnapshot();
        _readOnlyStates = snapshot.Goals.Where(goal => goal.Id != _goalId.Value)
            .ToDictionary(goal => goal.Id, goal => StateJson(snapshot, goal), StringComparer.Ordinal);
        return reloaded;
    }

    internal void Persist(AgentOrchestratorKernel checkpoint, GoalId id)
    {
        if (id != _goalId)
            throw new InvalidOperationException($"Acceptance checkpoint cannot save non-target goal {id.Value}.");
        Persist(checkpoint);
    }

    internal void Persist(AgentOrchestratorKernel checkpoint)
    {
        ValidateScope(checkpoint); // Validate the whole reload before the first target write.
        var snapshot = checkpoint.ExportSnapshot();
        var target = AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot(
            [checkpoint.ExportGoalSnapshot(_goalId)],
            snapshot.HumanInputRequests.Where(request => request.GoalId == _goalId.Value).ToArray()));
        try { _persist(target); }
        finally
        {
            // Rejected merges also rebase: a caught failure must not replay stale state.
            var durable = target.ExportSnapshot();
            checkpoint.ReplaceGoalStateWithSnapshot(durable.Goals.Single(), durable.HumanInputRequests);
        }
    }

    internal void PersistCritical(AgentOrchestratorKernel checkpoint, IReadOnlyCollection<GoalId> ids)
    {
        ValidateScope(checkpoint);
        if (ids.Any(id => id != _goalId))
            throw new InvalidOperationException("Acceptance critical checkpoint cannot save non-target goals.");
        CliPersistentStateRunner.PersistCriticalGoalSnapshotsOrThrow(
            _repository, checkpoint, ids, _baselines, _workspace.SqliteStatePath, results =>
            {
                foreach (var result in results)
                {
                    _ = CliPersistentStateRunner.RebaseCheckpointAfterDurableSave(checkpoint, result);
                    if (result.PersistedSnapshot is not null)
                        RecordDurableBaseline(result.PersistedSnapshot);
                }
            });
    }

    internal void PersistGoals(AgentOrchestratorKernel checkpoint, IReadOnlyCollection<GoalId> ids)
    {
        if (ids.Any(id => id != _goalId))
            throw new InvalidOperationException("Acceptance checkpoint cannot save non-target goals.");
        if (ids.Count > 0) Persist(checkpoint);
    }

    internal void RecordDurableBaseline(GoalSnapshot snapshot)
    {
        if (snapshot.Id != _goalId.Value)
            throw new InvalidOperationException("Acceptance cannot own a non-target durable baseline.");
        // Progress saves and externally committed snapshots advance the same critical baseline.
        _baselines[snapshot.Id] = snapshot;
    }

    private void ValidateScope(AgentOrchestratorKernel checkpoint)
    {
        var snapshot = checkpoint.ExportSnapshot();
        foreach (var goal in snapshot.Goals.Where(goal => goal.Id != _goalId.Value))
        {
            if (!_readOnlyStates.TryGetValue(goal.Id, out var observed) ||
                !string.Equals(observed, StateJson(snapshot, goal), StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"Acceptance checkpoint changed non-target goal {goal.Id} without an owned baseline; no state was saved.");
        }
        if (snapshot.HumanInputRequests.Any(request => request.GoalId != _goalId.Value &&
                !snapshot.Goals.Any(goal => goal.Id == request.GoalId)))
            throw new InvalidOperationException("Acceptance checkpoint contains unowned human requests; no state was saved.");
    }

    private static string StateJson(OrchestratorSnapshot snapshot, GoalSnapshot goal) =>
        JsonSerializer.Serialize(new GoalStateSnapshot(goal,
            snapshot.HumanInputRequests.Where(request => request.GoalId == goal.Id).ToArray()));
}
