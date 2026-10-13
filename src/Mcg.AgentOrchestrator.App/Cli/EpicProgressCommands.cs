using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class EpicProgressCommands
{
    internal static bool ExecutePortfolio(CliExecutionContext context)
    {
        var store = new PortfolioStore(context.Workspace.PortfolioStorePath);
        var rollups = store.BuildEpicRollupsAsync(context.Kernel.Goals).GetAwaiter().GetResult();
        var rows = store.BuildPortfolioGoalRowsAsync(context.Kernel.Goals).GetAwaiter().GetResult();
        PortfolioTextView.PrintPortfolio(rollups, rows,
            EpicProgressReadModel.Load(context.Workspace).ToDictionary(row => row.Epic.Id));
        return false;
    }

    internal static bool ExecuteList(OrchestratorWorkspace workspace, IReadOnlyList<string> parts, string? sinceValue)
    {
        var json = RequireJsonOnce(parts);
        DateTimeOffset? since = null;
        if (parts.Any(part => part.Equals("--since", StringComparison.OrdinalIgnoreCase)
            || part.StartsWith("--since=", StringComparison.OrdinalIgnoreCase)))
        {
            if (!CliSinceArgument.TryParse(sinceValue, DateTimeOffset.UtcNow, out var cutoff))
                throw new ArgumentException(CliCommandHelp.EpicListUsage);
            since = cutoff;
        }
        var rollups = EpicProgressReadModel.Load(workspace, since);
        if (json) { EpicProgressAgentReadView.Print(rollups); return false; }
        PortfolioTextView.PrintEpicRollups(rollups, since,
            rollups.ToDictionary(row => row.Epic.Id, row => EpicPlanNextStep.Summary(row.Plan!.Items)), includeOutcome: true);
        return false;
    }

    internal static bool ExecuteShow(OrchestratorWorkspace workspace, IReadOnlyList<string> parts)
    {
        var json = RequireJsonOnce(parts);
        parts = parts.Where(part => part != "--json").ToArray();
        CliArgumentParser.RequirePartCount(parts, 2, "epic-show <epic>");
        var row = EpicProgressReadModel.LoadEpic(workspace, parts[1]);
        if (json) { EpicProgressAgentReadView.Print([row]); return false; }
        PortfolioTextView.PrintEpicShow(row, EpicPlanNextStep.Summary(row.Plan!.Items), includeOutcome: true);
        return false;
    }

    internal static bool RequireJsonOnce(IReadOnlyList<string> parts)
    {
        var count = parts.Count(part => part == "--json");
        if (count > 1) throw new ArgumentException("--json must appear once.");
        return count == 1;
    }
}
