using System.Globalization;
using Mcg.AgentOrchestrator.Core;
using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed partial class SqliteOrchestratorStateRepository
{
    [StoreSchemaSource("orchestrator-state", "src/Mcg.AgentOrchestrator.Execution/Persistence/SqliteOrchestratorStateRepository.PromotionFacts.cs")]
    internal static void ApplyPromotionFactsSchema(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS goal_promotion_facts (
                goal_id TEXT PRIMARY KEY,
                candidate_revision TEXT NOT NULL,
                prior_main_revision TEXT NOT NULL,
                promoted_main_revision TEXT NOT NULL,
                promoted_at TEXT NOT NULL,
                provenance TEXT NOT NULL
            )
            """;
        command.ExecuteNonQuery();
    }

    // Git and SQLite are separate durable boundaries. Callers capture time once
    // after Git acknowledges success and reuse that fact on persistence retries.
    public void RecordPromotionFacts(IReadOnlyList<GoalPromotionFact> facts) =>
        RecordPromotionFactsAsync(facts).GetAwaiter().GetResult();

    public async Task RecordPromotionFactsAsync(IReadOnlyList<GoalPromotionFact> facts,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(facts);
        if (facts.Count == 0 || facts.Select(fact => fact.GoalId).Distinct(StringComparer.Ordinal).Count() != facts.Count)
            throw new ArgumentException("Promotion attribution requires distinct goals.", nameof(facts));
        var first = facts[0];
        if (facts.Any(fact => fact.PriorMainRevision != first.PriorMainRevision ||
            fact.PromotedMainRevision != first.PromotedMainRevision || fact.PromotedAt != first.PromotedAt ||
            fact.Provenance != first.Provenance))
            throw new ArgumentException("Grouped attributions must describe one promotion.", nameof(facts));

        var write = await BeginWriteAsync(nameof(RecordPromotionFactsAsync), cancellationToken).ConfigureAwait(false);
        await using var connection = write.Connection;
        try
        {
            foreach (var fact in facts)
            {
                using var read = connection.CreateCommand();
                read.CommandText = """
                    SELECT goal_id, candidate_revision, prior_main_revision, promoted_main_revision, promoted_at, provenance
                    FROM goal_promotion_facts WHERE goal_id = $goal
                    """;
                read.Parameters.AddWithValue("$goal", fact.GoalId);
                using (var reader = read.ExecuteReader())
                {
                    if (reader.Read())
                    {
                        var original = ReadPromotionFact(reader, 0);
                        if (original is null || original.CandidateRevision != fact.CandidateRevision ||
                            original.PriorMainRevision != fact.PriorMainRevision ||
                            original.PromotedMainRevision != fact.PromotedMainRevision || original.Provenance != fact.Provenance)
                            throw new InvalidOperationException($"Original promotion identity conflicts for goal {fact.GoalId}.");
                        // Re-observation can never retime the original success.
                        continue;
                    }
                }
                using var insert = connection.CreateCommand();
                insert.CommandText = """
                    INSERT INTO goal_promotion_facts
                        (goal_id, candidate_revision, prior_main_revision, promoted_main_revision, promoted_at, provenance)
                    VALUES ($goal, $candidate, $prior, $main, $at, $provenance)
                    """;
                insert.Parameters.AddWithValue("$goal", fact.GoalId);
                insert.Parameters.AddWithValue("$candidate", fact.CandidateRevision);
                insert.Parameters.AddWithValue("$prior", fact.PriorMainRevision);
                insert.Parameters.AddWithValue("$main", fact.PromotedMainRevision);
                insert.Parameters.AddWithValue("$at", fact.PromotedAt.ToString("O", CultureInfo.InvariantCulture));
                insert.Parameters.AddWithValue("$provenance", fact.Provenance);
                _statementObserver?.Invoke(insert.CommandText);
                insert.ExecuteNonQuery();
            }
            await RunNonQueryAsync(connection, "COMMIT", cancellationToken).ConfigureAwait(false);
            write.Telemetry.Emit("commit");
        }
        catch (Exception exception)
        {
            try { await RunNonQueryAsync(connection, "ROLLBACK", CancellationToken.None).ConfigureAwait(false); } catch { }
            write.Telemetry.Emit("rollback", exception);
            throw;
        }
    }

    private static bool HasPromotionFactsTable(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_schema WHERE type = 'table' AND name = 'goal_promotion_facts'";
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture) == 1;
    }

    private static GoalPromotionFact? ReadPromotionFact(SqliteDataReader reader, int offset)
    {
        for (var index = offset; index < offset + 6; index++)
            if (reader.IsDBNull(index)) return null;
        if (!DateTimeOffset.TryParseExact(reader.GetString(offset + 4), "O", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var at)) return null;
        try
        {
            return new(reader.GetString(offset), reader.GetString(offset + 1), reader.GetString(offset + 2),
                reader.GetString(offset + 3), at, reader.GetString(offset + 5));
        }
        catch (ArgumentException)
        {
            // Legacy or incompatible proof stays explicitly unknown; no guessed date.
            return null;
        }
    }
}
