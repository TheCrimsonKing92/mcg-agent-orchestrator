using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed partial class CohortAcceptanceStore
{
    public IReadOnlyList<AcceptanceCohortReceipt> ReadUnconsumedTerminalFailedReceipts()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT r.cohort_id FROM cohort_receipts r
            LEFT JOIN cohort_outcome_consumptions c ON c.cohort_id=r.cohort_id
            WHERE r.outcome='Failed' AND r.attribution<>'NotApplicable' AND c.cohort_id IS NULL;
            """;
        var cohortIds = new List<string>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read()) cohortIds.Add(reader.GetString(0));
        }
        return cohortIds.Select(id => ReadReceipt(connection, id, transaction: null))
            .OfType<AcceptanceCohortReceipt>()
            .OrderBy(receipt => receipt.CompletedAt)
            .ThenBy(receipt => receipt.ReceiptId, StringComparer.Ordinal)
            .ToArray();
    }

    public bool TryRecordOutcomeConsumption(string cohortId, string receiptId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cohortId);
        ArgumentException.ThrowIfNullOrWhiteSpace(receiptId);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO cohort_outcome_consumptions(cohort_id, receipt_id, consumed_at)
            VALUES($cohort, $receipt, $at);
            """;
        command.Parameters.AddWithValue("$cohort", cohortId);
        command.Parameters.AddWithValue("$receipt", receiptId);
        command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
        return command.ExecuteNonQuery() == 1;
    }

    public IReadOnlySet<string> ReadInteractionOnlyMemberKeys()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT m.goal_id, m.candidate_revision FROM cohort_members m
            JOIN cohort_receipts r ON r.cohort_id=m.cohort_id
            WHERE r.attribution='InteractionOnly';
            """;
        using var reader = command.ExecuteReader();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read()) keys.Add($"{reader.GetString(0)}:{reader.GetString(1)}");
        return keys;
    }
}
