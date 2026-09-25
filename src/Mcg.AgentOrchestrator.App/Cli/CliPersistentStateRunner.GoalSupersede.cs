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
        var outcome = ExecuteGoalSupersedeApplication(command, goalId, currentGoalId, stateRepository);
        if (outcome.Goal is not null)
        {
            currentGoal = outcome.Goal;
        }

        CliCommandHandlers.RenderGoalSupersedeOutcome(outcome, workspace);
        return outcome.ShouldSave;
    }

    internal static CliCommandHandlers.GoalLifecycleTransitionOutcome ExecuteGoalSupersedeApplication(
        CliCommandHandlers.GoalSupersedeCommand command,
        GoalId goalId,
        GoalId? currentGoalId,
        ITransactionalOrchestratorStateRepository stateRepository) =>
        TransactGoalSnapshotTransition(stateRepository, "cli:supersede-goal", goalId,
            kernel => CliCommandHandlers.ApplyGoalSupersedeWithoutRendering(command, kernel, goalId, currentGoalId),
            out _);
}
