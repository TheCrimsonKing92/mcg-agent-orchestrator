using Mcg.AgentOrchestrator.App.Orchestration;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class EpicProgressCommands
{
    internal static bool ExecuteList(OrchestratorWorkspace workspace, IReadOnlyList<string> parts, string? sinceValue)
    {
        DateTimeOffset? since = null;
        if (parts.Any(part => part.Equals("--since", StringComparison.OrdinalIgnoreCase)
            || part.StartsWith("--since=", StringComparison.OrdinalIgnoreCase)))
        {
            if (!CliSinceArgument.TryParse(sinceValue, DateTimeOffset.UtcNow, out var cutoff))
                throw new ArgumentException(CliCommandHelp.EpicListUsage);
            since = cutoff;
        }
        var rollups = EpicProgressReadModel.Load(workspace, since);
        var plans = EpicPlanStatusReader.Load(workspace, rollups.Select(row => row.Epic).ToArray());
        ConsoleViews.PrintEpicRollups(rollups, since,
            plans.ToDictionary(pair => pair.Key, pair => EpicPlanNextStep.Summary(pair.Value.Items)));
        return false;
    }

    internal static bool ExecuteShow(OrchestratorWorkspace workspace, IReadOnlyList<string> parts)
    {
        CliArgumentParser.RequirePartCount(parts, 2, "epic-show <epic>");
        var row = EpicProgressReadModel.LoadEpic(workspace, parts[1]);
        var plan = EpicPlanStatusReader.Load(workspace, [row.Epic])[row.Epic.Id];
        ConsoleViews.PrintEpicShow(row, EpicPlanNextStep.Summary(plan.Items));
        return false;
    }
}
