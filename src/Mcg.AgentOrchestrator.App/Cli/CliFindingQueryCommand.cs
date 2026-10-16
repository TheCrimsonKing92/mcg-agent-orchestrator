using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Mcg.AgentOrchestrator.App.Orchestration;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class CliFindingQueryCommand
{
    internal const string Usage = "finding [--goal <goal-prefix>] <stable-id-or-prefix>";

    internal static bool IsFindingQueryCommand(IReadOnlyList<string> args) =>
        args.Count > 0 && args[0].Equals("finding", StringComparison.OrdinalIgnoreCase);

    internal static (string Query, string? GoalPrefix) Parse(IReadOnlyList<string> args)
    {
        if (args.Count == 2 && !string.IsNullOrWhiteSpace(args[1]) && !args[1].StartsWith('-'))
            return (args[1], null);
        if (args.Count == 4 && args[1].Equals("--goal", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(args[2]) && !args[2].StartsWith('-') &&
            !string.IsNullOrWhiteSpace(args[3]) && !args[3].StartsWith('-'))
            return (args[3], args[2]);
        throw new ArgumentException($"Usage: {Usage}");
    }

    internal static void Execute(IReadOnlyList<string> args, AgentOrchestratorKernel kernel)
    {
        var (query, goalPrefix) = Parse(args);
        IEnumerable<Goal> goals = goalPrefix is null
            ? kernel.Goals
            : [OrchestratorEntityResolver.ResolveGoal(kernel, null, goalPrefix)];
        var result = FindingLookup.Find(goals, query);
        if (result.Matches.Count == 0)
            throw new KeyNotFoundException($"No finding matches {query}.");
        FindingLookupTextView.PrintFindingLookup(result);
    }

    internal static void Execute(
        IReadOnlyList<string> args,
        IOrchestratorStateQueries stateQueries,
        OrchestratorWorkspace workspace,
        IModelProviderRegistry providers,
        IOperatorChannel? channel,
        ref IReadOnlyList<AgentDefinition> agents,
        ref WorkerProfileCatalog workerProfiles,
        ref Goal? currentGoal)
    {
        var kernel = new AgentOrchestratorKernel();
        if (!CliCommandHelp.IsCommandSpecificHelp(args))
        {
            CliCommandHelp.ThrowIfInvalidFlags(args);
            var (_, goalPrefix) = Parse(args);
            var metadata = stateQueries.ListGoalMetadataAsync(includeTerminalCreatedAt: true).GetAwaiter().GetResult();
            var ids = metadata.Where(goal => goalPrefix is null ||
                    goal.Id.StartsWith(goalPrefix, StringComparison.OrdinalIgnoreCase))
                .Select(goal => new GoalId(goal.Id)).ToArray();
            if (ids.Length > 0)
                kernel = stateQueries.LoadGoalsAsync(ids).GetAwaiter().GetResult();
        }
        var commandCurrentGoal = currentGoal;
        var changed = CliCommandDispatcher.ExecuteCommand(args, kernel, workspace, ref agents,
            providers, ref workerProfiles, ref commandCurrentGoal, channel);
        if (changed)
            throw new InvalidOperationException("Finding query attempted to mutate orchestrator state through its read-only route.");
    }
}
