using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static partial class CliPersistentStateRunner
{
    private static bool ExecuteGoalParkTransition(
        IReadOnlyList<string> args,
        ITransactionalOrchestratorStateRepository stateRepository,
        OrchestratorWorkspace workspace,
        ref Goal? currentGoal)
    {
        var command = CliCommandHandlers.PrepareGoalParkCommand(args);
        var goalId = ResolveSingleGoalCommandGoalId(
            stateRepository, currentGoal?.Id.Value, command.GoalSelector);
        var outcome = ExecuteGoalParkApplication(command, goalId, stateRepository, out var pendingMessageId);
        if (outcome.Goal is not null)
        {
            currentGoal = outcome.Goal;
        }

        CliCommandHandlers.RenderGoalParkOutcome(command, outcome, workspace,
            pendingMessageId is null ? null : () => DeliverCommittedGoalParkAttentionResolution(
                (IOrchestratorStateOutboxRepository)stateRepository, workspace, goalId, pendingMessageId));
        return outcome.ShouldSave;
    }

    internal static CliCommandHandlers.GoalLifecycleTransitionOutcome ExecuteGoalParkApplication(
        CliCommandHandlers.GoalParkCommand command,
        GoalId goalId,
        ITransactionalOrchestratorStateRepository stateRepository) =>
        ExecuteGoalParkApplication(command, goalId, stateRepository, out _);

    private static CliCommandHandlers.GoalLifecycleTransitionOutcome ExecuteGoalParkApplication(
        CliCommandHandlers.GoalParkCommand command, GoalId goalId,
        ITransactionalOrchestratorStateRepository stateRepository, out string? pendingMessageId)
    {
        pendingMessageId = null;
        try
        {
            return TransactGoalParkState(stateRepository, goalId,
                kernel => CliCommandHandlers.ApplyGoalParkWithoutRendering(command, kernel, goalId),
                $"Goal parked: {command.Reason}", out pendingMessageId);
        }
        catch (GoalTransactionConflictException)
        {
            return new CliCommandHandlers.GoalLifecycleTransitionOutcome(
                CliCommandHandlers.GoalLifecycleTransitionDisposition.ConflictExhausted,
                goalId,
                ObservedStatus: null);
        }
    }

    private static CliCommandHandlers.GoalLifecycleTransitionOutcome TransactGoalParkState(
        ITransactionalOrchestratorStateRepository repository, GoalId goalId,
        Func<AgentOrchestratorKernel, CliCommandHandlers.GoalLifecycleTransitionOutcome> apply,
        string resolution, out string? pendingMessageId)
    {
        pendingMessageId = null;
        if (repository is not IOrchestratorStateOutboxRepository)
            return repository.TransactGoalStateAsync("cli:park-goal", goalId, (state, _) =>
            {
                if (state is null) throw new KeyNotFoundException($"Goal '{goalId.Value}' was not found.");
                var kernel = new AgentOrchestratorKernel();
                kernel.ReplaceGoalStateWithSnapshot(state.Goal, state.HumanInputRequests);
                var outcome = apply(kernel);
                var updatedState = outcome.ShouldSave ? ExportGoalStateSnapshot(kernel, goalId) : state;
                return Task.FromResult((outcome.ShouldSave, updatedState, outcome));
            }).GetAwaiter().GetResult();

        var committed = repository.TransactGoalStateWithOutboxAsync("cli:park-goal", goalId, (state, _) =>
        {
            if (state is null) throw new KeyNotFoundException($"Goal '{goalId.Value}' was not found.");
            var kernel = new AgentOrchestratorKernel();
            kernel.ReplaceGoalStateWithSnapshot(state.Goal, state.HumanInputRequests);
            var outcome = apply(kernel);
            var updatedState = outcome.ShouldSave ? ExportGoalStateSnapshot(kernel, goalId) : state;
            var message = outcome.ShouldSave ? GoalParkAttentionResolutionOutbox.CreateMessage(
                goalId, resolution, kernel.GetGoal(goalId).Timeline[^1].OccurredAt) : null;
            IReadOnlyList<OrchestratorStateOutboxMessage> messages = message is null ? [] : [message];
            return Task.FromResult((outcome.ShouldSave, (GoalStateSnapshot?)updatedState,
                (Outcome: outcome, MessageId: message?.Id), messages));
        }).GetAwaiter().GetResult();
        pendingMessageId = committed.MessageId;
        return committed.Outcome;
    }
}
