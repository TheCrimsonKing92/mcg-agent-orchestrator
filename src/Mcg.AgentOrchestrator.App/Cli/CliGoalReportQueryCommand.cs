using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class CliGoalReportQueryCommand
{
    internal static bool IsGoalReportQueryCommand(IReadOnlyList<string> args)
    {
        if (args.Count < 2 || CliCommandHelp.IsCommandSpecificHelp(args) || !IsPrefix(args[1]))
            return false;

        return args[0].ToLowerInvariant() switch
        {
            "evidence" or "stages" or "gates" or "verify-needed" or "input-needed" => args.Count == 2,
            "revise" => args.Count == 3 && args[2].Equals("--history", StringComparison.OrdinalIgnoreCase),
            _ => false
        };
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
        CliCommandHelp.ThrowIfInvalidFlags(args);
        var prefix = args[1];
        var matches = stateQueries.ListGoalMetadataAsync().GetAwaiter().GetResult()
            .Where(goal => goal.Id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var goalId = matches.Length switch
        {
            1 => new GoalId(matches[0].Id),
            0 => throw new KeyNotFoundException($"Goal '{prefix}' was not found."),
            _ => throw new InvalidOperationException($"Goal prefix '{prefix}' is ambiguous.")
        };
        var kernel = stateQueries.LoadGoalsAsync([goalId]).GetAwaiter().GetResult();
        currentGoal = kernel.Goals.SingleOrDefault(goal => goal.Id == goalId)
            ?? throw new KeyNotFoundException($"Goal '{goalId.Value}' was not found.");
        CliReadOnlyQueryExecutor.Execute(args, kernel, workspace, providers, channel,
            ref agents, ref workerProfiles, ref currentGoal);
    }

    private static bool IsPrefix(string value) =>
        !string.IsNullOrWhiteSpace(value) && !value.StartsWith("-", StringComparison.Ordinal) &&
        !value.Equals("help", StringComparison.OrdinalIgnoreCase);
}
