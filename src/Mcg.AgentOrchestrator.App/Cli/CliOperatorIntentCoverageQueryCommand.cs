using System.Globalization;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class CliOperatorIntentCoverageQueryCommand
{
    internal static bool IsIntentCoverageQueryCommand(IReadOnlyList<string> args) =>
        args.Count > 0 && args[0].Equals("intent-coverage", StringComparison.OrdinalIgnoreCase) &&
        !CliCommandHelp.IsCommandSpecificHelp(args);

    internal static void Execute(IReadOnlyList<string> args, OrchestratorWorkspace workspace)
    {
        DateTimeOffset? since = null;
        var until = DateTimeOffset.UtcNow;
        var minCases = 30;
        var json = false;
        for (var i = 1; i < args.Count; i++)
        {
            if (args[i] == "--json") { json = true; continue; }
            if (args[i] is not ("--since" or "--until" or "--min-cases") || i + 1 == args.Count)
            { Invalid(); return; }
            var flag = args[i++];
            if (flag == "--min-cases")
            {
                if (!int.TryParse(args[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out minCases) || minCases < 1)
                { Invalid(); return; }
                continue;
            }
            if (!DateTimeOffset.TryParseExact(args[i],
                ["yyyy-MM-dd'T'HH:mm:ssK", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK", "yyyy-MM-dd"],
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)) { Invalid(); return; }
            if (flag == "--since") since = at.ToUniversalTime(); else until = at.ToUniversalTime();
        }
        if (since is null && until < DateTimeOffset.MinValue.AddDays(7)) { Invalid(); return; }
        since ??= until.AddDays(-7);
        if (since >= until) { Invalid(); return; }
        var input = OperatorIntentCoverageRecordReader.Read(workspace);
        var report = OperatorIntentCoverageReport.Build(input.Intents, input.Triggers, since.Value, until, minCases);
        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            return;
        }
        Console.WriteLine(FormattableString.Invariant($"since={report.Since:O}\tuntil={report.Until:O}\thuman_intents={report.HumanIntents}\tagreed={report.Agreed}\tdisagreed={report.Disagreed}\tsilent={report.Silent}\tuncovered={report.Uncovered}\tcoverage={report.Coverage:R} ({report.Coverage:P1})\tmin_cases={report.MinCases}"));
        foreach (var row in report.Verbs)
            Console.WriteLine(FormattableString.Invariant($"verb\t{Cell(row.Verb)}\ttotal={row.Total}\tagreed={row.Agreed}\tdisagreed={row.Disagreed}\tsilent={row.Silent}\tuncovered={row.Uncovered}\tcoverage={row.Coverage:P1}"));
        foreach (var row in report.Classes)
            Console.WriteLine(FormattableString.Invariant($"class\t{Cell(row.ClassKey)}\ttotal={row.Total}\tagreed={row.Agreed}\tdisagreed={row.Disagreed}\tsilent={row.Silent}\tuncovered={row.Uncovered}\tcoverage={row.Coverage:P1}\trejected={row.Rejected}\tdistinct_goals={row.DistinctGoals}\tstructural={row.Structural.ToString().ToLowerInvariant()}\tautomatable_candidate={row.AutomatableCandidate.ToString().ToLowerInvariant()}"));
    }

    private static string Cell(string value) => value.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
    private static void Invalid() => Console.Error.WriteLine(CliCommandHelp.IntentCoverageUsage);
}
