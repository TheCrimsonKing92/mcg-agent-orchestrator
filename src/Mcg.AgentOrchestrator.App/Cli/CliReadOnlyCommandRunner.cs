using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class CliReadOnlyCommandRunner
{
    internal static bool IsReadOnlyCommand(IReadOnlyList<string> args) =>
        CliFailureClustersQueryCommand.IsFailureClustersQueryCommand(args) ||
        CliLaneReuseShadowQueryCommand.IsLaneReuseShadowQueryCommand(args) ||
        CliRemoteExecutorsQueryCommand.IsCommand(args) ||
        CliRoundValueQueryCommand.IsRoundValueQueryCommand(args) ||
        GoalBoardCommand.IsBoardCommand(args) ||
        TrialCompareCliCommand.RequiresHistoricalState(args) ||
        CliTaskQueryCommand.IsTaskQueryCommand(args) ||
        CliTimelineQueryCommand.IsTimelineQueryCommand(args) ||
        CliGoalReportQueryCommand.IsGoalReportQueryCommand(args) ||
        CliSingleGoalReportQueryCommand.IsSingleGoalReportQueryCommand(args) ||
        CliStatusQueryCommand.IsStatusQueryCommand(args) ||
        CliGoalEventsQueryCommand.IsGoalEventsQueryCommand(args) ||
        CliAttentionQueryCommand.IsAttentionQueryCommand(args) ||
        CliNextFullQueryCommand.IsNextFullQueryCommand(args) ||
        CliInspectionQueryCommand.IsInspectionQueryCommand(args) ||
        CliBacklogQueryCommand.IsBacklogQueryCommand(args);

    internal static bool TryExecute(
        IReadOnlyList<string> args,
        ITransactionalOrchestratorStateRepository stateRepository,
        OrchestratorWorkspace workspace,
        IModelProviderRegistry providers,
        IOperatorChannel? channel,
        ref IReadOnlyList<AgentDefinition> agents,
        ref WorkerProfileCatalog workerProfiles,
        ref Goal? currentGoal,
        out bool changed,
        IClock? diagnosticsClock = null)
    {
        if (CliFailureClustersQueryCommand.IsFailureClustersQueryCommand(args)) { CliFailureClustersQueryCommand.Execute(args, workspace); changed = false; return true; }
        if (CliLaneReuseShadowQueryCommand.IsLaneReuseShadowQueryCommand(args)) { CliLaneReuseShadowQueryCommand.Execute(args, workspace); changed = false; return true; }
        if (CliRemoteExecutorsQueryCommand.IsCommand(args)) { CliRemoteExecutorsQueryCommand.Execute(args, workspace); changed = false; return true; }
        if (CliRoundValueQueryCommand.IsRoundValueQueryCommand(args)) { CliRoundValueQueryCommand.Execute(args, stateRepository, workspace); changed = false; return true; }
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

        if (CliTaskQueryCommand.IsTaskQueryCommand(args))
        {
            CliTaskQueryCommand.Execute(
                args,
                stateRepository,
                workspace,
                providers,
                channel,
                ref agents,
                ref workerProfiles,
                ref currentGoal);
            changed = false;
            return true;
        }

        if (CliTimelineQueryCommand.IsTimelineQueryCommand(args))
        {
            CliTimelineQueryCommand.Execute(args, stateRepository, workspace, providers, channel,
                ref agents, ref workerProfiles, ref currentGoal);
            changed = false;
            return true;
        }

        if (CliGoalReportQueryCommand.IsGoalReportQueryCommand(args))
        {
            CliGoalReportQueryCommand.Execute(args, stateRepository, workspace, providers, channel,
                ref agents, ref workerProfiles, ref currentGoal);
            changed = false;
            return true;
        }

        if (CliSingleGoalReportQueryCommand.IsSingleGoalReportQueryCommand(args))
        {
            CliSingleGoalReportQueryCommand.Execute(args, stateRepository, workspace, providers, channel,
                ref agents, ref workerProfiles, ref currentGoal);
            changed = false;
            return true;
        }

        if (CliStatusQueryCommand.IsStatusQueryCommand(args))
        {
            CliStatusQueryCommand.Execute(args, stateRepository, workspace, providers, channel,
                ref agents, ref workerProfiles, ref currentGoal);
            changed = false;
            return true;
        }

        if (CliGoalEventsQueryCommand.IsGoalEventsQueryCommand(args))
        {
            CliGoalEventsQueryCommand.Execute(args, stateRepository, workspace);
            changed = false;
            return true;
        }

        if (CliAttentionQueryCommand.IsAttentionQueryCommand(args) &&
            CliAttentionQueryCommand.TryExecute(args, stateRepository, workspace, providers, channel,
                ref agents, ref workerProfiles, ref currentGoal))
        {
            changed = false;
            return true;
        }

        if (CliNextFullQueryCommand.IsNextFullQueryCommand(args) &&
            CliNextFullQueryCommand.TryExecute(args, stateRepository, workspace, providers, channel,
                ref agents, ref workerProfiles, ref currentGoal, diagnosticsClock))
        {
            changed = false;
            return true;
        }

        if (CliInspectionQueryCommand.IsInspectionQueryCommand(args))
        {
            CliInspectionQueryCommand.Execute(args, stateRepository, workspace, providers, channel,
                ref agents, ref workerProfiles);
            changed = false;
            return true;
        }

        if (CliBacklogQueryCommand.IsBacklogQueryCommand(args))
        {
            CliBacklogQueryCommand.Execute(args, stateRepository, workspace, providers, channel,
                ref agents, ref workerProfiles);
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
