using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed partial class CohortAcceptanceStore
{
    public IReadOnlyList<AcceptanceCohortPartitionReceipt> ReadPartitionReceipts(string cohortId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cohortId);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT receipt_id, goal_id, member_ordinal, candidate_revision, main_revision,
                   tree_revision, manifest_identity, outcome, elapsed_ms, test_result_paths_json,
                   failed_checks_json, failing_test_identities_json
            FROM cohort_partition_receipts WHERE cohort_id=$cohort ORDER BY member_ordinal;
            """;
        command.Parameters.AddWithValue("$cohort", cohortId);
        var legacyAttribution = ReadReceipt(connection, cohortId, transaction: null);
        using var reader = command.ExecuteReader();
        var result = new List<AcceptanceCohortPartitionReceipt>();
        while (reader.Read())
        {
            var receipt = new AcceptanceCohortPartitionReceipt(
                reader.GetString(0), new GoalId(reader.GetString(1)), reader.GetInt32(2),
                reader.GetString(3), reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.GetString(6), Enum.Parse<AcceptanceCohortGateOutcome>(reader.GetString(7)),
                reader.GetInt64(8), JsonSerializer.Deserialize<string[]>(reader.GetString(9)) ?? [])
            {
                FailedChecks = reader.IsDBNull(10)
                    ? []
                    : JsonSerializer.Deserialize<string[]>(reader.GetString(10)) ?? [],
                FailingTestIdentities = reader.IsDBNull(11)
                    ? legacyAttribution?.AttributedMembers
                        .Where(member => member.GoalId.Value == reader.GetString(1))
                        .SelectMany(member => member.ReproducedFailingTests).ToArray() ?? []
                    : JsonSerializer.Deserialize<string[]>(reader.GetString(11)) ?? []
            };
            result.Add(receipt);
        }
        return result;
    }

}
