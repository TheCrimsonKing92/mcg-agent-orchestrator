using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static partial class CliPersistentStateRunner
{
    private static bool ExecuteGoalAbandonTransition(
        IReadOnlyList<string> args,
        ITransactionalOrchestratorStateRepository stateRepository,
        OrchestratorWorkspace workspace,
        ref Goal? currentGoal)
    {
        var command = CliCommandHandlers.PrepareGoalAbandonCommand(args);
        var goalId = ResolveSingleGoalCommandGoalId(stateRepository, currentGoal?.Id.Value, command.GoalSelector);
        var hooks = WorktreeCleanupContext.Load(attentionStoreDirectory: workspace.OrchestratorDirectory).Hooks;
        var outcome = ExecuteGoalAbandonApplication(command, goalId, stateRepository, workspace, hooks,
            out var committedKernel, out var pendingMessageId, out var pendingCleanupId);
        if (outcome.Goal is not null)
        {
            currentGoal = outcome.Goal;
        }

        CliCommandHandlers.RenderGoalAbandonOutcome(command, outcome, committedKernel, workspace, hooks,
            pendingMessageId is null ? null : () => DeliverCommittedGoalLifecycleEvent(
                (IOrchestratorStateOutboxRepository)stateRepository, workspace, goalId, GoalStatus.Cancelled, pendingMessageId),
            pendingCleanupId is null ? null : () => DeliverCommittedGoalAbandonCleanup(
                (IOrchestratorStateOutboxRepository)stateRepository, workspace, goalId, outcome.Goal!.Status,
                pendingCleanupId, committedKernel!, hooks));
        return outcome.ShouldSave;
    }

    internal static CliCommandHandlers.GoalLifecycleTransitionOutcome ExecuteGoalAbandonApplication(
        CliCommandHandlers.GoalAbandonCommand command,
        GoalId goalId,
        ITransactionalOrchestratorStateRepository stateRepository,
        OrchestratorWorkspace workspace,
        GoalWorktreeCleanupHooks hooks) =>
        ExecuteGoalAbandonApplication(command, goalId, stateRepository, workspace, hooks, out _, out _, out _);

    private static CliCommandHandlers.GoalLifecycleTransitionOutcome ExecuteGoalAbandonApplication(
        CliCommandHandlers.GoalAbandonCommand command,
        GoalId goalId,
        ITransactionalOrchestratorStateRepository stateRepository,
        OrchestratorWorkspace workspace,
        GoalWorktreeCleanupHooks hooks,
        out AgentOrchestratorKernel? committedKernel,
        out string? pendingMessageId,
        out string? pendingCleanupId)
    {
        pendingMessageId = null;
        pendingCleanupId = null;
        if (!command.Confirmed)
        {
            var snapshot = stateRepository.LoadGoalAsync(goalId).GetAwaiter().GetResult()
                ?? throw new KeyNotFoundException($"Goal '{goalId.Value}' was not found.");
            committedKernel = RestoreGoalExactly(snapshot);
            return CliCommandHandlers.ApplyGoalAbandonWithoutRendering(
                command, committedKernel, goalId, workspace, hooks);
        }

        return TransactGoalAbandonWithOutbox(stateRepository, goalId, command.Reason,
            kernel => CliCommandHandlers.ApplyGoalAbandonWithoutRendering(command, kernel, goalId, workspace, hooks),
            out committedKernel, out pendingMessageId, out pendingCleanupId);
    }

    private static CliCommandHandlers.GoalLifecycleTransitionOutcome TransactGoalAbandonWithOutbox(
        ITransactionalOrchestratorStateRepository repository, GoalId goalId, string reason,
        Func<AgentOrchestratorKernel, CliCommandHandlers.GoalLifecycleTransitionOutcome> apply,
        out AgentOrchestratorKernel? committedKernel, out string? pendingMessageId, out string? pendingCleanupId)
    {
        pendingMessageId = null;
        pendingCleanupId = null;
        if (repository is not IOrchestratorStateOutboxRepository)
            return TransactGoalSnapshotTransition(repository, "cli:abandon-goal", goalId, apply, out committedKernel);

        try
        {
            var committed = repository.TransactGoalStateWithOutboxAsync("cli:abandon-goal", goalId, (state, _) =>
            {
                if (state is null) throw new KeyNotFoundException($"Goal '{goalId.Value}' was not found.");
                var kernel = RestoreGoalExactly(state.Goal);
                var result = apply(kernel);
                var lifecycle = result.ShouldSave && result.CommittedTimelineEvent is not null
                    ? GoalLifecycleEventOutbox.CreateMessage(result.CommittedTimelineEvent) : null;
                var cleanup = result.ShouldSave ? GoalAbandonAfterCommitOutbox.CreateMessage(goalId,
                    reason.Trim(),
                    result.CommittedTimelineEvent?.OccurredAt ?? DateTimeOffset.UtcNow,
                    result.LiveDispatches is { Count: > 0 }, lifecycle?.Id) : null;
                var messages = new List<OrchestratorStateOutboxMessage>();
                if (lifecycle is not null) messages.Add(lifecycle);
                if (cleanup is not null) messages.Add(cleanup);
                var updated = new GoalStateSnapshot(result.ShouldSave ? ExportGoalSnapshot(kernel, goalId) : state.Goal, []);
                return Task.FromResult((result.ShouldSave, (GoalStateSnapshot?)updated,
                    (Outcome: result, Kernel: kernel, MessageId: lifecycle?.Id, CleanupId: cleanup?.Id),
                    (IReadOnlyList<OrchestratorStateOutboxMessage>)messages));
            }).GetAwaiter().GetResult();
            committedKernel = committed.Kernel;
            pendingMessageId = committed.MessageId;
            pendingCleanupId = committed.CleanupId;
            return committed.Outcome;
        }
        catch (GoalTransactionConflictException)
        {
            committedKernel = null;
            return new(CliCommandHandlers.GoalLifecycleTransitionDisposition.ConflictExhausted, goalId, ObservedStatus: null);
        }
    }
}
