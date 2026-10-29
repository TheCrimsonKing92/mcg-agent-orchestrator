using System.Globalization;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class CliImpactReplayCommand
{
    internal static bool IsCommand(IReadOnlyList<string> args) => args.Count > 0 &&
        args[0].Equals("impact-replay", StringComparison.OrdinalIgnoreCase);

    internal static int Run(IReadOnlyList<string> args, OrchestratorWorkspace workspace) =>
        Run(args, workspace.ExecutionDirectory, workspace.OrchestratorDirectory, null, Console.Out, Console.Error);

    // Explicit directories let fixture tests exercise the command without opening live stores.
    internal static int Run(IReadOnlyList<string> args, string executionDirectory, string orchestratorDirectory,
        string? tempCheckoutParent, TextWriter output, TextWriter error)
    {
        DateTimeOffset? since = null;
        var limit = 100;
        string? destination = null;
        var floor = new List<string>();
        for (var i = 1; i < args.Count; i++)
        {
            if (args[i] is "--help" or "-h") { output.WriteLine(CliCommandHelp.ImpactReplayUsage); return 0; }
            if (args[i] is not ("--since" or "--limit" or "--out" or "--floor") || i + 1 == args.Count)
                return Usage();
            var flag = args[i++];
            var value = args[i];
            if (string.IsNullOrWhiteSpace(value) || value.StartsWith("--", StringComparison.Ordinal)) return Usage();
            switch (flag)
            {
                case "--since":
                    if (!DateTimeOffset.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date)) return Usage();
                    since = date;
                    break;
                case "--limit":
                    if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out limit) || limit < 1) return Usage();
                    break;
                case "--out": destination = value; break;
                case "--floor": floor.Add(value); break;
            }
        }
        if (destination is null) return Usage();
        try
        {
            var report = ImpactReplay.Read(executionDirectory, orchestratorDirectory, since, limit,
                floor.Count == 0 ? null : floor, tempCheckoutParent);
            ImpactReplayReportWriter.Write(destination, report);
            output.WriteLine($"Impact replay: {report.Attempts.Count} attempts; reports: {Path.GetFullPath(destination)}");
            foreach (var cleanupError in report.CleanupErrors) error.WriteLine(cleanupError);
            return report.CleanupErrors.Count == 0 ? 0 : 1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException or InvalidDataException)
        {
            error.WriteLine($"Impact replay failed: {ex.Message}");
            return 1;
        }
        int Usage() { error.WriteLine(CliCommandHelp.ImpactReplayUsage); return 2; }
    }
}
