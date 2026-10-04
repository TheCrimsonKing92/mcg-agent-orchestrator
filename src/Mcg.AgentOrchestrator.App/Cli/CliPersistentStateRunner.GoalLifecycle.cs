using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static partial class CliPersistentStateRunner
{
    private static readonly HashSet<string> GoalScopedLifecycleVerbs =
        new(StringComparer.OrdinalIgnoreCase) { "park-goal", "unpark-goal", "abandon-goal", "cancel-goal", "supersede-goal" };

    private static bool IsGoalScopedLifecycleInvocation(IReadOnlyList<string> args) =>
        GoalScopedLifecycleVerbs.Contains(args[0]) || CliCommandHandlers.IsStopSupersedeAlias(args) ||
        CliCommandHandlers.IsStopCancelAlias(args);

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

        if (args[0].Equals("abandon-goal", StringComparison.OrdinalIgnoreCase))
        {
            CliCommandHelp.ThrowIfInvalidFlags(args);
            return ExecuteGoalAbandonTransition(args, stateRepository, workspace, ref currentGoal);
        }

        if (args[0].Equals("cancel-goal", StringComparison.OrdinalIgnoreCase) ||
            CliCommandHandlers.IsStopCancelAlias(args))
        {
            CliCommandHelp.ThrowIfInvalidFlags(args);
            var cancelArgs = CliCommandHandlers.IsStopCancelAlias(args)
                ? CliCommandHandlers.PrepareGoalCancelCommandFromStopAlias(args).Parts
                : args;
            return ExecuteGoalCancelTransition(cancelArgs, stateRepository, workspace, ref currentGoal);
        }

        if (args[0].Equals("supersede-goal", StringComparison.OrdinalIgnoreCase) ||
            CliCommandHandlers.IsStopSupersedeAlias(args))
        {
            CliCommandHelp.ThrowIfInvalidFlags(args);
            return ExecuteGoalSupersedeTransition(args, stateRepository, workspace, ref currentGoal);
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

        return TransactGoalSnapshotTransition(
            stateRepository, "cli:unpark-goal", goalId,
            kernel => CliCommandHandlers.ApplyGoalUnparkWithoutRendering(command, kernel, goalId),
            out _);
    }

    private static CliCommandHandlers.GoalLifecycleTransitionOutcome TransactGoalSnapshotTransition(
        ITransactionalOrchestratorStateRepository stateRepository,
        string operationName,
        GoalId goalId,
        Func<AgentOrchestratorKernel, CliCommandHandlers.GoalLifecycleTransitionOutcome> apply,
        out AgentOrchestratorKernel? committedKernel)
    {
        AgentOrchestratorKernel? appliedKernel = null;
        try
        {
            var outcome = stateRepository.TransactGoalAsync(
                    operationName, goalId,
                    (snapshot, _) =>
                    {
                        if (snapshot is null)
                        {
                            throw new KeyNotFoundException($"Goal '{goalId.Value}' was not found.");
                        }

                        var kernel = RestoreGoalExactly(snapshot);
                        var result = apply(kernel);
                        appliedKernel = kernel;
                        var updated = result.ShouldSave ? ExportGoalSnapshot(kernel, goalId) : snapshot;
                        return Task.FromResult((result.ShouldSave, updated, result));
                    })
                .GetAwaiter().GetResult();
            committedKernel = appliedKernel;
            return outcome;
        }
        catch (GoalTransactionConflictException)
        {
            committedKernel = null;
            return new CliCommandHandlers.GoalLifecycleTransitionOutcome(
                CliCommandHandlers.GoalLifecycleTransitionDisposition.ConflictExhausted,
                goalId, ObservedStatus: null);
        }
    }

    private static AgentOrchestratorKernel RestoreGoalExactly(GoalSnapshot snapshot)
    {
        var kernel = new AgentOrchestratorKernel();
        kernel.ReplaceGoalWithSnapshot(snapshot);
        return kernel;
    }
}
