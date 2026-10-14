using System.Reflection;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

// Parallel-safe: version probes own private in-memory connections; inventory reads the explicit source root.
public sealed class StoreSchemaRegistryTests
{
    [Fact]
    public void Inventory_SchemaCreatingSources_EachHasOneEntryWithOwnership()
    {
        var root = VerifiedRepositoryRoot.Find();
        var sources = EnumerateSources(Path.Combine(root, "src"))
            .Where(path => File.ReadAllText(path).Contains("CREATE TABLE IF NOT EXISTS", StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
            .Order(StringComparer.Ordinal).ToArray();
        Assert.NotEmpty(sources);
        var entries = StoreSchemaRegistry.Inventory;
        var fragments = typeof(StoreSchemaRegistry).Assembly.GetTypes()
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance))
            .SelectMany(method => method.GetCustomAttributes<StoreSchemaSourceAttribute>()).ToArray();
        Assert.All(fragments, fragment =>
        {
            Assert.Single(entries.Where(entry => entry.StoreName == fragment.StoreName));
            Assert.Contains(fragment.SourceFile, sources);
            Assert.Single(fragments.Where(other => other.SourceFile == fragment.SourceFile));
        });
        // These entries register their owning stores; CREATE statements live in extracted setup types.
        Assert.Equal(sources, entries.Select(entry => entry == StoreSchemaRegistry.AuthorClaims
            ? "src/Mcg.AgentOrchestrator.Execution/Persistence/AuthorClaimStoreSetup.cs"
            : entry == StoreSchemaRegistry.JudgePanel
                ? "src/Mcg.AgentOrchestrator.Execution/Persistence/JudgePanelStoreSetup.cs"
            : entry == StoreSchemaRegistry.BoardFill
                ? "src/Mcg.AgentOrchestrator.Execution/Persistence/BoardFillStoreSetup.cs"
            : entry == StoreSchemaRegistry.RunEvents
                ? "src/Mcg.AgentOrchestrator.Execution/Persistence/RunEventStoreSetup.cs"
            : entry == StoreSchemaRegistry.CohortAcceptance || entry == StoreSchemaRegistry.CohortAttributionRetractions ||
                entry == StoreSchemaRegistry.CohortSoloInheritedReceipts
                ? "src/Mcg.AgentOrchestrator.Execution/Persistence/CohortAcceptanceStoreSetup.cs"
            : entry.SourceFile).Concat(fragments.Select(fragment => fragment.SourceFile))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(entries.Count, entries.Select(entry => entry.StoreName).Distinct(StringComparer.Ordinal).Count());
        Assert.All(entries, entry =>
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.StoreName));
            Assert.False(string.IsNullOrWhiteSpace(entry.Database));
            Assert.False(string.IsNullOrWhiteSpace(entry.Family));
        });
        Assert.Equal(new[] { StoreSchemaRegistry.Portfolio, StoreSchemaRegistry.Experiments, StoreSchemaRegistry.Backlog, StoreSchemaRegistry.OperatorIntents, StoreSchemaRegistry.OperatorLessons,
            StoreSchemaRegistry.OperatorEscapes, StoreSchemaRegistry.CohortAcceptance, StoreSchemaRegistry.CohortAttributionRetractions, StoreSchemaRegistry.CohortSoloInheritedReceipts, StoreSchemaRegistry.MergeTrainAcceptance, StoreSchemaRegistry.FollowerGateAcceptance, StoreSchemaRegistry.CollaborationItems, StoreSchemaRegistry.RunEvents, StoreSchemaRegistry.DogfoodLog, StoreSchemaRegistry.AuthorClaims, StoreSchemaRegistry.StewardTriggers, StoreSchemaRegistry.JudgePanel, StoreSchemaRegistry.BoardFill, StoreSchemaRegistry.ProgressiveReviewGlanceCircuit, StoreSchemaRegistry.ProgressiveReviewSteering }, entries.Where(entry => entry.CurrentVersion.HasValue).ToArray());
        Assert.Equal(1, StoreSchemaRegistry.RunEvents.CurrentVersion);
        Assert.Equal("run-events", StoreSchemaRegistry.RunEvents.StoreName);
        Assert.Equal("run-events.db", StoreSchemaRegistry.RunEvents.Database);
        Assert.Equal("collaboration", StoreSchemaRegistry.RunEvents.Family);
        Assert.Equal("src/Mcg.AgentOrchestrator.Execution/Persistence/RunEventStore.cs",
            StoreSchemaRegistry.RunEvents.SourceFile);
        Assert.Contains(StoreSchemaRegistry.RunEvents, entries);
        Assert.Equal(1, StoreSchemaRegistry.AuthorClaims.CurrentVersion);
        Assert.Equal("author-claims.db", StoreSchemaRegistry.AuthorClaims.Database);
        Assert.Equal("src/Mcg.AgentOrchestrator.App/Orchestration/ConductorAuthorClaimStore.cs",
            StoreSchemaRegistry.AuthorClaims.SourceFile);
        Assert.Equal(1, StoreSchemaRegistry.StewardTriggers.CurrentVersion);
        Assert.Equal("steward-triggers.db", StoreSchemaRegistry.StewardTriggers.Database);
        Assert.Equal(1, StoreSchemaRegistry.JudgePanel.CurrentVersion);
        Assert.Equal("judge-panel.db", StoreSchemaRegistry.JudgePanel.Database);
        Assert.Equal("src/Mcg.AgentOrchestrator.App/Orchestration/ConductorJudgePanelCaseStore.Schema.cs",
            StoreSchemaRegistry.JudgePanel.SourceFile);
        Assert.Contains(StoreSchemaRegistry.JudgePanel, entries);
        Assert.Equal(1, StoreSchemaRegistry.BoardFill.CurrentVersion);
        Assert.Equal("board-fill.db", StoreSchemaRegistry.BoardFill.Database);
        Assert.Equal("src/Mcg.AgentOrchestrator.App/Orchestration/ConductorBoardFillDraftStore.cs",
            StoreSchemaRegistry.BoardFill.SourceFile);
        Assert.Equal(1, StoreSchemaRegistry.FollowerGateAcceptance.CurrentVersion);
        Assert.Equal(1, StoreSchemaRegistry.Experiments.CurrentVersion);
        Assert.Equal("experiments.db", StoreSchemaRegistry.Experiments.Database);
        Assert.Equal("follower-gate-acceptance.db", StoreSchemaRegistry.FollowerGateAcceptance.Database);
        Assert.Equal(1, StoreSchemaRegistry.ProgressiveReviewSteering.CurrentVersion);
        Assert.Equal("progressive-review-steering.db", StoreSchemaRegistry.ProgressiveReviewSteering.Database);
        Assert.Equal(1, StoreSchemaRegistry.ProgressiveReviewGlanceCircuit.CurrentVersion);
        Assert.Equal("progressive-review-glance-circuit.db", StoreSchemaRegistry.ProgressiveReviewGlanceCircuit.Database);
        Assert.Equal(1, StoreSchemaRegistry.CollaborationItems.CurrentVersion);
        Assert.Equal("collaboration-items.db", StoreSchemaRegistry.CollaborationItems.Database);
        Assert.Equal(1, StoreSchemaRegistry.MergeTrainAcceptance.CurrentVersion);
        Assert.Equal("merge-train-acceptance.db", StoreSchemaRegistry.MergeTrainAcceptance.Database);
        Assert.Equal(1, StoreSchemaRegistry.DogfoodLog.CurrentVersion);
        Assert.Equal("dogfood-log.db", StoreSchemaRegistry.DogfoodLog.Database);
        Assert.Equal(2, StoreSchemaRegistry.OperatorIntents.CurrentVersion);
        Assert.Equal(SqliteOperatorIntentStore.DatabaseFileName, StoreSchemaRegistry.OperatorIntents.Database);
    }

    [Fact]
    public void OperatorIntents_SetupOwnsAdmissionTablesUnderExistingSchemaVersion()
    {
        var root = Path.Combine(Path.GetTempPath(), "mcg-schema-owner-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, SqliteOperatorIntentStore.DatabaseFileName);
        try
        {
            SqliteOperatorIntentStore.Setup(path);
            using var conn = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            conn.Open();
            Assert.Equal(StoreSchemaState.Current, StoreSchemaVersions.Verify(conn, StoreSchemaRegistry.OperatorIntents));
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT name FROM sqlite_schema WHERE type = 'table' AND name LIKE 'task_intent_%' ORDER BY name";
            var tables = new List<string>();
            using (var reader = cmd.ExecuteReader())
                while (reader.Read()) tables.Add(reader.GetString(0));
            Assert.Equal(new[] { "task_intent_admission", "task_intent_launches", "task_intent_preparations", "task_intent_suppressions" }, tables);
            cmd.CommandText = "SELECT store_name FROM store_schema_versions";
            using var versions = cmd.ExecuteReader();
            Assert.True(versions.Read());
            Assert.Equal(StoreSchemaRegistry.OperatorIntents.StoreName, versions.GetString(0));
            Assert.False(versions.Read());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Verify_NoVersionTable_ReportsMissingWithoutCreatingSchema()
    {
        using var conn = OpenMemory();
        Assert.Null(StoreSchemaVersions.Read(conn, "portfolio"));
        Assert.Equal(StoreSchemaState.Missing, StoreSchemaVersions.Verify(conn, StoreSchemaRegistry.Portfolio));
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM sqlite_schema";
        Assert.Equal(0L, cmd.ExecuteScalar());
    }

    [Theory]
    [InlineData(null, StoreSchemaState.Missing)]
    [InlineData(0, StoreSchemaState.Older)]
    [InlineData(1, StoreSchemaState.Older)]
    [InlineData(2, StoreSchemaState.Older)]
    [InlineData(3, StoreSchemaState.Current)]
    [InlineData(4, StoreSchemaState.Newer)]
    public void Verify_RecordedVersion_ClassifiesWithoutChangingRecord(int? version, StoreSchemaState expected)
    {
        using var conn = OpenMemory();
        StoreSchemaVersions.UpgradeToCurrent(conn, StoreSchemaRegistry.Portfolio);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = version.HasValue
            ? "UPDATE store_schema_versions SET version = $version, applied_at = 'original' WHERE store_name = 'portfolio'"
            : "DELETE FROM store_schema_versions WHERE store_name = 'portfolio'";
        if (version.HasValue)
            cmd.Parameters.AddWithValue("$version", version.Value);
        cmd.ExecuteNonQuery();

        Assert.Equal(expected, StoreSchemaVersions.Verify(conn, StoreSchemaRegistry.Portfolio));
        Assert.Equal(version, StoreSchemaVersions.Read(conn, "portfolio"));
        if (version.HasValue)
        {
            cmd.CommandText = "SELECT applied_at FROM store_schema_versions WHERE store_name = 'portfolio'";
            Assert.Equal("original", cmd.ExecuteScalar());
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void UpgradeToCurrent_ExistingRecord_UpgradesOnlyOlderAndPreservesOtherStores(int version)
    {
        using var conn = OpenMemory();
        StoreSchemaVersions.UpgradeToCurrent(conn, StoreSchemaRegistry.Portfolio);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE store_schema_versions SET version = $version, applied_at = 'original' WHERE store_name = 'portfolio';
            INSERT INTO store_schema_versions VALUES ('other', 99, 'untouched');
            """;
        cmd.Parameters.AddWithValue("$version", version);
        cmd.ExecuteNonQuery();

        StoreSchemaVersions.UpgradeToCurrent(conn, StoreSchemaRegistry.Portfolio);
        Assert.Equal(Math.Max(version, StoreSchemaRegistry.Portfolio.CurrentVersion!.Value), StoreSchemaVersions.Read(conn, "portfolio"));
        Assert.Equal(99, StoreSchemaVersions.Read(conn, "other"));
        cmd.CommandText = "SELECT applied_at FROM store_schema_versions WHERE store_name = 'other'";
        Assert.Equal("untouched", cmd.ExecuteScalar());
        cmd.CommandText = "SELECT applied_at FROM store_schema_versions WHERE store_name = 'portfolio'";
        var appliedAt = Assert.IsType<string>(cmd.ExecuteScalar());
        if (version < StoreSchemaRegistry.Portfolio.CurrentVersion)
            Assert.True(DateTimeOffset.TryParse(appliedAt, out _));
        else
            Assert.Equal("original", appliedAt);
    }

    [Fact]
    public void UpgradeToCurrent_CallerRollsBack_RollsBackVersionTableAndRecord()
    {
        using var conn = OpenMemory();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "BEGIN IMMEDIATE";
        cmd.ExecuteNonQuery();
        StoreSchemaVersions.UpgradeToCurrent(conn, StoreSchemaRegistry.Portfolio);
        Assert.Equal(StoreSchemaState.Current, StoreSchemaVersions.Verify(conn, StoreSchemaRegistry.Portfolio));
        cmd.CommandText = "ROLLBACK";
        cmd.ExecuteNonQuery();
        Assert.Equal(StoreSchemaState.Missing, StoreSchemaVersions.Verify(conn, StoreSchemaRegistry.Portfolio));
    }

    private static SqliteConnection OpenMemory()
    {
        var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();
        return conn;
    }

    private static IEnumerable<string> EnumerateSources(string directory)
    {
        foreach (var path in Directory.EnumerateFiles(directory))
            yield return path;
        foreach (var child in Directory.EnumerateDirectories(directory))
        {
            if (new[] { "bin", "obj", ".scratch", ".orchestrator-prototype", "TestResults", "playwright-report" }
                .Contains(Path.GetFileName(child), StringComparer.OrdinalIgnoreCase))
                continue;
            foreach (var path in EnumerateSources(child))
                yield return path;
        }
    }
}
