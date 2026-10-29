using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class ImpactReplayReportWriter
{
    internal static void Write(string outputDirectory, ImpactReplayReport report)
    {
        Directory.CreateDirectory(outputDirectory);
        File.WriteAllText(Path.Combine(outputDirectory, "impact-replay.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
        File.WriteAllText(Path.Combine(outputDirectory, "impact-replay.md"), Render(report));
    }

    internal static string Render(ImpactReplayReport report)
    {
        var text = new StringBuilder("# Impact replay\n\n");
        text.AppendLine($"Floor source: {report.FloorSource}");
        if (report.FloorSource == "default")
            text.AppendLine("Default floor policy: Process spawning*, Isolated process spawning*, Chaos gate, Remainder merge train and ratchet.");
        text.AppendLine($"Floor lanes: {string.Join(", ", report.FloorLanes.Select(Cell))}");
        text.AppendLine();
        text.AppendLine($"Attempts replayed: {report.Attempts.Count}");
        text.AppendLine($"Unreplayable attempts: {report.UnreplayableAttempts.Count}");
        text.AppendLine($"Skipped inputs: {report.SkippedInputs.Count}");
        text.AppendLine($"Incomplete miss checks: {report.Attempts.Count(attempt => !attempt.MissCheckComplete)}");
        var shares = report.Attempts.Select(attempt => attempt.ShareOfLanesSkipped).Order().ToArray();
        text.AppendLine($"Median share of lanes skipped: {Percent(NearestRank(shares, 0.5))}");
        text.AppendLine($"P90 share of lanes skipped: {Percent(NearestRank(shares, 0.9))}");
        text.AppendLine($"Total estimated lane-seconds saved: {Number(report.Attempts.Sum(attempt => attempt.EstimatedSerialSecondsSaved))}");
        text.AppendLine();
        text.AppendLine("## Selections abandoned, by kind");
        text.AppendLine();
        var abandoned = report.Attempts.Where(attempt => attempt.SelectionOutcome != "Resolved")
            .GroupBy(attempt => attempt.DegradationKind ?? attempt.SelectionOutcome).OrderBy(group => group.Key, StringComparer.Ordinal).ToArray();
        if (abandoned.Length == 0) text.AppendLine("None.");
        foreach (var group in abandoned) text.AppendLine($"- {Cell(group.Key)}: {group.Count()}");
        text.AppendLine();
        text.AppendLine("## Misses");
        text.AppendLine();
        var misses = report.Attempts.SelectMany(attempt => attempt.FailingClasses.Where(failure => failure.IsMiss)
            .Select(failure => (attempt.AttemptId, Failure: failure))).ToArray();
        if (misses.Length == 0) text.AppendLine("None observed; consult incomplete miss checks before drawing conclusions.");
        else
        {
            text.AppendLine("| Attempt | Class | Lane |");
            text.AppendLine("| --- | --- | --- |");
            foreach (var miss in misses)
                text.AppendLine($"| {Cell(miss.AttemptId)} | {Cell(miss.Failure.ClassName)} | {Cell(miss.Failure.Lane!)} |");
        }
        text.AppendLine();
        text.AppendLine("## Attempts");
        text.AppendLine();
        text.AppendLine("| Attempt | Selection | Lanes ran | Would run | Skipped | Estimated seconds saved |");
        text.AppendLine("| --- | --- | --- | --- | --- | --- |");
        foreach (var attempt in report.Attempts)
            text.AppendLine($"| {Cell(attempt.AttemptId)} | {Cell(attempt.SelectionOutcome + (attempt.DegradationKind is null ? "" : ": " + attempt.DegradationKind))} " +
                $"| {Cell(string.Join(", ", attempt.RanLanes))} | {Cell(string.Join(", ", attempt.WouldRunLanes))} " +
                $"| {Cell(string.Join(", ", attempt.SkippedLanes))} | {Number(attempt.EstimatedSerialSecondsSaved)} |");
        text.AppendLine();
        text.AppendLine("## Input and evidence diagnostics");
        text.AppendLine();
        foreach (var diagnostic in report.SkippedInputs.Concat(report.UnreplayableAttempts))
            text.AppendLine($"- {Cell(diagnostic.Source)}: {Cell(diagnostic.Reason)}");
        foreach (var attempt in report.Attempts)
        {
            foreach (var warning in attempt.EvidenceWarnings) text.AppendLine($"- {Cell(attempt.AttemptId)}: {Cell(warning)}");
            var nonCSharp = attempt.ChangedFiles.Where(path => !path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (nonCSharp.Length > 0) text.AppendLine($"- {Cell(attempt.AttemptId)}: non-C# changes: {Cell(string.Join(", ", nonCSharp))}");
        }
        foreach (var error in report.CleanupErrors) text.AppendLine($"- {Cell(error)}");
        return text.ToString();
    }

    private static double NearestRank(double[] values, double percentile) =>
        values.Length == 0 ? 0 : values[Math.Max(0, (int)Math.Ceiling(values.Length * percentile) - 1)];
    private static string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
    private static string Percent(double value) => value.ToString("P1", CultureInfo.InvariantCulture);
    private static string Cell(string value) => value.Replace("|", "\\|", StringComparison.Ordinal).Replace('\r', ' ').Replace('\n', ' ');
}
