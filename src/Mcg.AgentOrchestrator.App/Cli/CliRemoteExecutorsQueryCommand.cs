using System.Globalization;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class CliRemoteExecutorsQueryCommand
{
    internal static bool IsCommand(IReadOnlyList<string> args) => args.Count > 0 &&
        args[0].Equals("remote-executors", StringComparison.OrdinalIgnoreCase) && !CliCommandHelp.IsCommandSpecificHelp(args);

    internal static void Execute(IReadOnlyList<string> args, OrchestratorWorkspace workspace)
    {
        DateTimeOffset? since = null;
        var last = 10;
        var json = false;
        for (var i = 1; i < args.Count; i++)
        {
            if (args[i] == "--json") { json = true; continue; }
            if (args[i] is not ("--since" or "--last") || i + 1 == args.Count) { Invalid(); return; }
            var flag = args[i++];
            if (flag == "--last")
            {
                if (!int.TryParse(args[i], NumberStyles.None, CultureInfo.InvariantCulture, out last) || last <= 0) { Invalid(); return; }
            }
            else
            {
                if (!DateTimeOffset.TryParseExact(args[i],
                    ["yyyy-MM-dd'T'HH:mm:ssK", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK", "yyyy-MM-dd"],
                    CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)) { Invalid(); return; }
                since = at.ToUniversalTime();
            }
        }
        var report = RemoteExecutorReportReader.Read(workspace, since, last);
        if (json) { Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions(JsonSerializerDefaults.Web))); return; }
        Console.WriteLine($"unreadable_outcome_lines={report.UnreadableOutcomeLines}\tunreadable_probe_lines={report.UnreadableProbeLines}");
        Console.WriteLine("executor\tattempts\toutcomes\taccepted_share\tlast_success\tlast_probe");
        foreach (var row in report.Executors)
        {
            Console.WriteLine(FormattableString.Invariant($"{Cell(row.ExecutorId)}\t{row.Attempts}\t{JsonSerializer.Serialize(row.OutcomeCounts)}\t{row.AcceptedShare?.ToString("R", CultureInfo.InvariantCulture) ?? "null"}\t{row.LastSuccessAt?.ToString("O") ?? "null"}\t{JsonSerializer.Serialize(row.LastProbe, new JsonSerializerOptions(JsonSerializerDefaults.Web))}"));
            foreach (var attempt in row.RecentAttempts)
                Console.WriteLine(FormattableString.Invariant($"attempt\t{Cell(row.ExecutorId)}\t{attempt.ObservedAt:O}\t{Cell(attempt.GateAttemptId)}\t{Cell(attempt.Lane)}\t{Cell(attempt.Outcome)}\t{Cell(attempt.Reason)}"));
        }
    }
    private static string Cell(string? value) => value?.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ') ?? "";
    private static void Invalid() => Console.Error.WriteLine(CliCommandHelp.RemoteExecutorsUsage);
}
