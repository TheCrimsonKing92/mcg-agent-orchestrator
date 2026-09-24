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
        var outcome = ExecuteGoalParkApplication(command, goalId, stateRepository);
        if (outcome.Goal is not null)
        {
            currentGoal = outcome.Goal;
        }

        CliCommandHandlers.RenderGoalParkOutcome(command, outcome, workspace);
        return outcome.ShouldSave;
    }

    internal static CliCommandHandlers.GoalLifecycleTransitionOutcome ExecuteGoalParkApplication(
        CliCommandHandlers.GoalParkCommand command,
        GoalId goalId,
        ITransactionalOrchestratorStateRepository stateRepository)
    {
        try
        {
            return stateRepository.TransactGoalStateAsync(
                    "cli:park-goal",
                    goalId,
                    (state, _) =>
                    {
                        if (state is null)
                        {
                            throw new KeyNotFoundException($"Goal '{goalId.Value}' was not found.");
                        }

                        var kernel = new AgentOrchestratorKernel();
                        kernel.ReplaceGoalStateWithSnapshot(state.Goal, state.HumanInputRequests);
                        var outcome = CliCommandHandlers.ApplyGoalParkWithoutRendering(command, kernel, goalId);
                        var updatedState = outcome.ShouldSave
                            ? ExportGoalStateSnapshot(kernel, goalId)
                            : state;
                        return Task.FromResult((outcome.ShouldSave, updatedState, outcome));
                    })
                .GetAwaiter()
                .GetResult();
        }
        catch (GoalTransactionConflictException)
        {
            return new CliCommandHandlers.GoalLifecycleTransitionOutcome(
                CliCommandHandlers.GoalLifecycleTransitionDisposition.ConflictExhausted,
                goalId,
                ObservedStatus: null);
        }
    }
}
