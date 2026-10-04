using System.Globalization;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static partial class CliOwnerDigestJudgePanel
{
    private sealed record ResolutionRecord(string Id, string GoalId, string? TaskId,
        DateTimeOffset At, int Priority, Resolution Resolution, bool PairsRetry = false);

    private static Resolution Resolve(string goalId, DateTimeOffset trigger,
        IReadOnlyList<ResolutionRecord> records) => records
        // PanelCaseKey has no task id. Never guess one from a judge's proposed owner.
        .Where(record => record.GoalId.Equals(goalId, StringComparison.OrdinalIgnoreCase) && record.At > trigger)
        .OrderBy(record => record.At).ThenBy(record => record.Priority)
        .ThenBy(record => record.Id, StringComparer.Ordinal).Select(record => record.Resolution)
        .FirstOrDefault() ?? new(PanelResolutionKind.Unresolved, "unresolved");

    private static IReadOnlyList<ResolutionRecord> ReadResolutions(OrchestratorWorkspace workspace, IReadOnlyList<Goal> goals)
    {
        var intents = ReadIntents(workspace, goals);
        var records = new List<ResolutionRecord>(intents);
        foreach (var goal in goals)
        foreach (var entry in goal.Timeline)
        {
            var task = goal.Tasks.FirstOrDefault(task => task.Id == entry.TaskId);
            if (entry.Kind == ProgressKind.TaskRetried)
            {
                var nextDispatch = task?.DispatchHistory.Where(dispatch => dispatch.DispatchedAt > entry.OccurredAt)
                    .Select(dispatch => (DateTimeOffset?)dispatch.DispatchedAt).Min();
                if (intents.Any(intent => intent.PairsRetry &&
                        intent.GoalId.Equals(goal.Id.Value, StringComparison.OrdinalIgnoreCase) &&
                        intent.TaskId == entry.TaskId?.Value && intent.At >= entry.OccurredAt &&
                        (nextDispatch is null || intent.At < nextDispatch))) continue;
                records.Add(new($"retry:{entry.TaskId}:{entry.OccurredAt:O}", goal.Id.Value, entry.TaskId?.Value,
                    entry.OccurredAt, 1, new(PanelResolutionKind.ConductorRetry, $"{Role(task)}-retry-by-conductor")));
            }
            else if (entry.Kind == ProgressKind.GoalCancelled)
                records.Add(new($"cancel:{entry.OccurredAt:O}", goal.Id.Value, null, entry.OccurredAt, 1,
                    new(PanelResolutionKind.Cancelled, "cancelled")));
        }
        records.AddRange(ReadLandings(workspace));
        return records;
    }

    private static IReadOnlyList<ResolutionRecord> ReadIntents(OrchestratorWorkspace workspace, IReadOnlyList<Goal> goals)
    {
        var path = Path.Combine(workspace.OrchestratorDirectory, SqliteOperatorIntentStore.DatabaseFileName);
        if (!File.Exists(path))
        {
            Console.Error.WriteLine("Warning: owner-digest panel resolutions unavailable: operator-intents.db is missing.");
            return [];
        }
        try
        {
            using var connection = OpenReadOnly(path);
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT id, goal_id, task_id, verb, payload_json, completed_at FROM operator_intents
                WHERE status = 'Applied' AND verb IN ('retry', 'adjudicate', 'answer') AND completed_at IS NOT NULL
                """;
            using var reader = command.ExecuteReader();
            var records = new List<ResolutionRecord>();
            while (reader.Read())
            {
                if (!DateTimeOffset.TryParse(reader.GetString(5), CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var at)) continue;
                var goalId = reader.GetString(1);
                var taskId = reader.IsDBNull(2) ? null : reader.GetString(2);
                var verb = reader.GetString(3);
                var task = goals.FirstOrDefault(goal => goal.Id.Value.Equals(goalId, StringComparison.OrdinalIgnoreCase))
                    ?.Tasks.FirstOrDefault(task => task.Id.Value == taskId);
                Resolution? resolution = verb switch
                {
                    "retry" => new(PanelResolutionKind.OperatorRetry, $"operator-retry-{Role(task)}"),
                    "answer" => new(PanelResolutionKind.OperatorAnswer, "operator-answer"),
                    _ => Adjudication(reader.GetString(4))
                };
                if (resolution is not null)
                    records.Add(new(reader.GetString(0), goalId, taskId, at, 0, resolution,
                        verb is "retry" or "adjudicate"));
            }
            return records;
        }
        catch (SqliteException ex)
        {
            Console.Error.WriteLine($"Warning: owner-digest panel resolutions unavailable: {ex.Message}");
            return [];
        }
    }

    private static Resolution? Adjudication(string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("shape", out var shape) || shape.ValueKind != JsonValueKind.String)
                return null;
            return shape.GetString() switch
            {
                "close" => new(PanelResolutionKind.AdjudicateClose, "operator-adjudicate-close"),
                "route" => new(PanelResolutionKind.AdjudicateRoute, "operator-adjudicate-route"),
                "reopen-regate" => new(PanelResolutionKind.AdjudicateReopenRegate, "operator-adjudicate-reopen-regate"),
                _ => null
            };
        }
        catch (JsonException) { return null; }
    }

    private static IReadOnlyList<ResolutionRecord> ReadLandings(OrchestratorWorkspace workspace)
    {
        var records = new List<ResolutionRecord>();
        if (!Directory.Exists(workspace.GoalLifecycleEventsDirectory)) return records;
        foreach (var file in Directory.EnumerateFiles(workspace.GoalLifecycleEventsDirectory, "*.jsonl").Order(StringComparer.Ordinal))
        foreach (var line in File.ReadLines(file))
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (!root.TryGetProperty("eventType", out var kind) || kind.GetString() != "GoalLanded") continue;
                var goal = root.GetProperty("goalId").GetString()!;
                var at = root.GetProperty("timestamp").GetDateTimeOffset();
                records.Add(new($"landing:{goal}:{at:O}", goal, null, at, 2, new(PanelResolutionKind.Landed, "landed")));
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException) { }
        }
        return records;
    }

    private static string Role(TaskSpec? task) => task?.RequiredRole.ToString().ToLowerInvariant() ?? "task";
}
