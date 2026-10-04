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
            out var committedKernel, out var pendingMessageId);
        if (outcome.Goal is not null)
        {
            currentGoal = outcome.Goal;
        }

        CliCommandHandlers.RenderGoalAbandonOutcome(command, outcome, committedKernel, workspace, hooks,
            pendingMessageId is null ? null : () => DeliverCommittedGoalLifecycleEvent(
                (IOrchestratorStateOutboxRepository)stateRepository, workspace, goalId, GoalStatus.Cancelled, pendingMessageId));
        return outcome.ShouldSave;
    }

    internal static CliCommandHandlers.GoalLifecycleTransitionOutcome ExecuteGoalAbandonApplication(
        CliCommandHandlers.GoalAbandonCommand command,
        GoalId goalId,
        ITransactionalOrchestratorStateRepository stateRepository,
        OrchestratorWorkspace workspace,
        GoalWorktreeCleanupHooks hooks) =>
        ExecuteGoalAbandonApplication(command, goalId, stateRepository, workspace, hooks, out _, out _);

    private static CliCommandHandlers.GoalLifecycleTransitionOutcome ExecuteGoalAbandonApplication(
        CliCommandHandlers.GoalAbandonCommand command,
        GoalId goalId,
        ITransactionalOrchestratorStateRepository stateRepository,
        OrchestratorWorkspace workspace,
        GoalWorktreeCleanupHooks hooks,
        out AgentOrchestratorKernel? committedKernel,
        out string? pendingMessageId)
    {
        pendingMessageId = null;
        if (!command.Confirmed)
        {
            var snapshot = stateRepository.LoadGoalAsync(goalId).GetAwaiter().GetResult()
                ?? throw new KeyNotFoundException($"Goal '{goalId.Value}' was not found.");
            committedKernel = RestoreGoalExactly(snapshot);
            return CliCommandHandlers.ApplyGoalAbandonWithoutRendering(
                command, committedKernel, goalId, workspace, hooks);
        }

        return TransactGoalLifecycleTransitionWithOutbox(stateRepository, "cli:abandon-goal", goalId,
            kernel => CliCommandHandlers.ApplyGoalAbandonWithoutRendering(command, kernel, goalId, workspace, hooks),
            out committedKernel, out pendingMessageId);
    }
}
