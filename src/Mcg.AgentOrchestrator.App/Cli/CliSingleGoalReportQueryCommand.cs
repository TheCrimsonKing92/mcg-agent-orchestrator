using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class CliSingleGoalReportQueryCommand
{
    internal static bool IsSingleGoalReportQueryCommand(IReadOnlyList<string> args) =>
        (args.Count > 0 &&
        args[0].ToLowerInvariant() is "subscription-plan" or "goal-changes" &&
        !CliCommandHelp.IsCommandSpecificHelp(args) &&
        !string.IsNullOrWhiteSpace(CliSingleGoalReportSelector.ResolveGoalPrefix(args, GetOptionalArgument))) ||
        (args.Count == 2 &&
        (args[0].Equals("retention-plan", StringComparison.OrdinalIgnoreCase) ||
         args[0].Equals("supervisor", StringComparison.OrdinalIgnoreCase) ||
         args[0].Equals("goal-timing", StringComparison.OrdinalIgnoreCase) ||
         args[0].Equals("failure-triage", StringComparison.OrdinalIgnoreCase)) &&
        !string.IsNullOrWhiteSpace(args[1]) &&
        !args[1].StartsWith('-') &&
        !CliCommandHelp.IsCommandSpecificHelp(args));

    internal static void Execute(
        IReadOnlyList<string> args,
        IOrchestratorStateQueries stateQueries,
        OrchestratorWorkspace workspace,
        IModelProviderRegistry providers,
        IOperatorChannel? channel,
        ref IReadOnlyList<AgentDefinition> agents,
        ref WorkerProfileCatalog workerProfiles,
        ref Goal? currentGoal,
        IClock? diagnosticsClock = null)
    {
        var prefix = CliSingleGoalReportSelector.ResolveGoalPrefix(args, GetOptionalArgument)!;
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
            ref agents, ref workerProfiles, ref currentGoal, diagnosticsClock);
    }

    // Keep prefix parsing identical to CliPersistentStateRunner.GetOptionalArgument/IsCliValueFlag.
    private static string? GetOptionalArgument(IReadOnlyList<string> parts, params string[] flags)
    {
        for (var i = 1; i < parts.Count; i++)
        {
            var part = parts[i];
            if (flags.Contains(part, StringComparer.OrdinalIgnoreCase))
            {
                if (IsCliValueFlag(part))
                {
                    i++;
                }

                continue;
            }

            if (IsCliValueFlag(part))
            {
                i++;
                continue;
            }

            if (part.StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            return part;
        }

        return null;
    }

    private static bool IsCliValueFlag(string value) =>
        value.Equals("--goal", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("--backlog-coverage", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("--backlog-item", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("--pipeline", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("--role", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("--task", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("--autonomy", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("--policy", StringComparison.OrdinalIgnoreCase);
}
