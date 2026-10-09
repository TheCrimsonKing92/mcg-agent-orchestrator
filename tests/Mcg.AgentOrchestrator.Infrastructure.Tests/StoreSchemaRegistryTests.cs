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
        Assert.Equal(sources, entries.Select(entry => entry.SourceFile).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(entries.Count, entries.Select(entry => entry.StoreName).Distinct(StringComparer.Ordinal).Count());
        Assert.All(entries, entry =>
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.StoreName));
            Assert.False(string.IsNullOrWhiteSpace(entry.Database));
            Assert.False(string.IsNullOrWhiteSpace(entry.Family));
        });
        Assert.Equal(new[] { StoreSchemaRegistry.Portfolio, StoreSchemaRegistry.Backlog, StoreSchemaRegistry.OperatorIntents, StoreSchemaRegistry.OperatorLessons,
            StoreSchemaRegistry.OperatorEscapes, StoreSchemaRegistry.DogfoodLog }, entries.Where(entry => entry.CurrentVersion.HasValue).ToArray());
        Assert.Equal(1, StoreSchemaRegistry.DogfoodLog.CurrentVersion);
        Assert.Equal("dogfood-log.db", StoreSchemaRegistry.DogfoodLog.Database);
        Assert.Equal(1, StoreSchemaRegistry.OperatorIntents.CurrentVersion);
        Assert.Equal(SqliteOperatorIntentStore.DatabaseFileName, StoreSchemaRegistry.OperatorIntents.Database);
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
    [InlineData(1, StoreSchemaState.Current)]
    [InlineData(2, StoreSchemaState.Newer)]
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
