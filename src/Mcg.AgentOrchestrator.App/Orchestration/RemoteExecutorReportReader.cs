using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class RemoteExecutorReportReader
{
    internal static RemoteExecutorReport Read(OrchestratorWorkspace workspace, DateTimeOffset? since, int last)
    {
        var outcomes = ReadLines(RemoteExecutorHealthLedger.ResolveStorePath(workspace.RootDirectory), root =>
        {
            var (seconds, startedAt) = ReadAttempt(root);
            return new RemoteExecutorOutcomeRow(root.GetProperty("observed_at").GetDateTimeOffset(),
                RequiredString(root, "executor_id"), RequiredString(root, "gate_attempt_id"),
                RequiredString(root, "lane"), RequiredString(root, "outcome"), OptionalString(root, "reason"), seconds, startedAt);
        }, out var unreadable);
        var probes = ReadProbes(RemoteExecutorProbeLedger.ResolveStorePath(workspace.RootDirectory), out var unreadableProbes);
        var executors = RemoteLaneExecutorConfiguration.LoadExecutors(RemoteLaneExecutorConfiguration.ResolveStorePath(workspace.RootDirectory));
        var slots = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var executor in executors) slots.TryAdd(executor.Id, executor.Slots);
        return RemoteExecutorReport.Build(executors.Select(entry => entry.Id), outcomes, probes,
            since, last, unreadable, unreadableProbes, slots);
    }

    private static (double? Seconds, DateTimeOffset? StartedAt) ReadAttempt(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("attempt", out var attempt) ||
            attempt.ValueKind != JsonValueKind.Object) return (null, null);
        double? seconds = null;
        if (attempt.TryGetProperty("last_status", out var status) && status.ValueKind == JsonValueKind.Object &&
            status.TryGetProperty("seconds", out var value) && value.ValueKind == JsonValueKind.Number &&
            value.TryGetDouble(out var number) && double.IsFinite(number) && number > 0)
            seconds = number;
        DateTimeOffset? startedAt = null;
        if (attempt.TryGetProperty("steps", out var steps) && steps.ValueKind == JsonValueKind.Array)
            foreach (var step in steps.EnumerateArray())
                if (step.ValueKind == JsonValueKind.Object && step.TryGetProperty("started_at", out var start) &&
                    start.ValueKind == JsonValueKind.String && start.TryGetDateTimeOffset(out var at) &&
                    (startedAt is null || at < startedAt))
                    startedAt = at;
        return (seconds, startedAt);
    }

    internal static IReadOnlyList<RemoteExecutorProbeRow> ReadProbes(string path, out int unreadable) =>
        ReadLines(path, root => new RemoteExecutorProbeRow(root.GetProperty("observed_at").GetDateTimeOffset(),
            RequiredString(root, "executor_id"), root.GetProperty("reachable").GetBoolean(),
            root.GetProperty("exit_code").GetInt32(), root.GetProperty("timed_out").GetBoolean(),
            OptionalString(root, "task_state"),
            root.GetProperty("power_online").ValueKind == JsonValueKind.Null ? null : root.GetProperty("power_online").GetBoolean(),
            root.GetProperty("reasons").EnumerateArray().Select(value => value.GetString() ?? throw new JsonException()).ToArray(),
            RequiredString(root, "stderr_tail")), out unreadable);

    private static IReadOnlyList<T> ReadLines<T>(string path, Func<JsonElement, T> parse, out int unreadable)
    {
        unreadable = 0;
        var rows = new List<T>();
        foreach (var line in SharedJsonlFile.ReadLines(path))
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                rows.Add(parse(document.RootElement));
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
            { unreadable++; }
        }
        return rows;
    }
    private static string RequiredString(JsonElement root, string name) =>
        root.GetProperty(name).GetString() ?? throw new JsonException();
    private static string? OptionalString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null ? value.GetString() : null;
}
