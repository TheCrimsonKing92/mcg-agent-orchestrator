using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Mcg.AgentOrchestrator.App.Orchestration;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class CliCommandCheckpoint
{
    // Baselines belong to one command invocation. Each successful checkpoint advances them;
    // an unchanged goal in the loaded kernel must never be submitted as a whole-state write.
    internal static Action<AgentOrchestratorKernel> Create(
        ITransactionalOrchestratorStateRepository repository,
        OrchestratorWorkspace workspace,
        AgentOrchestratorKernel loaded,
        IDictionary<string, GoalSnapshot> baselines,
        Action<GoalSnapshot>? recordDurableBaseline = null)
    {
        var humanBaselines = loaded.ExportSnapshot().HumanInputRequests
            .GroupBy(request => request.GoalId)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var committed = new HashSet<string>(StringComparer.Ordinal);

        return checkpoint =>
        {
            var snapshot = checkpoint.ExportSnapshot();
            foreach (var goal in snapshot.Goals)
            {
                var humans = snapshot.HumanInputRequests.Where(request => request.GoalId == goal.Id).ToArray();
                if (baselines.TryGetValue(goal.Id, out var baseline) &&
                    SameGoal(baseline, goal) && Same(humanBaselines.GetValueOrDefault(goal.Id, []), humans))
                    continue;

                try
                {
                    GoalSnapshotSaveResult result;
                    if (baseline is null)
                    {
                        if (repository is not IOrchestratorStateOutboxRepository outboxRepository)
                            throw new InvalidOperationException("New goals require the durable goal-creation owner.");

                        // Merge-save deliberately skips absent rows. Creation must instead validate
                        // and commit through the existing scoped creation transaction/outbox owner.
                        CliPersistentStateRunner.CommitGoalCreationAsync(outboxRepository, workspace, goal, (current, _) =>
                        {
                            GoalCreationPreconditionValidator.Validate(current, goal, workspace);
                            current.ReplaceGoalStateWithSnapshot(goal, humans);
                            return Task.FromResult((true, true,
                                (IReadOnlyList<OrchestratorStateOutboxMessage>)(
                                    GoalRefinementWorkCoordinator.HasPendingWork(checkpoint.GetGoal(new GoalId(goal.Id)))
                                        ? [GoalRefinementWorkCoordinator.CreateMessage(new GoalId(goal.Id))]
                                        : [])));
                        }).GetAwaiter().GetResult();
                        result = new GoalSnapshotSaveResult(goal.Id, GoalSnapshotSaveDisposition.Saved,
                            goal, "goal creation committed", humans);
                    }
                    else
                    {
                        // These are progress checkpoints, not process admission. Allow the existing
                        // merger to retain independent progress (including acceptance verdicts).
                        result = repository.SaveGoalSnapshotsWithMergeAsync([
                            new GoalSnapshotSaveRequest(baseline, goal, humans)
                        ]).GetAwaiter().GetResult().Single();
                    }

                    _ = CliPersistentStateRunner.RebaseCheckpointAfterDurableSave(checkpoint, result);
                    if (result.PersistedSnapshot is not null)
                    {
                        if (recordDurableBaseline is not null)
                            recordDurableBaseline(result.PersistedSnapshot);
                        else
                            baselines[goal.Id] = result.PersistedSnapshot;
                        humanBaselines[goal.Id] = (result.PersistedHumanInputRequests ?? humans).ToArray();
                    }
                    if (result.Disposition == GoalSnapshotSaveDisposition.Skipped || result.PersistedSnapshot is null)
                    {
                        var failure = new InvalidOperationException(
                            $"COMMAND_CHECKPOINT_REJECTED goal={goal.Id} disposition={result.Disposition} reason={result.Message}");
                        // Retain submitted and stored evidence for the caller; rebasing must not
                        // erase the evidence explaining a rejected intended mutation.
                        failure.Data["SubmittedGoalState"] = JsonSerializer.Serialize(new GoalStateSnapshot(goal, humans));
                        failure.Data["StoredGoalState"] = JsonSerializer.Serialize(result);
                        throw failure;
                    }
                    committed.Add(goal.Id);
                }
                catch (Exception ex)
                {
                    var failure = new InvalidOperationException(
                        $"COMMAND_CHECKPOINT_FAILED goal={goal.Id} committed={string.Join(',', committed)} reason={ex.Message}", ex);
                    failure.Data["CommittedGoalIds"] = string.Join(',', committed);
                    failure.Data["UncommittedGoalId"] = goal.Id;
                    foreach (System.Collections.DictionaryEntry entry in ex.Data)
                        failure.Data[entry.Key] = entry.Value;
                    throw failure;
                }
            }
        };

        // The merger can retain an empty obligation list, while Goal.ToSnapshot exports
        // an empty list as null. Keep the exact durable baseline for conflict checks,
        // but do not submit another mutation solely for that representation change.
        static bool SameGoal(GoalSnapshot left, GoalSnapshot right) => Same(
            left with { GateTestObligations = left.GateTestObligations is { Count: 0 } ? null : left.GateTestObligations },
            right with { GateTestObligations = right.GateTestObligations is { Count: 0 } ? null : right.GateTestObligations });

        static bool Same<T>(T left, T right) =>
            string.Equals(JsonSerializer.Serialize(left), JsonSerializer.Serialize(right), StringComparison.Ordinal);
    }
}
