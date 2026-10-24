using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

// Invocation-local persistence ownership for direct-writer families. An observed goal
// never becomes a creation permission. All scope checks precede the first write.
internal sealed class CliOwnedGoalCheckpoint
{
    private readonly ITransactionalOrchestratorStateRepository _repository;
    private readonly Dictionary<string, GoalStateSnapshot> _baselines;
    private readonly string? _target;
    private readonly CancellationToken _cancellationToken;

    internal CliOwnedGoalCheckpoint(ITransactionalOrchestratorStateRepository repository,
        AgentOrchestratorKernel loaded, GoalId? target = null, CancellationToken cancellationToken = default)
    {
        _repository = repository;
        _target = target?.Value;
        _cancellationToken = cancellationToken;
        var snapshot = loaded.ExportSnapshot();
        _baselines = snapshot.Goals.ToDictionary(goal => goal.Id, goal => State(snapshot, goal), StringComparer.Ordinal);
    }

    internal void Persist(AgentOrchestratorKernel checkpoint,
        Action<GoalSnapshotSaveResult, GoalStateSnapshot>? onRejected = null)
    {
        var snapshot = checkpoint.ExportSnapshot();
        var changed = new List<GoalStateSnapshot>();
        foreach (var goal in snapshot.Goals)
        {
            var current = State(snapshot, goal);
            if (!_baselines.TryGetValue(goal.Id, out var baseline))
                throw new InvalidOperationException($"Checkpoint has no owned baseline for goal {goal.Id}; no state was saved.");
            if (Same(baseline, current)) continue;
            if (_target is not null && goal.Id != _target)
                throw new InvalidOperationException($"Checkpoint changed non-target goal {goal.Id}; no state was saved.");
            changed.Add(current);
        }
        if (snapshot.HumanInputRequests.Any(request => !snapshot.Goals.Any(goal => goal.Id == request.GoalId)) ||
            _baselines.Keys.Any(id => !snapshot.Goals.Any(goal => goal.Id == id)))
            throw new InvalidOperationException("Checkpoint removed an observed goal or contains unowned requests; no state was saved.");

        foreach (var current in changed)
        {
            var baseline = _baselines[current.Goal.Id];
            var terminal = current.Goal.Status is
                GoalStatus.Completed or GoalStatus.Cancelled or GoalStatus.Failed or GoalStatus.Superseded;
            var result = Save(baseline, current, terminal);
            _ = CliPersistentStateRunner.RebaseCheckpointAfterDurableSave(checkpoint, result);
            if (result.PersistedSnapshot is { } durable)
            {
                if (result.Disposition == GoalSnapshotSaveDisposition.Saved)
                    checkpoint.ReplaceGoalStateWithSnapshot(durable, result.PersistedHumanInputRequests ?? current.HumanInputRequests);
                _baselines[durable.Id] = new(durable, result.PersistedHumanInputRequests ?? current.HumanInputRequests);
            }
            if (result.Disposition != GoalSnapshotSaveDisposition.Skipped && result.PersistedSnapshot is not null) continue;
            if (onRejected is not null)
            {
                onRejected(result, current);
                continue;
            }
            var failure = new InvalidOperationException(
                $"COMMAND_CHECKPOINT_REJECTED goal={current.Goal.Id} disposition={result.Disposition} reason={result.Message}");
            failure.Data["SubmittedGoalState"] = JsonSerializer.Serialize(current);
            failure.Data["StoredGoalState"] = JsonSerializer.Serialize(result);
            failure.Data["BaselineGoalState"] = JsonSerializer.Serialize(baseline);
            throw failure;
        }
    }

    private GoalSnapshotSaveResult Save(GoalStateSnapshot baseline, GoalStateSnapshot current, bool terminal)
    {
        try
        {
            return _repository.SaveGoalSnapshotsWithMergeAsync([
                new GoalSnapshotSaveRequest(baseline.Goal, current.Goal, current.HumanInputRequests, RejectConflict: terminal)
            ], _cancellationToken).GetAwaiter().GetResult().Single();
        }
        catch (NotSupportedException)
        {
            // Merge persistence is optional. Use the existing goal transaction for repositories
            // without it, checking the load-time baseline inside that transaction. Never restore
            // a captured goal over concurrent graph or human-input changes, even for progress.
            return _repository.TransactGoalStateAsync(new GoalId(current.Goal.Id), (stored, _) =>
            {
                var matches = stored is not null && Same(baseline, stored);
                var result = new GoalSnapshotSaveResult(current.Goal.Id,
                    matches ? GoalSnapshotSaveDisposition.Saved : GoalSnapshotSaveDisposition.Skipped,
                    matches ? current.Goal : stored?.Goal,
                    matches ? "stored state matched command baseline" : "stored state changed; rejected stale command checkpoint",
                    matches ? current.HumanInputRequests : stored?.HumanInputRequests);
                return Task.FromResult((matches, matches ? current : null, result));
            }, _cancellationToken).GetAwaiter().GetResult();
        }
    }

    internal void PersistSweep(AgentOrchestratorKernel checkpoint, string orchestratorDirectory) =>
        Persist(checkpoint, (result, submitted) =>
        {
            var evidence = $"COMMAND_CHECKPOINT_REJECTED goal={result.GoalId} reason={result.Message} " +
                $"submitted={JsonSerializer.Serialize(submitted)} stored={JsonSerializer.Serialize(result)}";
            Console.Error.WriteLine($"Startup sweep checkpoint rejected for {result.GoalId}: {result.Message}");
            CollaborationItemStore.ForDirectory(orchestratorDirectory).RaiseAsync(
                CollaborationItemType.Decision, result.GoalId, "Startup sweep checkpoint conflict",
                evidence + $" Reload goal-diagnostics {result.GoalId} and reconcile against the authoritative graph before terminalization.",
                $"startup-checkpoint-conflict:{result.GoalId}").GetAwaiter().GetResult();
        });

    private static GoalStateSnapshot State(OrchestratorSnapshot snapshot, GoalSnapshot goal) =>
        new(goal, snapshot.HumanInputRequests.Where(request => request.GoalId == goal.Id).ToArray());

    private static bool Same(GoalStateSnapshot left, GoalStateSnapshot right) =>
        JsonSerializer.Serialize(left) == JsonSerializer.Serialize(right);
}
