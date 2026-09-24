using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static partial class CliPersistentStateRunner
{
    private static bool ExecuteGoalLifecycleDispositionCommand(
        IReadOnlyList<string> args,
        ITransactionalOrchestratorStateRepository stateRepository,
        OrchestratorWorkspace workspace,
        ref IReadOnlyList<AgentDefinition> agents,
        IModelProviderRegistry providers,
        ref WorkerProfileCatalog workerProfiles,
        ref Goal? currentGoal,
        IOperatorChannel? channel = null)
    {
        if (args[0].Equals("park-goal", StringComparison.OrdinalIgnoreCase))
        {
            CliCommandHelp.ThrowIfInvalidFlags(args);
            return ExecuteGoalParkTransition(args, stateRepository, workspace, ref currentGoal);
        }

        if (!args[0].Equals("unpark-goal", StringComparison.OrdinalIgnoreCase))
        {
            return ExecuteCommandWithoutTransaction(
                args,
                stateRepository,
                workspace,
                ref agents,
                providers,
                ref workerProfiles,
                ref currentGoal,
                channel);
        }

        CliCommandHelp.ThrowIfInvalidFlags(args);
        return ExecuteGoalUnparkTransition(args, stateRepository, workspace, ref currentGoal);
    }

    private static bool ExecuteGoalUnparkTransition(
        IReadOnlyList<string> args,
        ITransactionalOrchestratorStateRepository stateRepository,
        OrchestratorWorkspace workspace,
        ref Goal? currentGoal)
    {
        var command = CliCommandHandlers.PrepareGoalUnparkCommand(args);
        var goalId = ResolveSingleGoalCommandGoalId(
            stateRepository,
            currentGoal?.Id.Value,
            command.GoalSelector);
        var outcome = ExecuteGoalUnparkApplication(command, goalId, stateRepository);
        if (outcome.Goal is not null)
        {
            currentGoal = outcome.Goal;
        }

        CliCommandHandlers.RenderGoalUnparkOutcome(command, outcome, workspace);
        return outcome.ShouldSave;
    }

    internal static CliCommandHandlers.GoalLifecycleTransitionOutcome ExecuteGoalUnparkApplication(
        CliCommandHandlers.GoalUnparkCommand command,
        GoalId goalId,
        ITransactionalOrchestratorStateRepository stateRepository)
    {
        if (!command.Confirmed)
        {
            var snapshot = stateRepository.LoadGoalAsync(goalId).GetAwaiter().GetResult()
                ?? throw new KeyNotFoundException($"Goal '{goalId.Value}' was not found.");
            var kernel = RestoreGoalExactly(snapshot);
            return CliCommandHandlers.ApplyGoalUnparkWithoutRendering(command, kernel, goalId);
        }

        try
        {
            return stateRepository.TransactGoalAsync(
                    "cli:unpark-goal",
                    goalId,
                    (snapshot, _) =>
                    {
                        if (snapshot is null)
                        {
                            throw new KeyNotFoundException($"Goal '{goalId.Value}' was not found.");
                        }

                        var kernel = RestoreGoalExactly(snapshot);
                        var applicationOutcome = CliCommandHandlers.ApplyGoalUnparkWithoutRendering(command, kernel, goalId);
                        var updatedSnapshot = applicationOutcome.ShouldSave
                            ? ExportGoalSnapshot(kernel, goalId)
                            : snapshot;
                        return Task.FromResult((applicationOutcome.ShouldSave, updatedSnapshot, applicationOutcome));
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

    private static AgentOrchestratorKernel RestoreGoalExactly(GoalSnapshot snapshot)
    {
        var kernel = new AgentOrchestratorKernel();
        kernel.ReplaceGoalWithSnapshot(snapshot);
        return kernel;
    }
}
