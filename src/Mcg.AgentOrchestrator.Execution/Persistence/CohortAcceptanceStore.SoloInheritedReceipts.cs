using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed record SoloInheritedReceipt(GoalId GoalId, string CandidateRevision,
    string ObservedMainRevision, string MergeBaseRevision,
    IReadOnlyList<string> InheritedTests, IReadOnlyList<string> ChangedPaths);

public sealed partial class CohortAcceptanceStore
{
    public void RecordSoloInheritedReceipt(SoloInheritedReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (receipt.InheritedTests.Count == 0 ||
            !string.Equals(receipt.MergeBaseRevision, receipt.ObservedMainRevision, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Solo inherited evidence must reproduce failures at observed main.", nameof(receipt));
        using var connection = Open();
        EnsureSoloInheritedReceiptSchema(connection);
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO solo_inherited_receipts(
                goal_id, candidate_revision, observed_main_revision, merge_base_revision,
                inherited_tests_json, changed_paths_json)
            VALUES ($goal, $candidate, $main, $baseline, $tests, $paths)
            ON CONFLICT(goal_id, candidate_revision, observed_main_revision) DO NOTHING;
            """;
        command.Parameters.AddWithValue("$goal", receipt.GoalId.Value);
        command.Parameters.AddWithValue("$candidate", receipt.CandidateRevision);
        command.Parameters.AddWithValue("$main", receipt.ObservedMainRevision);
        command.Parameters.AddWithValue("$baseline", receipt.MergeBaseRevision);
        command.Parameters.AddWithValue("$tests", JsonSerializer.Serialize(receipt.InheritedTests));
        command.Parameters.AddWithValue("$paths", JsonSerializer.Serialize(receipt.ChangedPaths));
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<SoloInheritedReceipt> ReadSoloInheritedReceipts(
        string observedMainRevision, GoalId excludingGoal, string excludingCandidate)
    {
        using var connection = Open();
        EnsureSoloInheritedReceiptSchema(connection);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT goal_id, candidate_revision, observed_main_revision, merge_base_revision,
                inherited_tests_json, changed_paths_json
            FROM solo_inherited_receipts
            WHERE observed_main_revision=$main AND goal_id<>$goal AND candidate_revision<>$candidate
            ORDER BY sequence, goal_id;
            """;
        command.Parameters.AddWithValue("$main", observedMainRevision);
        command.Parameters.AddWithValue("$goal", excludingGoal.Value);
        command.Parameters.AddWithValue("$candidate", excludingCandidate);
        using var reader = command.ExecuteReader();
        var receipts = new List<SoloInheritedReceipt>();
        while (reader.Read())
            receipts.Add(new(new GoalId(reader.GetString(0)), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), JsonSerializer.Deserialize<string[]>(reader.GetString(4))
                    ?? throw new InvalidDataException("Solo inherited identities are missing."),
                JsonSerializer.Deserialize<string[]>(reader.GetString(5))
                    ?? throw new InvalidDataException("Solo candidate changed paths are missing.")));
        return receipts;
    }

    private static void EnsureSoloInheritedReceiptSchema(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS solo_inherited_receipts(
                sequence INTEGER PRIMARY KEY AUTOINCREMENT,
                goal_id TEXT NOT NULL,
                candidate_revision TEXT NOT NULL,
                observed_main_revision TEXT NOT NULL,
                merge_base_revision TEXT NOT NULL,
                inherited_tests_json TEXT NOT NULL,
                changed_paths_json TEXT NOT NULL,
                UNIQUE(goal_id, candidate_revision, observed_main_revision));
            CREATE INDEX IF NOT EXISTS ix_solo_inherited_receipts_main
                ON solo_inherited_receipts(observed_main_revision, sequence);
            """;
        command.ExecuteNonQuery();
    }
}
