using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static partial class CliPersistentStateRunner
{
    private static bool ExecuteGoalSupersedeTransition(
        IReadOnlyList<string> args,
        ITransactionalOrchestratorStateRepository stateRepository,
        OrchestratorWorkspace workspace,
        ref Goal? currentGoal)
    {
        var command = CliCommandHandlers.IsStopSupersedeAlias(args)
            ? CliCommandHandlers.PrepareGoalSupersedeCommandFromStopAlias(args)
            : CliCommandHandlers.PrepareGoalSupersedeCommand(args);
        var currentGoalId = currentGoal?.Id;
        var goalId = ResolveSingleGoalCommandGoalId(stateRepository, currentGoalId?.Value, command.GoalSelector);
        var outcome = ExecuteGoalSupersedeApplication(command, goalId, currentGoalId, stateRepository, out var pendingMessageId);
        if (outcome.Goal is not null)
        {
            currentGoal = outcome.Goal;
        }

        CliCommandHandlers.RenderGoalSupersedeOutcome(outcome, workspace,
            pendingMessageId is null ? null : () => DeliverCommittedGoalLifecycleEvent(
                (IOrchestratorStateOutboxRepository)stateRepository, workspace, goalId, GoalStatus.Superseded, pendingMessageId));
        return outcome.ShouldSave;
    }

    internal static CliCommandHandlers.GoalLifecycleTransitionOutcome ExecuteGoalSupersedeApplication(
        CliCommandHandlers.GoalSupersedeCommand command,
        GoalId goalId,
        GoalId? currentGoalId,
        ITransactionalOrchestratorStateRepository stateRepository) =>
        ExecuteGoalSupersedeApplication(command, goalId, currentGoalId, stateRepository, out _);

    private static CliCommandHandlers.GoalLifecycleTransitionOutcome ExecuteGoalSupersedeApplication(
        CliCommandHandlers.GoalSupersedeCommand command,
        GoalId goalId,
        GoalId? currentGoalId,
        ITransactionalOrchestratorStateRepository stateRepository,
        out string? pendingMessageId) =>
        TransactGoalLifecycleTransitionWithOutbox(stateRepository, "cli:supersede-goal", goalId,
            kernel => CliCommandHandlers.ApplyGoalSupersedeWithoutRendering(command, kernel, goalId, currentGoalId),
            out _, out pendingMessageId);

    private static CliCommandHandlers.GoalLifecycleTransitionOutcome TransactGoalLifecycleTransitionWithOutbox(
        ITransactionalOrchestratorStateRepository stateRepository,
        string operationName,
        GoalId goalId,
        Func<AgentOrchestratorKernel, CliCommandHandlers.GoalLifecycleTransitionOutcome> apply,
        out AgentOrchestratorKernel? committedKernel,
        out string? pendingMessageId)
    {
        pendingMessageId = null;
        if (stateRepository is not IOrchestratorStateOutboxRepository)
            return TransactGoalSnapshotTransition(stateRepository, operationName, goalId, apply, out committedKernel);

        try
        {
            var committed = stateRepository.TransactGoalStateWithOutboxAsync(
                operationName, goalId, (state, _) =>
                {
                    if (state is null)
                        throw new KeyNotFoundException($"Goal '{goalId.Value}' was not found.");
                    var kernel = RestoreGoalExactly(state.Goal);
                    var result = apply(kernel);
                    // Already-terminal abandon saves state without adding a lifecycle event.
                    var message = result.ShouldSave && result.CommittedTimelineEvent is not null
                        ? GoalLifecycleEventOutbox.CreateMessage(result.CommittedTimelineEvent)
                        : null;
                    IReadOnlyList<OrchestratorStateOutboxMessage> messages = message is null ? [] : [message];
                    var updated = new GoalStateSnapshot(
                        result.ShouldSave ? ExportGoalSnapshot(kernel, goalId) : state.Goal, []);
                    return Task.FromResult((result.ShouldSave, (GoalStateSnapshot?)updated,
                        (Outcome: result, Kernel: kernel, MessageId: message?.Id), messages));
                }).GetAwaiter().GetResult();
            committedKernel = committed.Kernel;
            pendingMessageId = committed.MessageId;
            return committed.Outcome;
        }
        catch (GoalTransactionConflictException)
        {
            committedKernel = null;
            return new CliCommandHandlers.GoalLifecycleTransitionOutcome(
                CliCommandHandlers.GoalLifecycleTransitionDisposition.ConflictExhausted, goalId, ObservedStatus: null);
        }
    }
}
