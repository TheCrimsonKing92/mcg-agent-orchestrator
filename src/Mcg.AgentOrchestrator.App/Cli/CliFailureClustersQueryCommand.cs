using System.Globalization;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class CliFailureClustersQueryCommand
{
    internal static bool IsFailureClustersQueryCommand(IReadOnlyList<string> args) =>
        args.Count > 0 && args[0].Equals("failure-clusters", StringComparison.OrdinalIgnoreCase) &&
        !CliCommandHelp.IsCommandSpecificHelp(args);

    internal static void Execute(IReadOnlyList<string> args, OrchestratorWorkspace workspace)
    {
        DateTimeOffset? since = null;
        var until = DateTimeOffset.UtcNow;
        var top = 20;
        var json = false;
        for (var i = 1; i < args.Count; i++)
        {
            if (args[i] == "--json") { json = true; continue; }
            if (args[i] is not ("--since" or "--until" or "--top") || i + 1 == args.Count) { Invalid(); return; }
            var flag = args[i++];
            if (flag == "--top")
            {
                if (!int.TryParse(args[i], NumberStyles.None, CultureInfo.InvariantCulture, out top) || top <= 0)
                { Invalid(); return; }
            }
            else
            {
                if (!DateTimeOffset.TryParseExact(args[i],
                    ["yyyy-MM-dd'T'HH:mm:ssK", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK", "yyyy-MM-dd"],
                    CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)) { Invalid(); return; }
                if (flag == "--since") since = at; else until = at;
            }
        }
        since ??= until.AddDays(-14);
        if (since >= until) { Invalid(); return; }
        var input = FailureClusterSourceReader.Read(workspace, until);
        var rows = input.Build(since.Value, until).Take(top).ToArray();
        WriteSkipWarning(input);
        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(rows, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            return;
        }
        Console.WriteLine("rank\tkey\tfamily\tcost\tpaid\tknock-on\ttouches\tgate-min\tgoals\tfirst\tlast\tmarker\tcli\troot-events\tquery");
        var rank = 0;
        foreach (var row in rows)
            Console.WriteLine(FormattableString.Invariant($"{++rank}\t{row.Key}\t{Family(row.MessageFamily)}\t{row.TotalCost:0.##}\t{row.PaidRounds}\t{row.KnockOnRounds}\t{row.OperatorTouches}\t{row.GateMinutes:0.##}\t{Goals(row.GoalsAffected)}\t{row.FirstSeen:O}\t{row.LastSeen:O}\t{row.Marker}\t{row.WorkerCli}\t{row.RootEvents}\t{row.Query}"));
    }

    private static string Family(string message)
    {
        var cell = message.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
        return cell.Length > 80 ? cell[..80] + "..." : cell;
    }

    private static string Goals(IReadOnlyList<string> goals) => string.Join(',', goals.Take(3)) +
        (goals.Count > 3 ? $" +{goals.Count - 3} more" : "");

    private static void WriteSkipWarning(FailureClusterInputs input)
    {
        var parts = new List<string>();
        foreach (var (count, source) in new[] { (input.SkippedGoalEvents, "goal-event"),
            (input.SkippedConductEvents, "conduct-event"), (input.SkippedOperatorIntents, "operator-intent") })
            if (count > 0) parts.Add($"{count} {source} record{(count == 1 ? "" : "s")}");
        if (parts.Count == 0) return;
        var sources = parts.Count == 1 ? parts[0] : string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts[^1];
        Console.Error.WriteLine($"Warning: failure-clusters skipped {sources}.");
    }

    private static void Invalid() => Console.Error.WriteLine(CliCommandHelp.FailureClustersUsage);
}
