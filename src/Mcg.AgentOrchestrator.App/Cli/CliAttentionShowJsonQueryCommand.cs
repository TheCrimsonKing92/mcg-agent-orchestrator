using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class CliAttentionShowJsonQueryCommand
{
    internal static bool IsAttentionShowJsonCommand(IReadOnlyList<string> args) =>
        args.Count is 3 or 4 or 5 &&
        args[0].Equals("attention", StringComparison.OrdinalIgnoreCase) &&
        args[1].Equals("show", StringComparison.OrdinalIgnoreCase) && args[^1] == "--json" &&
        (args.Count == 3 ||
         args.Count == 4 && !args[2].StartsWith("--", StringComparison.Ordinal) ||
         args.Count == 5 && args[2].Equals("--goal", StringComparison.OrdinalIgnoreCase) &&
         !args[3].StartsWith("--", StringComparison.Ordinal)) &&
        !CliCommandHelp.IsCommandSpecificHelp(args);

    internal static void Execute(IReadOnlyList<string> args, IOrchestratorStateQueries stateQueries,
        OrchestratorWorkspace workspace, TimeProvider? time = null)
    {
        var metadata = stateQueries.ListGoalMetadataAsync().GetAwaiter().GetResult();
        var ids = metadata.Select(goal => new GoalId(goal.Id)).ToArray();
        var kernel = ids.Length == 0
            ? new AgentOrchestratorKernel()
            : stateQueries.LoadGoalsAsync(ids).GetAwaiter().GetResult();
        var prefix = args.Count == 3 ? null : args[^2];
        var goal = prefix is null ? null : CliCommandHandlers.ResolveAttentionGoal(kernel, prefix);
        var itemsDb = Path.Combine(workspace.OrchestratorDirectory, "collaboration-items.db");
        IReadOnlyList<CollaborationItem> items = [];
        IReadOnlyList<CollaborationItem> identityUniverse = [];
        if (File.Exists(itemsDb))
        {
            var store = CollaborationItemStore.OpenExisting(workspace.OrchestratorDirectory);
            items = store.GetAttentionQueueAsync().GetAwaiter().GetResult();
            identityUniverse = goal is null
                ? CliCommandHandlers.AllClarifications(store)
                : CliCommandHandlers.AllClarificationsForGoal(store, goal);
        }
        var waits = CliCommandHandlers.HumanWaitsForAttention(kernel, goal?.Id, includeHistory: false);
        items = CliCommandHandlers.CollaborationItemsForAttention(items, kernel, goal?.Id, includeHistory: false);
        Console.WriteLine(AttentionShowAgentReadView.Render(goal?.Id.Value, waits, items,
            item => CliCommandHandlers.IsSpecClarification(item)
                ? CliCommandHandlers.ClarificationId(item, identityUniverse) : null, time, kernel.Goals));
    }
}
