using System.Globalization;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class CliLaneReuseShadowQueryCommand
{
    internal static bool IsLaneReuseShadowQueryCommand(IReadOnlyList<string> args) =>
        args.Count > 0 && args[0].Equals("lane-reuse-shadow", StringComparison.OrdinalIgnoreCase) &&
        !CliCommandHelp.IsCommandSpecificHelp(args);

    internal static void Execute(IReadOnlyList<string> args, OrchestratorWorkspace workspace)
    {
        DateTimeOffset? since = null;
        var until = DateTimeOffset.UtcNow;
        var json = false;
        for (var i = 1; i < args.Count; i++)
        {
            if (args[i] == "--json") { json = true; continue; }
            if (args[i] is not ("--since" or "--until") || i + 1 == args.Count) { Invalid(); return; }
            var flag = args[i++];
            if (!DateTimeOffset.TryParseExact(args[i],
                ["yyyy-MM-dd'T'HH:mm:ssK", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK", "yyyy-MM-dd"],
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)) { Invalid(); return; }
            if (flag == "--since") since = at.ToUniversalTime(); else until = at.ToUniversalTime();
        }
        if (since is null && until < DateTimeOffset.MinValue.AddDays(7)) { Invalid(); return; }
        since ??= until.AddDays(-7);
        if (since >= until) { Invalid(); return; }
        var input = LaneReuseShadowRecordReader.Read(workspace);
        var report = LaneReuseShadowReport.Build(input.Records, since.Value, until,
            input.UntimedRecords, input.UnreadableRecords);
        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            return;
        }
        Console.WriteLine(FormattableString.Invariant($"gates={report.Gates}\tlane_rows={report.LaneRows}\twould_reuse_rows={report.WouldReuseRows}\twould_reuse_share={report.WouldReuseShare:R} ({report.WouldReuseShare:P1})\texecuted_lane_seconds={report.ExecutedLaneSeconds:R}\tsaved_lane_seconds={report.SavedLaneSeconds:R}\tsaved_share={report.SavedShare:R} ({report.SavedShare:P1})\tmisses={report.Misses.Count}\tflake_confirmed_misses={report.FlakeConfirmedMisses}\tuntimed_records={report.UntimedRecords}\tunreadable_records={report.UnreadableRecords}"));
        Console.WriteLine("must-run reason\tlanes");
        foreach (var reason in report.MustRunReasons)
            Console.WriteLine(FormattableString.Invariant($"{Cell(reason.Family)}\t{reason.Lanes}"));
        Console.WriteLine("miss\tgoal\tattempt\tlane\tmiss_reason\treference_source\tflake_confirmed\trecorded_at\treason\tfailed_predicate\tfailing_classes");
        foreach (var miss in report.Misses)
            Console.WriteLine(FormattableString.Invariant($"miss\t{Cell(miss.GoalId)}\t{Cell(miss.AttemptId)}\t{Cell(miss.Lane)}\t{Cell(miss.MissReason)}\t{Cell(miss.ReferenceSource)}\t{miss.FlakeConfirmed.ToString().ToLowerInvariant()}\t{miss.RecordedAt:O}\t{Cell(miss.Reason)}\t{Cell(miss.FailedPredicate)}\t{JsonSerializer.Serialize(miss.FailingClasses)}"));
    }

    private static string Cell(string? value) => value?.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ') ?? "";
    private static void Invalid() => Console.Error.WriteLine(CliCommandHelp.LaneReuseShadowUsage);
}
