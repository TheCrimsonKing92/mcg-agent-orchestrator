using System.Globalization;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class CliRoundValueQueryCommand
{
    internal static bool IsRoundValueQueryCommand(IReadOnlyList<string> args) =>
        args.Count > 0 && args[0].Equals("round-value", StringComparison.OrdinalIgnoreCase) &&
        !CliCommandHelp.IsCommandSpecificHelp(args);

    internal static void Execute(IReadOnlyList<string> args, IOrchestratorStateQueries queries,
        OrchestratorWorkspace workspace)
    {
        DateTimeOffset? since = null;
        var until = DateTimeOffset.UtcNow;
        var json = false;
        for (var i = 1; i < args.Count; i++)
        {
            if (args[i] == "--json") { json = true; continue; }
            if (args[i] is not ("--since" or "--until") || i + 1 == args.Count) { Invalid(); return; }
            var flag = args[i++];
            if (!TryTimestamp(args[i], out var at)) { Invalid(); return; }
            if (flag == "--since") since = at; else until = at;
        }
        since ??= until.AddDays(-7);
        if (since >= until) { Invalid(); return; }
        var metadata = queries.ListGoalMetadataAsync().GetAwaiter().GetResult();
        var goals = metadata.Count == 0 ? [] : queries.LoadGoalsAsync(
            metadata.Select(g => new GoalId(g.Id)).ToArray()).GetAwaiter().GetResult().Goals;
        var report = RoundValueReport.Build(goals, since.Value, until,
            CliOwnerDigestRetryIntents.Read(workspace, until));
        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                report.Since, report.Until,
                Days = report.Days.Select(d => new
                {
                    Day = d.Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    d.Totals.LandedGoals, d.Totals.LostGoals, d.Totals.Rounds, d.Totals.Productive,
                    d.Totals.ExpectedOverhead, d.Totals.Wasted, d.Totals.RoundsPerLanding, d.Totals.WasteShare,
                    d.Totals.InputTokens, d.Totals.CachedInputTokens, d.Totals.OutputTokens, d.Totals.UsageUnreported
                }),
                report.Window, report.WasteByCause, report.CascadeRoutes, report.PendingGoals, report.PendingRounds
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            return;
        }
        Console.WriteLine(FormattableString.Invariant($"Round value [{report.Since:O}, {report.Until:O}) | cohort = landed or lost goals whose last round is in the window"));
        Console.WriteLine("Day | Landed | Lost | Rounds | Productive | Overhead | Wasted | Rounds per landing | Waste share | Input | Cached input | Output | Usage unreported");
        foreach (var day in report.Days) WriteRow(day.Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), day.Totals);
        WriteRow("Window", report.Window);
        Console.WriteLine("Waste cause | Rounds | Share");
        foreach (var cause in report.WasteByCause)
            Console.WriteLine(FormattableString.Invariant($"{cause.Cause} | {cause.Rounds} | {cause.Share:0.###}"));
        if (report.CascadeRoutes.Count > 0)
        {
            Console.WriteLine("Cascade route | Rounds | Productive | Overhead | Wasted");
            foreach (var route in report.CascadeRoutes)
                Console.WriteLine(FormattableString.Invariant($"{route.Decision} | {route.Rounds} | {route.Productive} | {route.Overhead} | {route.Wasted}"));
        }
        Console.WriteLine(FormattableString.Invariant($"Pending goals | {report.PendingGoals} | rounds={report.PendingRounds}"));
    }

    private static bool TryTimestamp(string text, out DateTimeOffset at)
    {
        // Require the offset promised by the usage contract, rather than assuming the machine's zone.
        var hasOffset = text.EndsWith('Z') || (text.Length >= 6 &&
            text[^6] is '+' or '-' && text[^3] == ':');
        at = default;
        return hasOffset && DateTimeOffset.TryParseExact(text,
            ["yyyy-MM-dd'T'HH:mm:ssK", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK"],
            CultureInfo.InvariantCulture, DateTimeStyles.None, out at);
    }

    private static void WriteRow(string label, RoundValueTotals row)
    {
        var ratio = row.RoundsPerLanding?.ToString("0.###", CultureInfo.InvariantCulture) ?? "n/a";
        Console.WriteLine(FormattableString.Invariant($"{label} | {row.LandedGoals} | {row.LostGoals} | {row.Rounds} | {row.Productive} | {row.ExpectedOverhead} | {row.Wasted} | {ratio} | {row.WasteShare:0.###} | {row.InputTokens} | {row.CachedInputTokens} | {row.OutputTokens} | {row.UsageUnreported}"));
    }

    private static void Invalid() => Console.Error.WriteLine(CliCommandHelp.RoundValueUsage);
}
