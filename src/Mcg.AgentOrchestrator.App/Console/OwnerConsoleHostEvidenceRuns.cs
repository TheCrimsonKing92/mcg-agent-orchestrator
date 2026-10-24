using System.Text.Json;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed record OwnerConsoleHostActivity(string GoalId, string Label, DateTimeOffset StartedAt, TimeSpan Age = default);

internal static class OwnerConsoleHostEvidenceRuns
{
    internal sealed record Entry(string Attempt, OwnerConsoleHostActivity? Start);

    internal static IReadOnlyList<OwnerConsoleHostActivity> Read(IEnumerable<string> lines, DateTimeOffset now) =>
        Fold(lines.Select(Parse), now);

    internal static Entry? Parse(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            var kind = root.GetProperty("eventKind").GetString();
            if (kind is not ("EVIDENCE_START" or "EVIDENCE_END")) return null;
            var attempt = root.GetProperty("attempt").GetString();
            if (string.IsNullOrWhiteSpace(attempt)) return null;
            if (kind == "EVIDENCE_END") return new(attempt, null);
            var started = root.GetProperty("timestamp").GetDateTimeOffset();
            var goal = root.GetProperty("goalId").GetString() ?? "";
            var requests = root.TryGetProperty("member_requests", out var members)
                ? members.EnumerateArray().Select(item => item.GetString() ?? "").ToArray() : [];
            var label = requests.Length == 0 ? "" : requests[0][(requests[0].IndexOf(':') + 1)..];
            if (requests.Length > 1) label += ", +" + (requests.Length - 1);
            return new(attempt, new(goal, label, started));
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        { return null; }
    }

    internal static IReadOnlyList<OwnerConsoleHostActivity> Fold(IEnumerable<Entry?> entries, DateTimeOffset now)
    {
        var starts = new Dictionary<string, OwnerConsoleHostActivity>(StringComparer.Ordinal);
        var ended = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (entry is null) continue;
            if (entry.Start is null) ended.Add(entry.Attempt);
            else starts[entry.Attempt] = entry.Start with { Age = now - entry.Start.StartedAt };
        }
        return starts.Where(pair => !ended.Contains(pair.Key) && pair.Value.Age <= TimeSpan.FromMinutes(60))
            .Select(pair => pair.Value).ToArray();
    }
}
