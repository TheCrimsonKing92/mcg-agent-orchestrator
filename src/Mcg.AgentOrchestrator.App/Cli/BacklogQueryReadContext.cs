using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

// One invocation owns both successful and missing lookups. No state survives a CLI call.
internal sealed class BacklogQueryReadContext(IOrchestratorStateQueries queries)
{
    private readonly Dictionary<string, Goal?> goals = new(StringComparer.Ordinal);
    private readonly Dictionary<string, GoalSnapshot> snapshots = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AgentOrchestratorKernel> items = new(StringComparer.Ordinal);

    internal AgentOrchestratorKernel ForItem(string itemId)
    {
        if (items.TryGetValue(itemId, out var kernel)) return kernel;
        var ids = queries.ListBacklogRelatedGoalIdsAsync([itemId]).GetAwaiter().GetResult();
        Load(ids);
        kernel = AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot(
            ids.Where(id => snapshots.ContainsKey(id.Value))
                .Select(id => snapshots[id.Value]).ToArray(), []));
        items.Add(itemId, kernel);
        return kernel;
    }

    internal Goal? FindGoal(string id)
    {
        Load([new GoalId(id)]);
        return goals[id];
    }

    internal IReadOnlyList<SimilarityDocument> SimilarityDocuments() =>
        queries.ListCompletedGoalSimilarityDocumentsAsync().GetAwaiter().GetResult();

    private void Load(IReadOnlyCollection<GoalId> ids)
    {
        var missing = ids.Where(id => !goals.ContainsKey(id.Value)).Distinct().ToArray();
        foreach (var batch in missing.Chunk(500))
        {
            var loaded = queries.LoadGoalsAsync(batch).GetAwaiter().GetResult();
            foreach (var id in batch) goals[id.Value] = null;
            foreach (var goal in loaded.Goals)
            {
                goals[goal.Id.Value] = goal;
                snapshots[goal.Id.Value] = loaded.ExportGoalSnapshot(goal.Id);
            }
        }
    }
}
