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
        var outcome = ExecuteGoalCancelApplication(command, goalId, currentGoalId, stateRepository);
        if (outcome.Goal is not null)
        {
            currentGoal = outcome.Goal;
        }

        CliCommandHandlers.RenderGoalCancelOutcome(outcome, workspace);
        return outcome.ShouldSave;
    }

    internal static CliCommandHandlers.GoalLifecycleTransitionOutcome ExecuteGoalCancelApplication(
        CliCommandHandlers.GoalCancelCommand command,
        GoalId goalId,
        GoalId? currentGoalId,
        ITransactionalOrchestratorStateRepository stateRepository) =>
        TransactGoalSnapshotTransition(stateRepository, "cli:cancel-goal", goalId,
            kernel => CliCommandHandlers.ApplyGoalCancelWithoutRendering(command, kernel, goalId, currentGoalId),
            out _);
}
