using System.Text.Json;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record PanelTriggerFailure(string TriggerId, string GoalId, string Error);

internal sealed partial class ConductorJudgePanelCaseStore
{
    private const string TriggerFailurePrefix = "trigger-failure:";

    internal void RecordTriggerFailure(string triggerId, string goalId, Exception exception)
    {
        using var connection = Open();
        var failure = new PanelTriggerFailure(triggerId, goalId,
            exception.GetType().Name + ": " + exception.Message);
        // Independent diagnostics do not admit a case, consume a judge call or change slice 1 terminals.
        Execute(connection, null,
            "INSERT INTO panel_meta VALUES ($key, $value) ON CONFLICT(key) DO UPDATE SET value = excluded.value",
            ("$key", TriggerFailurePrefix + Hash(JsonSerializer.Serialize(new[] { goalId, triggerId }))),
            ("$value", JsonSerializer.Serialize(failure)));
    }

    internal IReadOnlyList<PanelTriggerFailure> TriggerFailures()
    {
        using var connection = Open();
        using var command = Command(connection, null,
            "SELECT value FROM panel_meta WHERE key LIKE $prefix ORDER BY key", ("$prefix", TriggerFailurePrefix + "%"));
        using var reader = command.ExecuteReader();
        var failures = new List<PanelTriggerFailure>();
        while (reader.Read()) failures.Add(JsonSerializer.Deserialize<PanelTriggerFailure>(reader.GetString(0))!);
        return failures;
    }
}
