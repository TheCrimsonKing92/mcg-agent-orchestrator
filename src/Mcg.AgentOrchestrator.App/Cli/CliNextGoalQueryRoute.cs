using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class CliNextGoalQueryRoute
{
    private const string SweepBlockerPrefix = "terminal-sweep-blocker:";

    internal static bool TryExecute(
        IReadOnlyList<string> args,
        string goalPrefix,
        IOrchestratorStateQueries stateQueries,
        OrchestratorWorkspace workspace,
        IModelProviderRegistry providers,
        IOperatorChannel? channel,
        ref IReadOnlyList<AgentDefinition> agents,
        ref WorkerProfileCatalog workerProfiles,
        ref Goal? currentGoal,
        IClock? diagnosticsClock = null)
    {
        var itemsDb = Path.Combine(workspace.OrchestratorDirectory, "collaboration-items.db");
        if (!File.Exists(itemsDb))
            return false;

        var ids = stateQueries.ListGoalMetadataAsync().GetAwaiter().GetResult()
            .Where(goal => goal.Id.StartsWith(goalPrefix, StringComparison.OrdinalIgnoreCase))
            .Select(goal => new GoalId(goal.Id)).ToArray();
        if (ids.Length != 1)
        {
            // Let the handler produce the existing unknown or ambiguous goal error.
            var matches = ids.Length == 0
                ? new AgentOrchestratorKernel()
                : stateQueries.LoadGoalsAsync(ids).GetAwaiter().GetResult();
            CliReadOnlyQueryExecutor.Execute(args, matches, workspace, providers, channel,
                ref agents, ref workerProfiles, ref currentGoal, diagnosticsClock);
            return true;
        }

        var kernel = stateQueries.LoadGoalsAsync(ids).GetAwaiter().GetResult();
        var goal = kernel.Goals.SingleOrDefault(candidate => candidate.Id == ids[0]);
        if (goal is null)
            return false;

        var diagnosis = TerminalGoalSweep.Diagnose(kernel, workspace.ExecutionDirectory, workspace.IntegrationBranch, goal.Id);
        if (diagnosis.Goals.Any(result => result.Blockers.Any(blocker =>
                !string.IsNullOrWhiteSpace(blocker.Command) &&
                !blocker.Command.Equals("excluded", StringComparison.OrdinalIgnoreCase))))
            return false;

        var items = CollaborationItemStore.OpenExisting(workspace.OrchestratorDirectory)
            .ListForGoalIdsAsync([goal.Id.Value]).GetAwaiter().GetResult();
        if (items.Any(item => item.CorrelationKey?.StartsWith(SweepBlockerPrefix, StringComparison.Ordinal) == true))
            return false;

        CliReadOnlyQueryExecutor.Execute(args, kernel, workspace, providers, channel,
            ref agents, ref workerProfiles, ref currentGoal, diagnosticsClock);
        return true;
    }
}
