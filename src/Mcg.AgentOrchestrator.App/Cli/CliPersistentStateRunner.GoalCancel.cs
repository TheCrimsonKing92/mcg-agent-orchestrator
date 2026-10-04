using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static partial class CliPersistentStateRunner
{
    private static bool ExecuteGoalCancelTransition(
        IReadOnlyList<string> args,
        ITransactionalOrchestratorStateRepository stateRepository,
        OrchestratorWorkspace workspace,
        ref Goal? currentGoal)
    {
        var command = CliCommandHandlers.PrepareGoalCancelCommand(args);
        var currentGoalId = currentGoal?.Id;
        var goalId = ResolveSingleGoalCommandGoalId(stateRepository, currentGoalId?.Value, command.GoalSelector);
        var outcome = ExecuteGoalCancelApplication(command, goalId, currentGoalId, stateRepository, out var pendingMessageId);
        if (outcome.Goal is not null)
        {
            currentGoal = outcome.Goal;
        }

        CliCommandHandlers.RenderGoalCancelOutcome(outcome, workspace,
            pendingMessageId is null ? null : () => DeliverCommittedGoalCancelEvent(
                (IOrchestratorStateOutboxRepository)stateRepository, workspace, goalId, pendingMessageId));
        return outcome.ShouldSave;
    }

    internal static CliCommandHandlers.GoalLifecycleTransitionOutcome ExecuteGoalCancelApplication(
        CliCommandHandlers.GoalCancelCommand command,
        GoalId goalId,
        GoalId? currentGoalId,
        ITransactionalOrchestratorStateRepository stateRepository) =>
        ExecuteGoalCancelApplication(command, goalId, currentGoalId, stateRepository, out _);

    private static CliCommandHandlers.GoalLifecycleTransitionOutcome ExecuteGoalCancelApplication(
        CliCommandHandlers.GoalCancelCommand command,
        GoalId goalId,
        GoalId? currentGoalId,
        ITransactionalOrchestratorStateRepository stateRepository,
        out string? pendingMessageId)
    {
        pendingMessageId = null;
        if (stateRepository is not IOrchestratorStateOutboxRepository)
            return TransactGoalSnapshotTransition(stateRepository, "cli:cancel-goal", goalId,
                kernel => CliCommandHandlers.ApplyGoalCancelWithoutRendering(command, kernel, goalId, currentGoalId),
                out _);

        try
        {
            var committed = stateRepository.TransactGoalStateWithOutboxAsync(
                "cli:cancel-goal", goalId, (state, _) =>
                {
                    if (state is null)
                        throw new KeyNotFoundException($"Goal '{goalId.Value}' was not found.");
                    var kernel = RestoreGoalExactly(state.Goal);
                    var result = CliCommandHandlers.ApplyGoalCancelWithoutRendering(command, kernel, goalId, currentGoalId);
                    var message = result.ShouldSave
                        ? GoalLifecycleEventOutbox.CreateMessage(result.CommittedTimelineEvent
                            ?? throw new InvalidOperationException("Committed lifecycle outcome is missing its timeline event."))
                        : null;
                    IReadOnlyList<OrchestratorStateOutboxMessage> messages = message is null ? [] : [message];
                    var updated = new GoalStateSnapshot(
                        result.ShouldSave ? ExportGoalSnapshot(kernel, goalId) : state.Goal, []);
                    return Task.FromResult((result.ShouldSave, (GoalStateSnapshot?)updated,
                        (Outcome: result, MessageId: message?.Id), messages));
                }).GetAwaiter().GetResult();
            pendingMessageId = committed.MessageId;
            return committed.Outcome;
        }
        catch (GoalTransactionConflictException)
        {
            return new CliCommandHandlers.GoalLifecycleTransitionOutcome(
                CliCommandHandlers.GoalLifecycleTransitionDisposition.ConflictExhausted, goalId, ObservedStatus: null);
        }
    }
}
