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
        if (input.SkippedLines > 0) Console.Error.WriteLine($"Warning: failure-clusters skipped {input.SkippedLines} unreadable records/files.");
        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(rows, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            return;
        }
        Console.WriteLine("rank\tkey\tcost\tpaid\tknock-on\ttouches\tgate-min\tgoals\tfirst\tlast\tmarker\tcli\tfamily\troot-events\tquery");
        var rank = 0;
        foreach (var row in rows)
            Console.WriteLine(FormattableString.Invariant($"{++rank}\t{row.Key}\t{row.TotalCost:0.##}\t{row.PaidRounds}\t{row.KnockOnRounds}\t{row.OperatorTouches}\t{row.GateMinutes:0.##}\t{string.Join(',', row.GoalsAffected)}\t{row.FirstSeen:O}\t{row.LastSeen:O}\t{row.Marker}\t{row.WorkerCli}\t{row.MessageFamily}\t{row.RootEvents}\t{row.Query}"));
    }

    private static void Invalid() => Console.Error.WriteLine(CliCommandHelp.FailureClustersUsage);
}
