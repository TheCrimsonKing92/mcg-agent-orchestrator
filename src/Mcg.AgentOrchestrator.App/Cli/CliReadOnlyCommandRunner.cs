using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class CliReadOnlyCommandRunner
{
    internal static bool TryExecute(
        IReadOnlyList<string> args,
        ITransactionalOrchestratorStateRepository stateRepository,
        OrchestratorWorkspace workspace,
        IModelProviderRegistry providers,
        IOperatorChannel? channel,
        ref IReadOnlyList<AgentDefinition> agents,
        ref WorkerProfileCatalog workerProfiles,
        ref Goal? currentGoal,
        out bool changed)
    {
        if (GoalBoardCommand.IsBoardCommand(args))
        {
            GoalBoardCommand.Run(args, stateRepository, workspace);
            changed = false;
            return true;
        }

        if (TrialCompareCliCommand.RequiresHistoricalState(args))
        {
            changed = ExecuteHistoricalTrialCompare(
                args,
                stateRepository,
                workspace,
                ref agents,
                providers,
                ref workerProfiles,
                channel);
            return true;
        }

        if (CliTaskQueryCommand.IsTaskQueryCommand(args) &&
            CliTaskQueryCommand.TryExecute(
                args,
                stateRepository,
                workspace,
                providers,
                channel,
                ref agents,
                ref workerProfiles,
                ref currentGoal))
        {
            changed = false;
            return true;
        }

        changed = false;
        return false;
    }

    internal static bool ExecuteHistoricalTrialCompare(
        IReadOnlyList<string> args,
        IOrchestratorStateRepository stateRepository,
        OrchestratorWorkspace workspace,
        ref IReadOnlyList<AgentDefinition> agents,
        IModelProviderRegistry providers,
        ref WorkerProfileCatalog workerProfiles,
        IOperatorChannel? channel = null)
    {
        var commandKernel = new AgentOrchestratorKernel();
        Goal? commandCurrentGoal = null;
        var changed = CliCommandDispatcher.ExecuteCommand(
            args,
            commandKernel,
            workspace,
            ref agents,
            providers,
            ref workerProfiles,
            ref commandCurrentGoal,
            channel,
            reloadKernelForGoals: requestedGoalIds => stateRepository.LoadGoalsAsync(
                requestedGoalIds.Select(goalId => new GoalId(goalId)).ToArray()).GetAwaiter().GetResult());
        if (changed)
        {
            throw new InvalidOperationException("Historical trial comparison attempted to mutate orchestrator state through its read-only route.");
        }

        return false;
    }
}
