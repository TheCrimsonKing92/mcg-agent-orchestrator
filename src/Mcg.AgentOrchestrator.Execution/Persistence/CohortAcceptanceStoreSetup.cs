using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.Infrastructure;

public static class CohortAcceptanceStoreSetup
{
    private static readonly StoreSchemaEntry[] Entries =
    [
        StoreSchemaRegistry.CohortAcceptance,
        StoreSchemaRegistry.CohortAttributionRetractions,
        StoreSchemaRegistry.CohortSoloInheritedReceipts
    ];

    public static void Setup(string dbPath)
    {
        var directory = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = false
        }.ToString());
        connection.Open();
        RunNonQuery(connection, "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;");
        if (Entries.All(entry => IsCurrentOrNewer(connection, entry)))
            return;

        RunNonQuery(connection, "BEGIN IMMEDIATE");
        try
        {
            // Recheck under the write lock in case another setup completed while we waited.
            if (!Entries.All(entry => IsCurrentOrNewer(connection, entry)))
            {
                CreateSchema(connection);
                foreach (var entry in Entries)
                {
                    if (!IsCurrentOrNewer(connection, entry))
                        StoreSchemaVersions.UpgradeToCurrent(connection, entry);
                }
            }
            RunNonQuery(connection, "COMMIT");
        }
        catch
        {
            try { RunNonQuery(connection, "ROLLBACK"); } catch { }
            throw;
        }
    }

    private static bool IsCurrentOrNewer(SqliteConnection connection, StoreSchemaEntry entry) =>
        StoreSchemaVersions.Verify(connection, entry) is StoreSchemaState.Current or StoreSchemaState.Newer;

    private static void RunNonQuery(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void CreateSchema(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS cohort_receipts(
                cohort_id TEXT PRIMARY KEY,
                receipt_id TEXT NOT NULL UNIQUE,
                main_revision TEXT NOT NULL,
                combined_tree_revision TEXT NOT NULL,
                manifest_identity TEXT NOT NULL,
                outcome TEXT NOT NULL,
                attribution TEXT NOT NULL,
                valid_for_landing INTEGER NOT NULL,
                completed_at TEXT NOT NULL,
                gate_elapsed_ms INTEGER NOT NULL,
                failed_checks_json TEXT NOT NULL,
                infrastructure_reason_code TEXT NULL,
                infrastructure_detail TEXT NULL,
                gate_exit_code INTEGER NULL,
                gate_test_result_paths_json TEXT NOT NULL DEFAULT '[]',
                gate_evidence_artifacts_json TEXT NOT NULL DEFAULT '[]');
            CREATE TABLE IF NOT EXISTS cohort_members(
                cohort_id TEXT NOT NULL REFERENCES cohort_receipts(cohort_id) ON DELETE CASCADE,
                member_ordinal INTEGER NOT NULL CHECK(member_ordinal IN (0,1)),
                goal_id TEXT NOT NULL,
                branch_revision TEXT NOT NULL,
                candidate_revision TEXT NOT NULL,
                landing_paths_json TEXT NOT NULL,
                resource_keys_json TEXT NOT NULL,
                risk_tier TEXT NOT NULL,
                promotion_disposition TEXT NOT NULL,
                merge_status TEXT NOT NULL,
                merge_reason TEXT NOT NULL,
                landed INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY(cohort_id, member_ordinal),
                UNIQUE(cohort_id, goal_id));
            CREATE TABLE IF NOT EXISTS cohort_landing_intents(
                cohort_id TEXT PRIMARY KEY REFERENCES cohort_receipts(cohort_id) ON DELETE CASCADE,
                receipt_id TEXT NOT NULL,
                combined_commit_revision TEXT NOT NULL,
                prior_integration_revision TEXT NULL,
                state TEXT NOT NULL CHECK(state IN ('prepared','finalized','invalidated')),
                updated_at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS cohort_partition_receipts(
                cohort_id TEXT NOT NULL REFERENCES cohort_receipts(cohort_id) ON DELETE CASCADE,
                member_ordinal INTEGER NOT NULL CHECK(member_ordinal IN (0,1)),
                receipt_id TEXT NOT NULL,
                goal_id TEXT NOT NULL,
                candidate_revision TEXT NOT NULL,
                main_revision TEXT NOT NULL,
                tree_revision TEXT NULL,
                manifest_identity TEXT NOT NULL,
                outcome TEXT NOT NULL,
                elapsed_ms INTEGER NOT NULL,
                test_result_paths_json TEXT NOT NULL,
                PRIMARY KEY(cohort_id, member_ordinal));
            CREATE TABLE IF NOT EXISTS cohort_fairness(
                goal_id TEXT PRIMARY KEY,
                overtake_count INTEGER NOT NULL,
                updated_at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS cohort_pair_suppressions(
                pair_fingerprint TEXT PRIMARY KEY,
                cohort_id TEXT NOT NULL,
                created_at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS cohort_attribution_effects(
                cohort_id TEXT PRIMARY KEY REFERENCES cohort_receipts(cohort_id) ON DELETE CASCADE,
                attribution TEXT NOT NULL,
                pair_fingerprint TEXT NOT NULL,
                innocent_goal_id TEXT NULL,
                applied_at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS cohort_outcome_consumptions(
                cohort_id TEXT PRIMARY KEY REFERENCES cohort_receipts(cohort_id) ON DELETE CASCADE,
                receipt_id TEXT NOT NULL,
                consumed_at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS cohort_materialization_failures(
                attempt_id TEXT PRIMARY KEY,
                first_goal_id TEXT NOT NULL,
                first_branch_revision TEXT NOT NULL,
                first_candidate_revision TEXT NOT NULL,
                second_goal_id TEXT NOT NULL,
                second_branch_revision TEXT NOT NULL,
                second_candidate_revision TEXT NOT NULL,
                main_revision TEXT NOT NULL,
                outcome TEXT NOT NULL,
                detail TEXT NOT NULL,
                recorded_at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS cohort_invalidations(
                invalidation_id TEXT PRIMARY KEY,
                cohort_id TEXT NOT NULL REFERENCES cohort_receipts(cohort_id) ON DELETE CASCADE,
                reason TEXT NOT NULL,
                detail TEXT NOT NULL,
                invalidated_at TEXT NOT NULL,
                observed_main_revision TEXT NOT NULL,
                combined_tree_revision TEXT NOT NULL,
                manifest_identity TEXT NOT NULL,
                ordered_member_bindings_json TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_cohort_invalidations_cohort
                ON cohort_invalidations(cohort_id, invalidated_at);
            """;
        command.ExecuteNonQuery();
        EnsureColumn(connection, "cohort_members", "risk_tier", "TEXT NOT NULL DEFAULT 'DocsOnly'");
        EnsureColumn(connection, "cohort_members", "promotion_disposition", "TEXT NOT NULL DEFAULT 'Auto'");
        EnsureColumn(connection, "cohort_members", "merge_status", "TEXT NOT NULL DEFAULT 'Clean'");
        EnsureColumn(connection, "cohort_members", "merge_reason", "TEXT NOT NULL DEFAULT 'NoConflictsDetected'");
        EnsureColumn(connection, "cohort_receipts", "gate_exit_code", "INTEGER NULL");
        EnsureColumn(connection, "cohort_receipts", "infrastructure_reason_code", "TEXT NULL");
        EnsureColumn(connection, "cohort_receipts", "infrastructure_detail", "TEXT NULL");
        EnsureColumn(connection, "cohort_receipts", "gate_test_result_paths_json", "TEXT NOT NULL DEFAULT '[]'");
        EnsureColumn(connection, "cohort_receipts", "gate_evidence_artifacts_json", "TEXT NOT NULL DEFAULT '[]'");
        EnsureColumn(connection, "cohort_receipts", "attributed_members_json", "TEXT NOT NULL DEFAULT '[]'");
        EnsureColumn(connection, "cohort_receipts", "unrelated_failures_json", "TEXT NOT NULL DEFAULT '[]'");
        EnsureColumn(connection, "cohort_receipts", "attribution_source", "TEXT NULL");
        EnsureColumn(connection, "cohort_receipts", "cohort_failing_tests_json", "TEXT NULL");
        EnsureColumn(connection, "cohort_receipts", "attribution_reason_detail", "TEXT NULL");
        EnsureRetractionSchema(connection);
        EnsureColumn(connection, "cohort_landing_intents", "prior_integration_revision", "TEXT NULL");
        EnsureReusablePartitionReceiptSchema(connection);
        EnsureColumn(connection, "cohort_partition_receipts", "failed_checks_json", "TEXT NULL");
        EnsureColumn(connection, "cohort_partition_receipts", "failing_test_identities_json", "TEXT NULL");
        EnsureSoloInheritedReceiptSchema(connection);
    }

    private static void EnsureReusablePartitionReceiptSchema(SqliteConnection connection)
    {
        using var inspect = connection.CreateCommand();
        inspect.CommandText = "SELECT sql FROM sqlite_master WHERE type='table' AND name='cohort_partition_receipts';";
        var schema = inspect.ExecuteScalar() as string;
        if (schema?.Contains("receipt_id TEXT NOT NULL UNIQUE", StringComparison.OrdinalIgnoreCase) != true)
        {
            return;
        }

        using var migrate = connection.CreateCommand();
        migrate.CommandText = """
            ALTER TABLE cohort_partition_receipts RENAME TO cohort_partition_receipts_legacy;
            CREATE TABLE cohort_partition_receipts(
                cohort_id TEXT NOT NULL REFERENCES cohort_receipts(cohort_id) ON DELETE CASCADE,
                member_ordinal INTEGER NOT NULL CHECK(member_ordinal IN (0,1)),
                receipt_id TEXT NOT NULL,
                goal_id TEXT NOT NULL,
                candidate_revision TEXT NOT NULL,
                main_revision TEXT NOT NULL,
                tree_revision TEXT NULL,
                manifest_identity TEXT NOT NULL,
                outcome TEXT NOT NULL,
                elapsed_ms INTEGER NOT NULL,
                test_result_paths_json TEXT NOT NULL,
                PRIMARY KEY(cohort_id, member_ordinal));
            INSERT INTO cohort_partition_receipts(
                cohort_id, member_ordinal, receipt_id, goal_id, candidate_revision,
                main_revision, tree_revision, manifest_identity, outcome, elapsed_ms, test_result_paths_json)
            SELECT cohort_id, member_ordinal, receipt_id, goal_id, candidate_revision,
                main_revision, tree_revision, manifest_identity, outcome, elapsed_ms, test_result_paths_json
            FROM cohort_partition_receipts_legacy;
            DROP TABLE cohort_partition_receipts_legacy;
            """;
        migrate.ExecuteNonQuery();
    }

    private static void EnsureColumn(SqliteConnection connection, string table, string column, string definition)
    {
        using var inspect = connection.CreateCommand();
        inspect.CommandText = $"PRAGMA table_info({table});";
        using var reader = inspect.ExecuteReader();
        while (reader.Read())
        {
            if (reader.GetString(1).Equals(column, StringComparison.OrdinalIgnoreCase)) return;
        }
        reader.Close();
        using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition};";
        alter.ExecuteNonQuery();
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
