using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class CliStorageHealthQueryCommand
{
    internal sealed record ClassRow(string Pattern, string Owner, string Writer, string Retention,
        double? RetentionAgeDays, long Bytes, long Files, long BudgetBytes, string Status);
    internal sealed record UnregisteredRow(string Name, long Bytes, long Files);
    internal sealed record Totals(long Bytes, long Files, int Classes, int Over, int Unregistered, long Unreadable, double ElapsedMs);
    internal sealed record Report(IReadOnlyList<ClassRow> Classes, IReadOnlyList<UnregisteredRow> Unregistered, Totals Totals);

    internal static bool IsStorageHealthQueryCommand(IReadOnlyList<string> args) =>
        args.Count > 0 && args[0].Equals("storage-health", StringComparison.OrdinalIgnoreCase) &&
        !CliCommandHelp.IsCommandSpecificHelp(args);

    internal static void Execute(IReadOnlyList<string> args, OrchestratorWorkspace workspace)
    {
        if (args.Skip(1).Any(arg => arg != "--json"))
        {
            Console.Error.WriteLine(CliCommandHelp.StorageHealthUsage);
            return;
        }
        var report = Build(workspace.OrchestratorDirectory, OrchestratorArtifactRegistry.Entries);
        if (args.Contains("--json"))
        {
            Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions(JsonSerializerDefaults.Web)
            { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower }));
            return;
        }
        foreach (var row in report.Classes)
            Console.WriteLine(FormattableString.Invariant($"class\t{Cell(row.Pattern)}\towner={Cell(row.Owner)}\twriter={row.Writer}\tretention={row.Retention}\tretention_age_days={row.RetentionAgeDays?.ToString(CultureInfo.InvariantCulture) ?? "-"}\tbytes={row.Bytes}\tfiles={row.Files}\tbudget_bytes={row.BudgetBytes}\tstatus={row.Status}"));
        foreach (var row in report.Unregistered)
            Console.WriteLine(FormattableString.Invariant($"unregistered\t{Cell(row.Name)}\tbytes={row.Bytes}\tfiles={row.Files}"));
        var totals = report.Totals;
        Console.WriteLine(FormattableString.Invariant($"totals\tbytes={totals.Bytes}\tfiles={totals.Files}\tclasses={totals.Classes}\tover={totals.Over}\tunregistered={totals.Unregistered}\tunreadable={totals.Unreadable}\telapsed_ms={totals.ElapsedMs}"));
    }

    internal static Report Build(string orchestratorDirectory, IReadOnlyList<OrchestratorArtifactRegistry.Entry> entries)
    {
        var stopwatch = Stopwatch.StartNew();
        var sizes = entries.ToDictionary(e => e.Pattern, _ => (Bytes: 0L, Files: 0L, Present: false), StringComparer.OrdinalIgnoreCase);
        var unregistered = new List<UnregisteredRow>();
        long unreadable = 0;
        foreach (var path in TopLevelPaths(orchestratorDirectory, ref unreadable))
        {
            var name = Path.GetFileName(path);
            var entry = OrchestratorArtifactRegistry.Classify(name, entries);
            var size = Measure(path, ref unreadable);
            if (entry is null) unregistered.Add(new(name, size.Bytes, size.Files));
            else
            {
                var current = sizes[entry.Pattern];
                sizes[entry.Pattern] = (current.Bytes + size.Bytes, current.Files + size.Files, true);
            }
        }
        var classes = entries.Select(entry =>
        {
            var size = sizes[entry.Pattern];
            return new ClassRow(entry.Pattern, entry.Owner, entry.Writer.ToString(), entry.Retention.ToString(),
                entry.RetentionAge?.TotalDays, size.Bytes, size.Files, entry.BudgetBytes,
                !size.Present ? "absent" : size.Bytes > entry.BudgetBytes ? "over" : "ok");
        }).OrderByDescending(row => row.Bytes).ThenBy(row => row.Pattern, StringComparer.Ordinal).ToArray();
        var unknown = unregistered.OrderByDescending(row => row.Bytes).ThenBy(row => row.Name, StringComparer.Ordinal).ToArray();
        return new(classes, unknown, new(classes.Sum(row => row.Bytes) + unknown.Sum(row => row.Bytes),
            classes.Sum(row => row.Files) + unknown.Sum(row => row.Files), entries.Count,
            classes.Count(row => row.Status == "over"), unknown.Length, unreadable, stopwatch.Elapsed.TotalMilliseconds));
    }

    private static string[] TopLevelPaths(string directory, ref long unreadable)
    {
        try
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) return [];
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException or FileNotFoundException) { return []; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { unreadable++; return []; }
        return Children(directory, ref unreadable);
    }

    private static (long Bytes, long Files) Measure(string path, ref long unreadable)
    {
        long bytes = 0;
        long files = 0;
        var pending = new Stack<string>();
        pending.Push(path);
        while (pending.TryPop(out var current))
        {
            try
            {
                var attributes = File.GetAttributes(current);
                if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    foreach (var child in Children(current, ref unreadable)) pending.Push(child);
                }
                else { bytes += new FileInfo(current).Length; files++; }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { unreadable++; }
        }
        return (bytes, files);
    }

    private static string[] Children(string directory, ref long unreadable)
    {
        try
        {
            return Directory.GetFileSystemEntries(directory, "*", new EnumerationOptions
            { RecurseSubdirectories = false, IgnoreInaccessible = false, AttributesToSkip = 0 });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { unreadable++; return []; }
    }

    private static string Cell(string value) => value.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
}
