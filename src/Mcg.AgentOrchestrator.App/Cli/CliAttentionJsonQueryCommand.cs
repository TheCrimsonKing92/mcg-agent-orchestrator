using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class CliAttentionJsonQueryCommand
{
    internal static bool IsAttentionJsonCommand(IReadOnlyList<string> args) =>
        args.Count is 2 or 3 && args[0].Equals("attention", StringComparison.OrdinalIgnoreCase) &&
        (args.Count == 2 && args[1] == "--json" ||
         args.Count == 3 && args[1].Equals("list", StringComparison.OrdinalIgnoreCase) && args[2] == "--json") &&
        !CliCommandHelp.IsCommandSpecificHelp(args);

    internal static void Execute(IReadOnlyList<string> args, IOrchestratorStateQueries stateQueries,
        OrchestratorWorkspace workspace, TimeProvider? time = null)
    {
        var metadata = stateQueries.ListGoalMetadataAsync().GetAwaiter().GetResult();
        var ids = metadata.Select(goal => new GoalId(goal.Id)).ToArray();
        var kernel = ids.Length == 0
            ? new AgentOrchestratorKernel()
            : stateQueries.LoadGoalsAsync(ids).GetAwaiter().GetResult();
        var itemsDb = Path.Combine(workspace.OrchestratorDirectory, "collaboration-items.db");
        IReadOnlyList<CollaborationItem> items = File.Exists(itemsDb)
            ? CollaborationItemStore.OpenExisting(workspace.OrchestratorDirectory)
                .GetAttentionQueueAsync().GetAwaiter().GetResult()
            : [];
        var waits = CliCommandHandlers.HumanWaitsForAttention(kernel, null, includeHistory: false);
        items = CliCommandHandlers.CollaborationItemsForAttention(items, kernel, null, includeHistory: false);
        Console.WriteLine(AttentionAgentReadView.Render(waits, items, time,
            ProviderUsageHoldView.Read(kernel.Goals, (time ?? TimeProvider.System).GetUtcNow()), kernel.Goals));
    }
}
