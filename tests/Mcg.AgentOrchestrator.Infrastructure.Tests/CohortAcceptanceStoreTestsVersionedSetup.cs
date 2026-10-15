using System.Text.Json;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

// Parallel-safe: each test owns a unique temporary directory; connections disable pooling.
public sealed class CohortAcceptanceStoreTestsVersionedSetup : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "cohort-setup-" + Guid.NewGuid().ToString("N"));
    private string Db => Path.Combine(root, "cohort-acceptance.db");
    private static readonly string[] StoreNames =
        ["cohort-acceptance", "cohort-attribution-retractions", "cohort-solo-inherited-receipts"];
    private static readonly string[] Tables =
    [
        "cohort_receipts", "cohort_members", "cohort_landing_intents", "cohort_partition_receipts",
        "cohort_fairness", "cohort_pair_suppressions", "cohort_attribution_effects", "cohort_outcome_consumptions",
        "cohort_materialization_failures", "cohort_invalidations", "cohort_attribution_retractions",
        "solo_inherited_receipts"
    ];
    private static readonly string[] SeededTables =
        ["cohort_receipts", "cohort_members", "cohort_landing_intents", "cohort_partition_receipts"];
    private static readonly AcceptanceCohortMemberBinding[] Members =
    [
        new(new GoalId("11111111"), new string('a', 40), new string('b', 40), ["docs/first.md"],
            ["first"], ChangeRiskTier.DocsOnly, ConductorTransitionDecision.Auto, "Clean", "NoConflictsDetected"),
        new(new GoalId("22222222"), new string('c', 40), new string('d', 40), ["docs/second.md"],
            ["second"], ChangeRiskTier.DocsOnly, ConductorTransitionDecision.Auto, "Clean", "NoConflictsDetected")
    ];
    private static readonly AcceptanceCohortIdentity Identity =
        AcceptanceCohortIdentity.Create(Members, new string('e', 40), new string('f', 40), "manifest-original");

    [Fact]
    public void Setup_FreshDirectory_CreatesTwelveTablesTwoIndexesAndThreeVersions()
    {
        Assert.False(Directory.Exists(root));

        CohortAcceptanceStoreSetup.Setup(Db);

        Assert.True(File.Exists(Db));
        using var connection = Open();
        AssertVersions(connection, 1);
        Assert.Equal(Tables.Order(StringComparer.Ordinal), Names(connection, "table")
            .Where(name => !name.StartsWith("sqlite_", StringComparison.Ordinal) && name != StoreSchemaVersions.TableName));
        Assert.Equal(new[] { "ix_cohort_invalidations_cohort", "ix_solo_inherited_receipts_main" },
            Names(connection, "index").Where(name => !name.StartsWith("sqlite_", StringComparison.Ordinal)));
    }

    [Fact]
    public void Setup_LiveShapedUnversionedFile_PreservesEveryValueAndReceiptIsReadable()
    {
        CreateLegacy();
        string[] before;
        string schema;
        using (var connection = Open(SqliteOpenMode.ReadWrite))
        {
            SeedRows(connection, Identity.Value);
            Assert.Equal(11, Names(connection, "table").Length);
            Assert.DoesNotContain("solo_inherited_receipts", Names(connection, "table"));
            Assert.DoesNotContain(StoreSchemaVersions.TableName, Names(connection, "table"));
            Assert.All(SeededTables, table => Assert.Equal(1L, Scalar(connection, $"SELECT count(*) FROM {table}")));
            before = SeededTables.Select(table => Rows(connection, table)).ToArray();
            schema = Schema(connection);
        }

        CohortAcceptanceStoreSetup.Setup(Db);

        using (var connection = Open(SqliteOpenMode.ReadWrite))
        {
            AssertVersions(connection, 1);
            Assert.Equal(before, SeededTables.Select(table => Rows(connection, table)).ToArray());
            Assert.Contains("solo_inherited_receipts", Names(connection, "table"));
            Assert.NotEqual(schema, Schema(connection));
            // Migration preserves the requested single member row. Receipt reads require a
            // canonical pair, so complete the pair only after all migration comparisons.
            InsertMember(connection, Identity.Value, 1);
        }

        var store = new CohortAcceptanceStore(Db);
        var receipt = Assert.IsType<AcceptanceCohortReceipt>(store.TryReadReceipt(Identity.Value));
        Assert.Equal("receipt-original", receipt.ReceiptId);
        Assert.Equal(Identity, receipt.Identity);
        Assert.Equal(AcceptanceCohortGateOutcome.Passed, receipt.Outcome);
        using var readBack = Open();
        Assert.Equal("prepared", Scalar(readBack, "SELECT state FROM cohort_landing_intents"));
    }

    [Fact]
    public void Constructor_CurrentSchema_StillInvalidatesLegacyIntentsEveryTime()
    {
        CreateLegacy();
        using (var connection = Open(SqliteOpenMode.ReadWrite))
            SeedRows(connection, "legacy-cohort");

        _ = new CohortAcceptanceStore(Db);
        using (var connection = Open(SqliteOpenMode.ReadWrite))
        {
            AssertVersions(connection, 1);
            Assert.Equal("invalidated", Scalar(connection, "SELECT state FROM cohort_landing_intents"));
            Execute(connection, "UPDATE cohort_landing_intents SET state='prepared', updated_at='original'");
        }

        _ = new CohortAcceptanceStore(Db);
        using var readBack = Open();
        Assert.Equal("invalidated", Scalar(readBack, "SELECT state FROM cohort_landing_intents"));
        Assert.NotEqual("original", Scalar(readBack, "SELECT updated_at FROM cohort_landing_intents"));
    }

    [Fact]
    public void ReadOnlyEvidenceReaders_AbsentCohortDatabase_CreateNoFile()
    {
        Directory.CreateDirectory(root);
        var item = new OwnerConductEvent(DateTimeOffset.UnixEpoch, "acceptance-cohort", null,
            "kind=cohort receipt=receipt-original identity=" + Identity.Value);
        Assert.False(File.Exists(Db));

        Assert.Null(new OwnerActivityEvidenceReader(Db).Read(item));
        Assert.False(File.Exists(Db));
        Assert.Null(new OwnerJointGateFailureEvidence(root).Read(item));
        Assert.False(File.Exists(Db));
        Assert.Empty(Directory.GetFiles(root));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Setup_AllNewer_PreservesSchemaRowsVersionsAndRunnerReports99(bool missingSchema)
    {
        if (missingSchema)
            Directory.CreateDirectory(root);
        else
            CreateLegacy();
        string before;
        using (var connection = Open(SqliteOpenMode.ReadWriteCreate))
        {
            if (!missingSchema) SeedRows(connection, Identity.Value);
            foreach (var name in StoreNames) RecordVersion(connection, name, 99);
            Execute(connection, "CREATE TABLE future_data(value BLOB); INSERT INTO future_data VALUES(X'00FF01');");
            before = Snapshot(connection);
        }

        CohortAcceptanceStoreSetup.Setup(Db);

        using (var connection = Open())
            Assert.Equal(before, Snapshot(connection));
        Assert.Contains(Db, Assert.Throws<InvalidOperationException>(() => StoreSetupRunner.Run(root)).Message);
        using var readBack = Open();
        Assert.Equal(before, Snapshot(readBack));
        AssertVersions(readBack, 99);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(99)]
    public void Setup_PartiallyRecorded_PreservesExistingVersionAndRecordsMissingNames(int version)
    {
        Directory.CreateDirectory(root);
        string before;
        using (var connection = Open(SqliteOpenMode.ReadWriteCreate))
        {
            RecordVersion(connection, StoreNames[0], version);
            before = Rows(connection, StoreSchemaVersions.TableName);
        }

        CohortAcceptanceStoreSetup.Setup(Db);

        using var readBack = Open();
        Assert.Equal(version, StoreSchemaVersions.Read(readBack, StoreNames[0]));
        Assert.Equal(1, StoreSchemaVersions.Read(readBack, StoreNames[1]));
        Assert.Equal(1, StoreSchemaVersions.Read(readBack, StoreNames[2]));
        Assert.Equal(before, Rows(readBack, StoreSchemaVersions.TableName, "WHERE store_name='cohort-acceptance'"));
        Assert.Contains("solo_inherited_receipts", Names(readBack, "table"));
    }

    [Fact]
    public void Setup_AllCurrent_ReturnsWithoutSchemaRepairOrMetadataWrites()
    {
        CohortAcceptanceStoreSetup.Setup(Db);
        string before;
        using (var connection = Open(SqliteOpenMode.ReadWrite))
        {
            Execute(connection, "DROP TABLE solo_inherited_receipts; UPDATE store_schema_versions SET applied_at='original';");
            before = Snapshot(connection);
        }

        CohortAcceptanceStoreSetup.Setup(Db);

        using var readBack = Open();
        Assert.Equal(before, Snapshot(readBack));
        Assert.DoesNotContain("solo_inherited_receipts", Names(readBack, "table"));
    }

    [Fact]
    public void Setup_LateSchemaFailure_RollsBackAllEarlierChangesAndRethrows()
    {
        Directory.CreateDirectory(root);
        string before;
        using (var connection = Open(SqliteOpenMode.ReadWriteCreate))
        {
            Execute(connection, "CREATE TABLE solo_inherited_receipts(unrelated TEXT); INSERT INTO solo_inherited_receipts VALUES('untouched');");
            before = Snapshot(connection);
        }

        var failure = Assert.Throws<SqliteException>(() => CohortAcceptanceStoreSetup.Setup(Db));

        Assert.Contains("observed_main_revision", failure.Message);
        using var readBack = Open();
        Assert.Equal(before, Snapshot(readBack));
        Assert.Null(StoreSchemaVersions.Read(readBack, StoreNames[0]));
        Assert.DoesNotContain("cohort_receipts", Names(readBack, "table"));
    }

    [Fact]
    public void Setup_OldUniquePartitionReceipt_RebuildsAndAllowsReusingReceipt()
    {
        var schema = LegacySchema.Replace(
            "member_ordinal INTEGER NOT NULL CHECK(member_ordinal IN (0,1)),\n        receipt_id TEXT NOT NULL,",
            "member_ordinal INTEGER NOT NULL CHECK(member_ordinal IN (0,1)),\n        receipt_id TEXT NOT NULL UNIQUE,", StringComparison.Ordinal)
            .Replace("ALTER TABLE cohort_partition_receipts ADD COLUMN failed_checks_json TEXT NULL;", "", StringComparison.Ordinal)
            .Replace("ALTER TABLE cohort_partition_receipts ADD COLUMN failing_test_identities_json TEXT NULL;", "", StringComparison.Ordinal);
        Directory.CreateDirectory(root);
        using (var connection = Open(SqliteOpenMode.ReadWriteCreate))
        {
            Execute(connection, schema);
            SeedRows(connection, Identity.Value, partitionColumns: false);
            Assert.Contains("receipt_id TEXT NOT NULL UNIQUE", Assert.IsType<string>(Scalar(connection,
                "SELECT sql FROM sqlite_schema WHERE name='cohort_partition_receipts'")));
            var duplicate = Assert.Throws<SqliteException>(() => DuplicatePartition(connection));
            Assert.Equal(19, duplicate.SqliteErrorCode);
        }

        CohortAcceptanceStoreSetup.Setup(Db);

        using var readBack = Open(SqliteOpenMode.ReadWrite);
        AssertVersions(readBack, 1);
        Assert.DoesNotContain("cohort_partition_receipts_legacy", Names(readBack, "table"));
        Assert.Equal("partition-original", Scalar(readBack, "SELECT receipt_id FROM cohort_partition_receipts"));
        DuplicatePartition(readBack);
        Assert.Equal(2L, Scalar(readBack, "SELECT count(*) FROM cohort_partition_receipts WHERE receipt_id='partition-original'"));
    }

    private void CreateLegacy()
    {
        Directory.CreateDirectory(root);
        using var connection = Open(SqliteOpenMode.ReadWriteCreate);
        Execute(connection, LegacySchema);
    }

    private static void SeedRows(SqliteConnection connection, string cohortId, bool partitionColumns = true)
    {
        Execute(connection, """
            INSERT INTO cohort_receipts VALUES(
                $id, 'receipt-original', $main, $tree, 'manifest-original', 'Passed', 'NotApplicable', 1,
                '2026-10-11T12:00:00+00:00', 123, '["original-check"]', NULL, 'original detail λ', 0,
                '["gate.trx"]', '[]', '[]', '[]', 'original-source', '["Original.Test"]', 'original-reason');
            INSERT INTO cohort_landing_intents VALUES($id, 'receipt-original', $tree, NULL, 'prepared', 'original');
            INSERT INTO cohort_partition_receipts(
                cohort_id, member_ordinal, receipt_id, goal_id, candidate_revision, main_revision,
                tree_revision, manifest_identity, outcome, elapsed_ms, test_result_paths_json)
            VALUES($id, 0, 'partition-original', '11111111', $candidate, $main, NULL,
                'manifest-original', 'Passed', 456, '["partition.trx"]');
            """, ("$id", cohortId), ("$main", Identity.ObservedMainRevision),
            ("$tree", Identity.CombinedTreeRevision), ("$candidate", Members[0].CandidateRevision));
        InsertMember(connection, cohortId, 0);
        if (partitionColumns)
            Execute(connection, """
                UPDATE cohort_partition_receipts SET failed_checks_json='["partition-check"]',
                    failing_test_identities_json=NULL;
                """);
    }

    private static void InsertMember(SqliteConnection connection, string cohortId, int ordinal)
    {
        var member = Members[ordinal];
        Execute(connection, """
            INSERT INTO cohort_members VALUES($id, $ordinal, $goal, $branch, $candidate,
                $paths, $resources, 'DocsOnly', 'Auto', 'Clean', 'NoConflictsDetected', 0);
            """, ("$id", cohortId), ("$ordinal", ordinal), ("$goal", member.GoalId.Value),
            ("$branch", member.BranchRevision), ("$candidate", member.CandidateRevision),
            ("$paths", JsonSerializer.Serialize(member.LandingPaths)), ("$resources", JsonSerializer.Serialize(member.ResourceKeys)));
    }

    private static void DuplicatePartition(SqliteConnection connection) => Execute(connection, """
        INSERT INTO cohort_partition_receipts(
            cohort_id, member_ordinal, receipt_id, goal_id, candidate_revision, main_revision,
            tree_revision, manifest_identity, outcome, elapsed_ms, test_result_paths_json)
        SELECT cohort_id, 1, receipt_id, '22222222', candidate_revision, main_revision,
            tree_revision, manifest_identity, outcome, elapsed_ms, test_result_paths_json FROM cohort_partition_receipts;
        """);

    private static void RecordVersion(SqliteConnection connection, string name, int version) => Execute(connection, """
        CREATE TABLE IF NOT EXISTS store_schema_versions(
            store_name TEXT NOT NULL PRIMARY KEY, version INTEGER NOT NULL, applied_at TEXT NOT NULL) WITHOUT ROWID;
        INSERT INTO store_schema_versions VALUES($name, $version, 'original');
        """, ("$name", name), ("$version", version));

    private static void AssertVersions(SqliteConnection connection, int expected)
    {
        Assert.Equal(StoreNames.Order(StringComparer.Ordinal), ReadStrings(connection,
            "SELECT store_name FROM store_schema_versions ORDER BY store_name"));
        Assert.All(StoreNames, name => Assert.Equal(expected, StoreSchemaVersions.Read(connection, name)));
    }

    private SqliteConnection Open(SqliteOpenMode mode = SqliteOpenMode.ReadOnly)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = Db, Mode = mode, Pooling = false }.ToString());
        connection.Open();
        Execute(connection, "PRAGMA foreign_keys=ON;");
        return connection;
    }

    private static void Execute(SqliteConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        command.ExecuteNonQuery();
    }

    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private static string[] ReadStrings(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var rows = new List<string>();
        while (reader.Read()) rows.Add(reader.GetString(0));
        return rows.ToArray();
    }

    private static string[] Names(SqliteConnection connection, string type) => ReadStrings(connection,
        $"SELECT name FROM sqlite_schema WHERE type='{type}' ORDER BY name");

    private static string Schema(SqliteConnection connection) => JsonSerializer.Serialize(ReadStrings(connection,
        "SELECT type || '|' || name || '|' || coalesce(sql, '') FROM sqlite_schema ORDER BY type, name"));

    private static string Rows(SqliteConnection connection, string table, string filter = "")
    {
        var columns = ReadStrings(connection, $"SELECT name FROM pragma_table_info('{table}') ORDER BY cid");
        var expressions = columns.Select(column => $"typeof([{column}]) || ':' || hex([{column}])");
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {string.Join(",", expressions)} FROM [{table}] {filter}";
        using var reader = command.ExecuteReader();
        var rows = new List<string>();
        while (reader.Read())
            rows.Add(JsonSerializer.Serialize(Enumerable.Range(0, reader.FieldCount).Select(reader.GetString).ToArray()));
        return JsonSerializer.Serialize(new { columns, rows = rows.Order(StringComparer.Ordinal).ToArray() });
    }

    private static string Snapshot(SqliteConnection connection) => JsonSerializer.Serialize(new
    {
        schema = Schema(connection),
        rows = Names(connection, "table").Select(table => new { table, values = Rows(connection, table) }).ToArray()
    });

    public void Dispose() => SharedTestSupport.RemoveTempDirectory(root);

    // Fixed pre-conversion schema fixture; no production setup is invoked to seed migration tests.
    private const string LegacySchema = """
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
            CREATE TABLE IF NOT EXISTS cohort_attribution_retractions(
                earlier_cohort_id TEXT NOT NULL REFERENCES cohort_receipts(cohort_id) ON DELETE CASCADE,
                goal_id TEXT NOT NULL,
                candidate_revision TEXT NOT NULL,
                later_cohort_id TEXT NOT NULL,
                failing_test_count INTEGER NOT NULL CHECK(failing_test_count>0),
                PRIMARY KEY(earlier_cohort_id, goal_id, candidate_revision));
            ALTER TABLE cohort_receipts ADD COLUMN attributed_members_json TEXT NOT NULL DEFAULT '[]';
            ALTER TABLE cohort_receipts ADD COLUMN unrelated_failures_json TEXT NOT NULL DEFAULT '[]';
            ALTER TABLE cohort_receipts ADD COLUMN attribution_source TEXT NULL;
            ALTER TABLE cohort_receipts ADD COLUMN cohort_failing_tests_json TEXT NULL;
            ALTER TABLE cohort_receipts ADD COLUMN attribution_reason_detail TEXT NULL;
            ALTER TABLE cohort_partition_receipts ADD COLUMN failed_checks_json TEXT NULL;
            ALTER TABLE cohort_partition_receipts ADD COLUMN failing_test_identities_json TEXT NULL;
        """;
}
