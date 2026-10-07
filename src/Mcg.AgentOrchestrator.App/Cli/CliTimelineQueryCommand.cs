using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class CliTimelineQueryCommand
{
    internal static bool IsTimelineQueryCommand(IReadOnlyList<string> args)
    {
        if (args.Count < 2 || CliCommandHelp.IsCommandSpecificHelp(args))
            return false;

        if (args[0].Equals("timeline", StringComparison.OrdinalIgnoreCase))
            return args.Count == 2 && IsPrefix(args[1]);

        if (!args[0].Equals("task-timeline", StringComparison.OrdinalIgnoreCase))
            return false;

        return (args.Count == 4 &&
                args[1].Equals("--goal", StringComparison.OrdinalIgnoreCase) && IsPrefix(args[2])) ||
               (args.Count == 3 && IsPrefix(args[1]) && !int.TryParse(args[1], out _) &&
                !args[2].StartsWith("--", StringComparison.Ordinal));
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
        // Help shapes are declined by the classifier, before validation or repository reads.
        CliCommandHelp.ThrowIfInvalidFlags(args);
        var prefix = args[1].Equals("--goal", StringComparison.OrdinalIgnoreCase) ? args[2] : args[1];
        var matches = stateQueries.ListGoalIdStatusesAsync().GetAwaiter().GetResult()
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
        !string.IsNullOrWhiteSpace(value) && !value.StartsWith("--", StringComparison.Ordinal);
}
