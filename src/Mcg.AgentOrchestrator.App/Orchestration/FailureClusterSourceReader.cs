using System.Globalization;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record FailureClusterInputs(IReadOnlyList<FailureClusterGoalEvent> GoalEvents,
    IReadOnlyList<FailureClusterConductEvent> ConductEvents,
    IReadOnlyList<FailureClusterOperatorTouch> OperatorTouches, int SkippedGoalEvents = 0,
    int SkippedConductEvents = 0, int SkippedOperatorIntents = 0)
{
    internal IReadOnlyList<FailureCluster> Build(DateTimeOffset since, DateTimeOffset until) =>
        FailureClusterReport.Build(GoalEvents, ConductEvents, OperatorTouches, since, until);
}

// This source owns no writer repository. History before Since preserves root and dispatch boundaries.
internal static class FailureClusterSourceReader
{
    internal static FailureClusterInputs Read(OrchestratorWorkspace workspace, DateTimeOffset until)
    {
        var goals = new List<FailureClusterGoalEvent>();
        var conduct = new List<FailureClusterConductEvent>();
        var skippedGoals = 0;
        var skippedConduct = 0;
        foreach (var path in Files(workspace.GoalLifecycleEventsDirectory, "*.jsonl"))
            ReadJsonLines(path, e =>
            {
                var at = e.GetProperty("timestamp").GetDateTimeOffset();
                if (at < until) goals.Add(new(Required(e, "eventType"),
                    Optional(e, "message") ?? "", Required(e, "goalId"),
                    Optional(e, "taskId"), at));
            }, ref skippedGoals);
        foreach (var path in ConductPaths(workspace.ConductEventsLogPath))
            ReadJsonLines(path, e =>
            {
                var at = e.GetProperty("timestamp").GetDateTimeOffset();
                if (at < until) conduct.Add(new(at, Required(e, "eventKind"),
                    Optional(e, "goalId"), Optional(e, "detail") ?? ""));
            }, ref skippedConduct);
        var touches = ReadTouches(workspace, until, out var skippedIntents);
        return new(goals, conduct, touches, skippedGoals, skippedConduct, skippedIntents);
    }

    internal static string[] ConductPaths(string path) =>
        Files(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + "-*" + Path.GetExtension(path))
            .Concat(File.Exists(path) ? [path] : Array.Empty<string>()).Order(StringComparer.Ordinal).ToArray();

    private static string[] Files(string directory, string pattern)
    {
        try { return Directory.Exists(directory) ? Directory.GetFiles(directory, pattern) : []; }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
    }

    internal static void ReadJsonLines(string path, Action<JsonElement> consume, ref int skipped)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(file);
            while (reader.ReadLine() is { } line)
            {
                try
                {
                    using var json = JsonDocument.Parse(line);
                    consume(json.RootElement);
                }
                catch (Exception ex) when (ex is JsonException or KeyNotFoundException or
                    InvalidOperationException or FormatException or ArgumentException) { skipped++; }
            }
        }
        catch (IOException) { skipped++; }
        catch (UnauthorizedAccessException) { skipped++; }
    }

    private static string? Optional(JsonElement e, string key) =>
        e.TryGetProperty(key, out var value) && value.ValueKind != JsonValueKind.Null ? value.GetString() : null;

    private static string Required(JsonElement e, string key) =>
        e.GetProperty(key).GetString() ?? throw new FormatException("Null " + key + ".");

    private static IReadOnlyList<FailureClusterOperatorTouch> ReadTouches(OrchestratorWorkspace workspace,
        DateTimeOffset until, out int skipped)
    {
        skipped = 0;
        var path = Path.Combine(workspace.OrchestratorDirectory, SqliteOperatorIntentStore.DatabaseFileName);
        if (!File.Exists(path)) return [];
        try
        {
            // Same strictly read-only connection policy as CliOwnerDigestRetryIntents; all intent verbs count.
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            connection.Open();
            using var schema = connection.CreateCommand();
            schema.CommandText = "SELECT COUNT(*) FROM pragma_table_info('operator_intents') WHERE name = 'actor'";
            var hasActor = Convert.ToInt64(schema.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
            if (!hasActor)
                Console.Error.WriteLine("Warning: failure-clusters operator_intents has no actor column; counting every intent as an operator touch.");
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT goal_id, task_id, created_at FROM operator_intents" +
                (hasActor ? " WHERE actor = 'operator'" : "");
            using var reader = command.ExecuteReader();
            var touches = new List<FailureClusterOperatorTouch>();
            while (reader.Read())
            {
                if (reader.IsDBNull(0) || reader.IsDBNull(2) ||
                    !DateTimeOffset.TryParse(reader.GetString(2), CultureInfo.InvariantCulture, DateTimeStyles.None, out var at))
                { skipped++; continue; }
                if (at < until) touches.Add(new(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), at));
            }
            return touches;
        }
        catch (SqliteException ex)
        {
            skipped++;
            Console.Error.WriteLine($"Warning: failure-clusters operator intents unavailable: {ex.Message}");
            return [];
        }
    }
}
