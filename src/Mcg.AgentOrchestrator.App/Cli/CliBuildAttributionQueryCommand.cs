using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class CliBuildAttributionQueryCommand
{
    internal static bool IsBuildAttributionQueryCommand(IReadOnlyList<string> args) =>
        args.Count > 0 && args[0].Equals("build-attribution", StringComparison.OrdinalIgnoreCase) &&
        !CliCommandHelp.IsCommandSpecificHelp(args);

    internal static void Execute(IReadOnlyList<string> args, OrchestratorWorkspace workspace) =>
        Execute(args, workspace, DotnetBuildStorageLayout.CaptureStorageRoot(), sha =>
        {
            try
            {
                if (!Regex.IsMatch(sha, "\\A[0-9a-fA-F]{40}\\z")) return null;
                var result = GitCli.Run(workspace.ExecutionDirectory, "rev-parse", sha + "^{tree}");
                if (!result.Succeeded) return null;
                var tree = result.Output.Trim();
                return Regex.IsMatch(tree, "\\A[0-9a-fA-F]{40}([0-9a-fA-F]{24})?\\z") ? tree.ToLowerInvariant() : null;
            }
            catch (Exception) { return null; }
        });

    internal static void Execute(IReadOnlyList<string> args, OrchestratorWorkspace workspace,
        DotnetBuildStorageRoot storageRoot, Func<string, string?> treeResolver)
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
        if (since is null && until < DateTimeOffset.MinValue.AddDays(1)) { Invalid(); return; }
        since ??= until.AddDays(-1);
        if (since >= until) { Invalid(); return; }
        var report = BuildAttributionReport.Build(BuildAttributionRecordReader.Read(storageRoot, workspace, since.Value, until),
            since.Value, until, treeResolver);
        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions
                { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower }));
            return;
        }
        var s = report.Summary;
        Console.WriteLine(FormattableString.Invariant($"holds={s.Holds} held_ms={s.HeldMs} compiles={s.Compiles} overall_duplicate_factor={Number(s.OverallDuplicateFactor)} unresolved_tree_holds={s.UnresolvedTreeHolds} unrecorded_tree_holds={s.UnrecordedTreeHolds} unknown_compile_holds={s.UnknownCompileHolds} unreadable_records={s.UnreadableRecords} cpu={s.Cpu}"));
        Console.WriteLine("class\tholds\theld_ms\theld_share\tcompiles\twaits\twait_ms\ttimeouts");
        foreach (var c in report.Consumers)
            Console.WriteLine(FormattableString.Invariant($"{c.Class}\t{c.Holds}\t{c.HeldMs}\t{Number(c.HeldShare)}\t{c.Compiles}\t{c.Waits}\t{c.WaitMs}\t{c.Timeouts}"));
        Console.WriteLine("tree\tcompiles\tdistinct_projects\tduplicate_factor\tdistinct_heads\theld_ms\tby_class");
        foreach (var t in report.Trees)
            Console.WriteLine(FormattableString.Invariant($"{t.Tree}\t{t.Compiles}\t{t.DistinctProjects}\t{Number(t.DuplicateFactor)}\t{t.DistinctHeads}\t{t.HeldMs}\t{t.ByClass}"));
    }

    private static string Number(double? n) => n?.ToString("0.0##", CultureInfo.InvariantCulture) ?? "n/a";
    private static void Invalid() => Console.Error.WriteLine(CliCommandHelp.BuildAttributionUsage);
}
