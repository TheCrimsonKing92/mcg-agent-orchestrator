using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed record CohortFailureEvidence(string CohortId, IReadOnlyList<GoalId> MemberGoalIds,
    string ObservedMainRevision, IReadOnlyList<string>? CohortFailingTests,
    IReadOnlyList<AcceptanceCohortAttributedMember> AttributedMembers);

public sealed record CohortAttributionRetraction(string EarlierCohortId, GoalId GoalId,
    string CandidateRevision, string LaterCohortId, int FailingTestCount);

public sealed partial class CohortAcceptanceStore
{
    public IReadOnlyList<CohortFailureEvidence> ReadFailedCohortEvidence(string observedMainRevision,
        string excludingCohortId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT cohort_id, main_revision, cohort_failing_tests_json, attributed_members_json
            FROM cohort_receipts
            WHERE outcome='Failed' AND main_revision=$main AND cohort_id<>$excluded
            ORDER BY completed_at, cohort_id;
            """;
        command.Parameters.AddWithValue("$main", observedMainRevision);
        command.Parameters.AddWithValue("$excluded", excludingCohortId);
        var rows = new List<(string Id, string Main, string[]? Tests, AcceptanceCohortAttributedMember[] Members)>();
        using (var reader = command.ExecuteReader())
            while (reader.Read())
                rows.Add((reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null :
                    JsonSerializer.Deserialize<string[]>(reader.GetString(2)) ?? [],
                    JsonSerializer.Deserialize<AcceptanceCohortAttributedMember[]>(reader.GetString(3)) ?? []));
        var result = new List<CohortFailureEvidence>();
        foreach (var row in rows)
        {
            using var members = connection.CreateCommand();
            members.CommandText = "SELECT goal_id FROM cohort_members WHERE cohort_id=$cohort ORDER BY member_ordinal;";
            members.Parameters.AddWithValue("$cohort", row.Id);
            using var reader = members.ExecuteReader();
            var goals = new List<GoalId>();
            while (reader.Read()) goals.Add(new GoalId(reader.GetString(0)));
            // Keep original blame as evidence even after retraction: a crash before the later
            // attribution save must replay the circuit and the deduplicated operator event.
            result.Add(new(row.Id, goals, row.Main, row.Tests, row.Members));
        }
        return result;
    }

    public CohortAttributionRetraction RecordAttributionRetraction(string earlierCohortId,
        GoalId goalId, string candidateRevision, string laterCohortId, int failingTestCount)
    {
        if (failingTestCount <= 0) throw new ArgumentOutOfRangeException(nameof(failingTestCount));
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO cohort_attribution_retractions(
                earlier_cohort_id, goal_id, candidate_revision, later_cohort_id, failing_test_count)
            VALUES ($earlier, $goal, $candidate, $later, $count)
            ON CONFLICT(earlier_cohort_id, goal_id, candidate_revision) DO NOTHING;
            SELECT later_cohort_id, failing_test_count FROM cohort_attribution_retractions
            WHERE earlier_cohort_id=$earlier AND goal_id=$goal AND candidate_revision=$candidate;
            """;
        command.Parameters.AddWithValue("$earlier", earlierCohortId);
        command.Parameters.AddWithValue("$goal", goalId.Value);
        command.Parameters.AddWithValue("$candidate", candidateRevision);
        command.Parameters.AddWithValue("$later", laterCohortId);
        command.Parameters.AddWithValue("$count", failingTestCount);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new InvalidOperationException("Cohort attribution retraction was not persisted.");
        return new(earlierCohortId, goalId, candidateRevision, reader.GetString(0), reader.GetInt32(1));
    }

    public string? ReadAttributionReasonDetail(string cohortId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT attribution_reason_detail FROM cohort_receipts WHERE cohort_id=$cohort;";
        command.Parameters.AddWithValue("$cohort", cohortId);
        return command.ExecuteScalar() as string;
    }

    private static void EnsureRetractionSchema(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS cohort_attribution_retractions(
                earlier_cohort_id TEXT NOT NULL REFERENCES cohort_receipts(cohort_id) ON DELETE CASCADE,
                goal_id TEXT NOT NULL,
                candidate_revision TEXT NOT NULL,
                later_cohort_id TEXT NOT NULL,
                failing_test_count INTEGER NOT NULL CHECK(failing_test_count>0),
                PRIMARY KEY(earlier_cohort_id, goal_id, candidate_revision));
            """;
        command.ExecuteNonQuery();
    }
}
