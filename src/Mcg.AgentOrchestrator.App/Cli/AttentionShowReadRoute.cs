using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class AttentionShowReadRoute
{
    internal static bool? TryExecute(
        IReadOnlyList<string> args,
        IOrchestratorStateQueries stateQueries,
        OrchestratorWorkspace workspace,
        IModelProviderRegistry providers,
        IOperatorChannel? channel,
        ref IReadOnlyList<AgentDefinition> agents,
        ref WorkerProfileCatalog workerProfiles,
        ref Goal? currentGoal)
    {
        if (args.Count < 2 || !args[0].Equals("attention", StringComparison.OrdinalIgnoreCase) ||
            !args[1].Equals("show", StringComparison.OrdinalIgnoreCase))
            return null;

        string? prefix;
        if (args.Count == 2)
            prefix = null;
        else if (args.Count == 3)
            prefix = args[2];
        else if (args.Count == 4 && args[2].Equals("--goal", StringComparison.OrdinalIgnoreCase))
            prefix = args[3];
        else
            return null;
        if (args.Count > 2 && (string.IsNullOrWhiteSpace(prefix) || prefix.StartsWith("--", StringComparison.Ordinal)))
            return null;

        var metadata = stateQueries.ListGoalMetadataAsync().GetAwaiter().GetResult();
        var matchedIds = prefix is null ? null : metadata
            .Where(goal => goal.Id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(goal => new GoalId(goal.Id)).ToArray();
        IReadOnlyList<HumanInputRequestSnapshot> openWaits;
        try
        {
            openWaits = stateQueries.ListOpenHumanInputRequestsAsync().GetAwaiter().GetResult();
        }
        catch (NotSupportedException)
        {
            return null;
        }

        var store = CollaborationItemStore.OpenExisting(workspace.OrchestratorDirectory);
        var storedItems = store.ListAsync().GetAwaiter().GetResult();
        var attentionQueue = store.GetAttentionQueueAsync().GetAwaiter().GetResult();
        var projection = stateQueries.ReadAttentionProjectionAsync().GetAwaiter().GetResult();
        var dutyIds = AttentionReadGoalSelection.ReadPostLandingDutyGoalIds(stateQueries, metadata, projection);
        if (dutyIds is null)
            return false;
        var selection = AttentionReadGoalSelection.Select(metadata, openWaits, storedItems, attentionQueue, dutyIds);
        if (selection.Decline)
            return false;

        IReadOnlyList<GoalId> obligationIds;
        try
        {
            obligationIds = stateQueries.ListGateTestObligationGoalIdsAsync(matchedIds).GetAwaiter().GetResult();
        }
        catch (NotSupportedException)
        {
            return null;
        }

        var ids = matchedIds ?? selection.GoalIds.Concat(obligationIds).Distinct().ToArray();
        var loaded = ids.Length == 0
            ? new AgentOrchestratorKernel()
            : stateQueries.LoadGoalsAsync(ids).GetAwaiter().GetResult();
        var obligationSet = obligationIds.ToHashSet();
        if (loaded.Goals.Count != ids.Length || loaded.Goals.Any(goal => goal.Status == GoalStatus.Parked) ||
            loaded.Goals.Any(goal => goal.GateTestObligations.Count > 0 && !obligationSet.Contains(goal.Id)))
            return false;

        // Selective loading can use id order; evidence blocks must retain the hot-row scan order.
        var loadedIds = loaded.Goals.Select(goal => goal.Id).ToHashSet();
        var orderedIds = obligationIds.Where(loadedIds.Contains)
            .Concat(loaded.Goals.Select(goal => goal.Id).Where(id => !obligationSet.Contains(id)));
        var kernel = AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot(
            orderedIds.Select(loaded.ExportGoalSnapshot).ToArray(),
            loaded.ExportSnapshot().HumanInputRequests));
        CliReadOnlyQueryExecutor.Execute(args, kernel, workspace, providers, channel,
            ref agents, ref workerProfiles, ref currentGoal);
        return true;
    }
}
