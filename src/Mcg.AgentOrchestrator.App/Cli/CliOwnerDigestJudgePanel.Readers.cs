using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static partial class CliOwnerDigestJudgePanel
{
    internal static string StorePath(OrchestratorWorkspace workspace) =>
        Path.Combine(workspace.OrchestratorDirectory, "judge-panel.db");

    internal static Result? Read(OrchestratorWorkspace workspace, OwnerDigestResult digest, IReadOnlyList<Goal> goals)
    {
        if (!File.Exists(StorePath(workspace))) return null;
        try
        {
            var stored = ReadCases(StorePath(workspace));
            var triggers = ReadTriggerTimes(workspace);
            var resolutions = ReadResolutions(workspace, goals);
            var missing = stored.Count(item => !triggers.ContainsKey(item.Key.TriggerId));
            var cases = stored.Where(item => triggers.TryGetValue(item.Key.TriggerId, out var at) &&
                    at >= digest.Since && at < digest.Until)
                .Select(item => new Case(item.Id, item.Key.GoalId, item.Key.TriggerKind,
                    triggers[item.Key.TriggerId], item.Terminal, item.Judges,
                    Resolve(item.Key.GoalId, triggers[item.Key.TriggerId], resolutions)))
                .OrderBy(item => item.TriggerAt).ThenBy(item => item.Id, StringComparer.Ordinal).ToArray();
            return new(cases, missing);
        }
        catch (Exception ex) when (ex is SqliteException or IOException or JsonException)
        {
            Console.Error.WriteLine($"Warning: owner-digest judge panel unavailable: {ex.Message}");
            return null;
        }
    }

    private sealed record StoredCase(string Id, PanelCaseKey Key, string Terminal, List<Judge> Judges);

    private static IReadOnlyList<StoredCase> ReadCases(string path)
    {
        using var connection = OpenReadOnly(path);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT c.case_id, c.key_json, c.terminal, j.judge, j.launched, j.outcome, j.result_json
            FROM panel_cases c LEFT JOIN panel_calls j ON j.case_id = c.case_id
            WHERE c.status = 'terminal' AND c.admitted = 1 ORDER BY c.case_id, j.judge
            """;
        using var reader = command.ExecuteReader();
        var cases = new Dictionary<string, StoredCase>(StringComparer.Ordinal);
        while (reader.Read())
        {
            var id = reader.GetString(0);
            if (!cases.TryGetValue(id, out var item))
            {
                var key = JsonSerializer.Deserialize<PanelCaseKey>(reader.GetString(1))
                    ?? throw new JsonException("Panel case key is null.");
                item = new(id, key, reader.GetString(2), []);
                cases.Add(id, item);
            }
            if (reader.IsDBNull(3)) continue;
            var outcome = reader.IsDBNull(5) ? "pending" : reader.GetString(5);
            string? kind = null, owner = null;
            if (outcome == "valid" && !reader.IsDBNull(6))
            {
                try
                {
                    var result = JsonSerializer.Deserialize<PanelJudgeResult>(reader.GetString(6));
                    if (PanelV0Contract.Validate(result?.Answer, id) == PanelJudgeOutcome.Valid)
                    {
                        using var answer = JsonDocument.Parse(result!.Answer!);
                        var action = answer.RootElement.GetProperty("next_action");
                        kind = action.GetProperty("kind").GetString();
                        owner = action.GetProperty("owner").GetString();
                    }
                }
                catch (JsonException) { /* The recorded outcome remains visible; no action is inferred. */ }
            }
            item.Judges.Add(new(reader.GetString(3), reader.GetInt32(4) == 1, outcome, kind, owner));
        }
        return cases.Values.ToArray();
    }

    private static IReadOnlyDictionary<string, DateTimeOffset> ReadTriggerTimes(OrchestratorWorkspace workspace)
    {
        try
        {
            return new ConductorJudgePanelTriggerSources(workspace.GoalLifecycleEventsDirectory,
                    workspace.ConductEventsLogPath, Path.Combine(workspace.OrchestratorDirectory, "author-claims.db"),
                    Path.Combine(workspace.OrchestratorDirectory, "cohort-acceptance.db"), workspace.SqliteStatePath)
                .Read().GroupBy(trigger => trigger.TriggerId, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Min(trigger => trigger.RecordedAt), StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is SqliteException or IOException)
        {
            Console.Error.WriteLine($"Warning: owner-digest panel trigger times unavailable: {ex.Message}");
            return new Dictionary<string, DateTimeOffset>();
        }
    }

    private static SqliteConnection OpenReadOnly(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        try { connection.Open(); return connection; }
        catch { connection.Dispose(); throw; }
    }
}
