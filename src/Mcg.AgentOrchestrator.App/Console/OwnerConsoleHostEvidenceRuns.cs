using System.Text.Json;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed record OwnerConsoleHostActivity(string GoalId, string Label, DateTimeOffset StartedAt, TimeSpan Age = default,
    string? Attempt = null, int? Ordinal = null);
internal sealed record OwnerConsoleEvidenceAttemptEnd(string GoalId, string Attempt, double? DurationSeconds,
    string? Outcome, long? TestsExecuted);

internal static class OwnerConsoleHostEvidenceRuns
{
    internal sealed record Entry(string Attempt, OwnerConsoleHostActivity? Start, OwnerConsoleEvidenceAttemptEnd? End = null);

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
            if (kind == "EVIDENCE_END")
            {
                var endedGoal = root.TryGetProperty("goalId", out var id) && id.ValueKind == JsonValueKind.String
                    ? id.GetString() ?? "" : "";
                double? duration = root.TryGetProperty("duration_s", out var seconds) && seconds.ValueKind == JsonValueKind.Number &&
                    seconds.TryGetDouble(out var number) && double.IsFinite(number) && number >= 0 ? number : null;
                long? count = root.TryGetProperty("tests_executed", out var tests) && tests.ValueKind == JsonValueKind.Number &&
                    tests.TryGetInt64(out var total) && total >= 0 ? total : null;
                var outcome = root.TryGetProperty("outcome", out var result) && result.ValueKind == JsonValueKind.String
                    ? result.GetString() : null;
                return new(attempt, null, new(endedGoal, attempt, duration, outcome, count));
            }
            var started = root.GetProperty("timestamp").GetDateTimeOffset();
            var goal = root.GetProperty("goalId").GetString() ?? "";
            var requests = root.TryGetProperty("member_requests", out var members)
                ? members.EnumerateArray().Select(item => item.GetString() ?? "").ToArray() : [];
            var label = requests.Length == 0 ? "" : requests[0][(requests[0].IndexOf(':') + 1)..];
            if (requests.Length > 1) label += ", +" + (requests.Length - 1);
            int? ordinal = root.TryGetProperty("ordinal", out var order) && order.ValueKind == JsonValueKind.Number &&
                order.TryGetInt32(out var value) && value > 0 ? value : null;
            return new(attempt, new(goal, label, started, Attempt: attempt, Ordinal: ordinal));
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        { return null; }
    }

    internal static OwnerConsoleEvidenceAttemptEnd? LatestEnded(IEnumerable<OwnerConsoleEvidenceAttemptEnd>? entries,
        string goalId, string? runningAttempt) => entries?.LastOrDefault(entry =>
            entry.GoalId == goalId && entry.Attempt != runningAttempt);

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
