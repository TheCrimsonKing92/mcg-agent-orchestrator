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
        DateTimeOffset? baselineSince = null;
        DateTimeOffset? baselineUntil = null;
        var json = false;
        var bySkill = false;
        for (var i = 1; i < args.Count; i++)
        {
            if (args[i] == "--json") { json = true; continue; }
            if (args[i] == "--by-skill") { bySkill = true; continue; }
            if (args[i] is not ("--since" or "--until" or "--baseline-since" or "--baseline-until") ||
                i + 1 == args.Count) { Invalid(); return; }
            var flag = args[i++];
            if (!TryTimestamp(args[i], out var at)) { Invalid(); return; }
            switch (flag)
            {
                case "--since": since = at; break;
                case "--until": until = at; break;
                case "--baseline-since": baselineSince = at; break;
                case "--baseline-until": baselineUntil = at; break;
            }
        }
        since ??= until.AddDays(-7);
        if (since >= until) { Invalid(); return; }
        if (baselineSince.HasValue != baselineUntil.HasValue || baselineSince >= baselineUntil)
        { Invalid(); return; }
        var metadata = queries.ListGoalMetadataAsync().GetAwaiter().GetResult();
        var goals = metadata.Count == 0 ? [] : queries.LoadGoalsAsync(
            metadata.Select(g => new GoalId(g.Id)).ToArray()).GetAwaiter().GetResult().Goals;
        if (bySkill)
        {
            WriteSkills(RoundValueSkillSlice.Build(goals, since.Value, until,
                CliOwnerDigestRetryIntents.Read(workspace, until)), json);
            return;
        }
        var intents = CliOwnerDigestRetryIntents.Read(workspace,
            baselineUntil > until ? baselineUntil.Value : until);
        var report = baselineSince is { } baselineStart && baselineUntil is { } baselineEnd
            ? RoundValueReport.Build(goals, since.Value, until, baselineStart, baselineEnd, intents)
            : RoundValueReport.Build(goals, since.Value, until, intents);
        if (json)
        {
            if (report.Baseline is { } comparison)
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
                    report.Window, report.WasteByCause, report.CascadeRoutes, report.PendingGoals, report.PendingRounds,
                    Baseline = new
                    {
                        Window = new { comparison.Since, comparison.Until },
                        Current = new { RoundsPerLanding = comparison.CurrentRoundsPerLanding },
                        Baseline = new { RoundsPerLanding = comparison.BaselineRoundsPerLanding },
                        comparison.Causes
                    }
                }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
                return;
            }
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
        if (report.Baseline is { } baseline)
        {
            Console.WriteLine(FormattableString.Invariant($"Baseline [{baseline.Since:O}, {baseline.Until:O}) | deltas = current minus baseline"));
            Console.WriteLine("Waste cause | Current rounds | Current share | Baseline rounds | Baseline share | Rounds delta | Share delta");
            foreach (var cause in baseline.Causes)
                Console.WriteLine(FormattableString.Invariant($"{cause.Cause} | {cause.CurrentRounds} | {cause.CurrentShare:0.###} | {cause.BaselineRounds} | {cause.BaselineShare:0.###} | {cause.RoundsDelta} | {cause.ShareDelta:0.###}"));
            var currentRatio = baseline.CurrentRoundsPerLanding?.ToString("0.###", CultureInfo.InvariantCulture) ?? "n/a";
            var baselineRatio = baseline.BaselineRoundsPerLanding?.ToString("0.###", CultureInfo.InvariantCulture) ?? "n/a";
            Console.WriteLine($"Rounds per landing | current={currentRatio} | baseline={baselineRatio}");
        }
    }

    private static void WriteSkills(RoundValueSkillSlice slice, bool json)
    {
        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(slice, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            return;
        }
        Console.WriteLine(FormattableString.Invariant($"Round value by skill [{slice.Since:O}, {slice.Until:O}) | cohort = landed or lost goals whose last round is in the window"));
        Console.WriteLine("Skill | Selected | Read | Claimed | Productive | Overhead | Wasted");
        foreach (var row in slice.Rows)
            Console.WriteLine(FormattableString.Invariant($"{row.Skill} | {row.Selected} | {row.Read} | {row.Claimed} | {row.Productive} | {row.Overhead} | {row.Wasted}"));
        Console.WriteLine(FormattableString.Invariant($"Read unavailable | {slice.ReadUnavailable}"));
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
