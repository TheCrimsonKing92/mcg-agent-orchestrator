using System.Globalization;
using System.Text.RegularExpressions;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal static class OwnerGateMotion
{
    private static readonly TimeSpan ReportLimit = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan MotionWindow = TimeSpan.FromMinutes(10);
    private static readonly Regex Fields = new(
        """(?:^|\s)(?<key>phase|target|child_pid|output_bytes)=(?<value>"(?:\\.|[^"\\])*"|[^\s"]+)(?=\s|$)""",
        RegexOptions.CultureInvariant);

    internal static string? Describe(string goalId, DateTimeOffset now, IReadOnlyList<OwnerConductEvent> events)
    {
        if (string.IsNullOrWhiteSpace(goalId)) return null;
        var samples = new List<Sample>();
        foreach (var item in events)
        {
            if (item.EventKind != "gate-progress" || string.IsNullOrWhiteSpace(item.GoalId) ||
                !goalId.Trim().StartsWith(item.GoalId.Trim(), StringComparison.Ordinal)) continue;
            if (Parse(item.Detail) is { } output) samples.Add(new(item.Timestamp, output));
        }
        var ordered = samples.OrderByDescending(sample => sample.Timestamp).ToArray();
        if (ordered.Length == 0) return null;
        var newest = ordered[0];
        if (now - newest.Timestamp > ReportLimit)
            return $"no progress report {Minutes(now - newest.Timestamp)}m";

        for (var index = 1; index < ordered.Length; index++)
        {
            if (newest.Timestamp - ordered[index].Timestamp > MotionWindow) break;
            if (ordered[index - 1].Output != ordered[index].Output) return "output moving";
        }

        var runStart = newest.Timestamp;
        foreach (var sample in ordered)
        {
            if (sample.Output != newest.Output) break;
            runStart = sample.Timestamp;
        }
        return newest.Timestamp - runStart >= MotionWindow
            ? $"no output change {Minutes(now - runStart)}m" : null;
    }

    private static Output? Parse(string detail)
    {
        string? phase = null, target = null, childPid = null, bytes = null;
        foreach (Match match in Fields.Matches(detail))
        {
            var value = match.Groups["value"].Value;
            if (value.StartsWith('"')) value = value[1..^1];
            switch (match.Groups["key"].Value)
            {
                case "phase": phase = value; break;
                case "target": target = value; break;
                case "child_pid": childPid = value; break;
                case "output_bytes": bytes = value; break;
            }
        }
        return phase is not null && target is not null && childPid is not null &&
            long.TryParse(bytes, NumberStyles.None, CultureInfo.InvariantCulture, out var count)
            ? new(phase, target, childPid, count) : null;
    }

    private static int Minutes(TimeSpan span) => Math.Max(0, (int)span.TotalMinutes);
    private sealed record Output(string Phase, string Target, string ChildPid, long Bytes);
    private sealed record Sample(DateTimeOffset Timestamp, Output Output);
}
