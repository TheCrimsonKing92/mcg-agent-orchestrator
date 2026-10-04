using System.Text.Json;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorJudgePanelTriggerSources
{
    private IEnumerable<PanelTrigger> ReadAuthor(IReadOnlyList<OwnerConductEvent> conduct)
    {
        if (!File.Exists(authorPath)) yield break;
        using var connection = OpenReadOnly(authorPath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT identity, claimed_at, item_json FROM author_claims WHERE outcome = 'owner-question'";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            PanelTrigger? trigger = null;
            try
            {
                var identity = reader.GetString(0);
                var item = JsonSerializer.Deserialize<ConductorAuthorItem>(reader.GetString(2));
                if (item is not null && DateTimeOffset.TryParse(reader.GetString(1), out var claimedAt))
                {
                    // The claim retains the original question, not an operator's later answer.
                    var escalation = conduct.FirstOrDefault(evt => evt.GoalId == item.GoalId &&
                        evt.Timestamp >= claimedAt && evt.EventKind == "goal-escalation" &&
                        evt.Detail.StartsWith($"author-owner-question item={item.TargetKind}:{item.TargetId} ", StringComparison.Ordinal));
                    trigger = new(PanelTriggerKind.AuthorAskOwner, item.GoalId, PanelTrigger.UnrecordedSha,
                        PanelTrigger.UnrecordedSha, "author-claim:" + identity,
                        escalation?.Timestamp ?? claimedAt, escalation?.Detail ?? item.Question, [], "ask-owner");
                }
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException) { }
            if (trigger is not null) yield return trigger;
        }
    }

    private IEnumerable<PanelTrigger> ReadCohort()
    {
        if (!File.Exists(cohortPath)) yield break;
        using var connection = OpenReadOnly(cohortPath);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT r.receipt_id, r.main_revision, r.completed_at, r.gate_test_result_paths_json,
                   r.attribution_source, m.goal_id, m.candidate_revision
            FROM cohort_receipts r JOIN cohort_members m ON m.cohort_id = r.cohort_id
            WHERE m.member_ordinal = 0 AND (r.attribution = 'BothMembersFailed' OR r.attribution_source = 'main-suspect')
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            PanelTrigger? trigger = null;
            try
            {
                if (DateTimeOffset.TryParse(reader.GetString(2), out var at))
                {
                    var id = reader.GetString(0);
                    var detail = reader.IsDBNull(4) ? "BothMembersFailed" : reader.GetString(4);
                    trigger = new(PanelTriggerKind.CohortBothFailed, reader.GetString(5), reader.GetString(6),
                        reader.GetString(1), "cohort-receipt:" + id, at,
                        $"Cohort receipt {id}: BothMembersFailed; attribution_source={detail}",
                        JsonSerializer.Deserialize<string[]>(reader.GetString(3)) ?? [], detail);
                }
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException) { }
            if (trigger is not null) yield return trigger;
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
