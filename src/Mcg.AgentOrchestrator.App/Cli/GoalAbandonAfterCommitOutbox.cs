using System.Text.Json;
using System.Text.Json.Serialization;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class GoalAbandonAfterCommitOutbox
{
    internal const string Kind = "goal-abandon-after-commit";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private sealed record Payload(string GoalId, DateTimeOffset CommittedAt, string Reason,
        [property: JsonRequired] bool LeaveLiveDispatchesRunning, string? LifecycleMessageId);

    internal static OrchestratorStateOutboxMessage CreateMessage(GoalId goalId, string reason,
        DateTimeOffset committedAt, bool leaveLiveDispatchesRunning, string? lifecycleMessageId) =>
        new(Guid.NewGuid().ToString("N"), Kind, JsonSerializer.Serialize(
            new Payload(goalId.Value, committedAt, reason, leaveLiveDispatchesRunning, lifecycleMessageId), JsonOptions), committedAt);

    internal static async Task<bool> IsWaitingForLifecycle(
        IOrchestratorStateOutboxRepository outbox, OrchestratorStateOutboxMessage message)
    {
        Payload payload;
        try { payload = Deserialize(message); }
        catch (JsonException) { return false; } // Let Process quarantine malformed content.
        return payload.LifecycleMessageId is not null &&
            await outbox.GetOutboxStateAsync(payload.LifecycleMessageId) is { Status: not OrchestratorStateOutboxStatus.Quarantined };
    }

    internal static async Task<OrchestratorStateOutboxProcessingResult> Process(
        ITransactionalOrchestratorStateRepository repository, OrchestratorWorkspace workspace,
        OrchestratorStateOutboxMessage message, CancellationToken cancellationToken,
        AgentOrchestratorKernel? committedKernel = null, GoalWorktreeCleanupHooks? hooks = null,
        Action<GoalAbandonPlan?>? onApplied = null)
    {
        Payload payload;
        try { payload = Deserialize(message); }
        catch (JsonException ex) { return OrchestratorStateOutboxProcessingResult.Quarantined(ex.Message); }

        var snapshot = await repository.LoadGoalAsync(new GoalId(payload.GoalId), cancellationToken);
        if (snapshot is null)
            return OrchestratorStateOutboxProcessingResult.Quarantined($"Abandon cleanup targets a missing goal: {payload.GoalId}");
        if (snapshot.Status is not (GoalStatus.Completed or GoalStatus.Failed or GoalStatus.Cancelled or GoalStatus.Superseded))
            return new(OrchestratorStateOutboxDisposition.Complete, "goal is not terminal");
        // Cleanup must not overtake a lifecycle projection that is still awaiting delivery.
        if (payload.LifecycleMessageId is not null && repository is IOrchestratorStateOutboxRepository outbox &&
            await outbox.GetOutboxStateAsync(payload.LifecycleMessageId, cancellationToken) is { Status: not OrchestratorStateOutboxStatus.Quarantined })
            throw new IOException("The preceding lifecycle event delivery is still pending.");

        var kernel = committedKernel ?? new AgentOrchestratorKernel();
        if (committedKernel is null)
            kernel.ReplaceGoalWithSnapshot(snapshot);
        var goal = kernel.GetGoal(new GoalId(payload.GoalId));
        hooks ??= WorktreeCleanupContext.Load(attentionStoreDirectory: workspace.OrchestratorDirectory).Hooks;
        GoalAbandonPlan? plan = null;
        if (GoalOperationJournal.HasRetiredTerminalDisposition(GoalOperationJournal.Read(workspace.ExecutionDirectory, goal.Id)))
        {
            // A prior delivery may have recorded the disposition and crashed before completing cleanup.
            // Do not append again: raw journal timestamps are observable by timing readers.
            if (!payload.LeaveLiveDispatchesRunning)
            {
                _ = GoalWorktrees.RemoveTerminal(workspace.ExecutionDirectory, goal.Id, kernel, hooks);
                if (DotnetBuildEnvironmentManager.InspectGoalLease(goal.Id, hooks.BuildStorageRoot).CanCleanup)
                    _ = DotnetBuildEnvironmentManager.TryCleanupOrphanedGoalLease(goal.Id, out _, out _, hooks.BuildStorageRoot);
                if (committedKernel is not null)
                    plan = GoalAbandonPlanner.Build(kernel, goal, workspace, payload.Reason, hooks, dryRun: false);
            }
        }
        else if (payload.LeaveLiveDispatchesRunning)
            GoalAbandonPlanner.RecordAbandonedTerminalDisposition(goal, workspace, payload.Reason);
        else
            plan = GoalAbandonPlanner.CompleteAfterCommit(kernel, goal, workspace, payload.Reason, hooks);
        onApplied?.Invoke(plan);
        return OrchestratorStateOutboxProcessingResult.Completed;
    }

    private static Payload Deserialize(OrchestratorStateOutboxMessage message)
    {
        var payload = JsonSerializer.Deserialize<Payload>(message.PayloadJson, JsonOptions);
        if (message.Kind != Kind || payload is null || !Guid.TryParseExact(payload.GoalId, "N", out _) ||
            payload.CommittedAt == default || string.IsNullOrWhiteSpace(payload.Reason) ||
            (payload.LifecycleMessageId is not null && !Guid.TryParseExact(payload.LifecycleMessageId, "N", out _)))
            throw new JsonException("Invalid abandon cleanup payload.");
        return payload;
    }
}
