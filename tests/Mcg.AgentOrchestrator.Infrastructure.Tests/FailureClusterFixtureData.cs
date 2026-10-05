using System.Text.Json;
using System.Runtime.CompilerServices;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;

// Real records selected and sanitized by the operator; the committed JSONL is copied verbatim.
internal static class FailureClusterFixtureData
{
    internal static readonly DateTimeOffset Since = DateTimeOffset.Parse("2026-09-21T00:00:00Z");
    internal static readonly DateTimeOffset Until = DateTimeOffset.Parse("2026-10-06T00:00:00Z");

    internal static string FilePath(string name) => Path.Combine(FixtureDirectory(), name);

    private static string FixtureDirectory([CallerFilePath] string sourceFilePath = "")
    {
        if (VerifiedRepositoryRoot.TryGetVerifiedRoot(out var root))
            return Path.Combine(root, "tests", "Mcg.AgentOrchestrator.Infrastructure.Tests", "Fixtures", "FailureClusters");
        return Path.Combine(Path.GetDirectoryName(sourceFilePath)!, "Fixtures", "FailureClusters");
    }

    internal static FailureClusterInputs Read() => new(ReadGoals("goal-events.jsonl"),
        ReadLines("conduct-events.jsonl", e => new FailureClusterConductEvent(
            e.GetProperty("timestamp").GetDateTimeOffset(), Text(e, "eventKind")!,
            Text(e, "goalId"), Text(e, "detail")!)),
        ReadLines("operator-intents.jsonl", e => new FailureClusterOperatorTouch(
            Text(e, "goal_id")!, Text(e, "task_id"), e.GetProperty("created_at").GetDateTimeOffset())));

    internal static FailureClusterGoalEvent[] ReadGoals(string name) => ReadLines(name,
        e => new FailureClusterGoalEvent(Text(e, "eventType")!, Text(e, "message")!,
            Text(e, "goalId")!, Text(e, "taskId"), e.GetProperty("timestamp").GetDateTimeOffset()));

    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null ? value.GetString() : null;

    private static T[] ReadLines<T>(string name, Func<JsonElement, T> parse) =>
        File.ReadLines(FilePath(name)).Select(line =>
        {
            using var json = JsonDocument.Parse(line);
            return parse(json.RootElement);
        }).ToArray();
}
