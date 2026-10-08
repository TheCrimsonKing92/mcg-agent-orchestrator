using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

// Parallel-safe: each test owns a unique directory and all connections disable pooling.
public sealed class StoreSetupRunnerTests
{
    [Fact]
    public void Registrations_VersionedInventory_CoversExactlyEligibleStores()
    {
        var versioned = StoreSchemaRegistry.Inventory.Where(entry => entry.CurrentVersion.HasValue)
            .Select(entry => entry.StoreName).Order(StringComparer.Ordinal).ToArray();
        Assert.NotEmpty(versioned);
        Assert.Equal(versioned, StoreSetupRunner.RegisteredStoreNames.Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task Run_EmptyDirectory_CreatesOnlyVersionedStoresAndAllowsReadOnly()
    {
        using var fixture = new ConductorVerbStartTests.Fixture();
        var workspace = fixture.Workspace;
        Assert.False(File.Exists(workspace.PortfolioStorePath));
        Assert.Contains("run setup", Assert.Throws<InvalidOperationException>(
            () => PortfolioStore.OpenReadOnly(workspace.PortfolioStorePath)).Message);

        var results = StoreSetupRunner.Run(workspace.OrchestratorDirectory);

        Assert.Equal(StoreSchemaRegistry.Inventory.Where(entry => entry.CurrentVersion.HasValue)
            .Select(entry => entry.StoreName), results.Select(result => result.StoreName));
        var portfolio = Assert.Single(results, result => result.StoreName == "portfolio");
        Assert.Equal(StoreSchemaRegistry.Portfolio.StoreName, portfolio.StoreName);
        Assert.Equal(workspace.PortfolioStorePath, portfolio.DatabasePath);
        Assert.Equal(1, portfolio.Version);
        using var connection = Open(workspace.PortfolioStorePath, SqliteOpenMode.ReadOnly);
        Assert.Equal(1, StoreSchemaVersions.Read(connection, portfolio.StoreName));
        Assert.All(Directory.GetFiles(workspace.OrchestratorDirectory), path =>
            Assert.Contains(Path.GetFileName(path), new[] { "portfolio.db", "portfolio.db-wal", "portfolio.db-shm",
                "operator-lessons.db", "operator-escapes.db" }));
        Assert.Empty(await PortfolioStore.OpenReadOnly(workspace.PortfolioStorePath).ListProjectsAsync());
        Assert.Equal(workspace.OperatorLessonsStorePath,
            Assert.Single(results, result => result.StoreName == "operator-lessons").DatabasePath);
        Assert.Equal(workspace.OperatorEscapesStorePath,
            Assert.Single(results, result => result.StoreName == "operator-escapes").DatabasePath);
        foreach (var result in results)
        {
            using var store = Open(result.DatabasePath, SqliteOpenMode.ReadOnly);
            Assert.Equal(1, result.Version);
            Assert.Equal(result.Version, StoreSchemaVersions.Read(store, result.StoreName));
        }
        Assert.Empty(SqliteOperatorLessonStore.OpenReadOnly(workspace.OperatorLessonsStorePath).List());
        Assert.Empty(SqliteOperatorEscapeStore.OpenReadOnly(workspace.OperatorEscapesStorePath).List());
    }

    [Fact]
    public void Run_SecondRun_PreservesRecordedVersionAndAppliedAt()
    {
        using var fixture = new ConductorVerbStartTests.Fixture();
        var workspace = fixture.Workspace;
        var first = StoreSetupRunner.Run(workspace.OrchestratorDirectory);
        using (var connection = Open(workspace.PortfolioStorePath, SqliteOpenMode.ReadWrite))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE store_schema_versions SET applied_at = 'original'";
            Assert.Equal(1, command.ExecuteNonQuery());
        }

        Assert.Equal(first, StoreSetupRunner.Run(workspace.OrchestratorDirectory));
        using var readBack = Open(workspace.PortfolioStorePath, SqliteOpenMode.ReadOnly);
        Assert.Equal(1, StoreSchemaVersions.Read(readBack, StoreSchemaRegistry.Portfolio.StoreName));
        using var query = readBack.CreateCommand();
        query.CommandText = "SELECT applied_at FROM store_schema_versions WHERE store_name = 'portfolio'";
        Assert.Equal("original", query.ExecuteScalar());
    }

    [Theory]
    [InlineData(2)]
    [InlineData(99)]
    public void Run_NewerVersion_ReportsRecordedVersionWithoutDowngrade(int version)
    {
        using var fixture = new ConductorVerbStartTests.Fixture();
        var workspace = fixture.Workspace;
        StoreSetupRunner.Run(workspace.OrchestratorDirectory);
        using (var connection = Open(workspace.PortfolioStorePath, SqliteOpenMode.ReadWrite))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE store_schema_versions SET version = $version";
            command.Parameters.AddWithValue("$version", version);
            Assert.Equal(1, command.ExecuteNonQuery());
        }

        Assert.Equal(version, Assert.Single(StoreSetupRunner.Run(workspace.OrchestratorDirectory),
            result => result.StoreName == "portfolio").Version);
        using var readBack = Open(workspace.PortfolioStorePath, SqliteOpenMode.ReadOnly);
        Assert.Equal(version, StoreSchemaVersions.Read(readBack, StoreSchemaRegistry.Portfolio.StoreName));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Run_UnversionedFile_RequiresSetupThenAllowsReadOnly(bool versionTable)
    {
        using var fixture = new ConductorVerbStartTests.Fixture();
        var workspace = fixture.Workspace;
        Directory.CreateDirectory(workspace.OrchestratorDirectory);
        using (var connection = Open(workspace.PortfolioStorePath, SqliteOpenMode.ReadWriteCreate))
        {
            if (versionTable)
            {
                StoreSchemaVersions.UpgradeToCurrent(connection, StoreSchemaRegistry.Portfolio);
                using var command = connection.CreateCommand();
                command.CommandText = "DELETE FROM store_schema_versions";
                Assert.Equal(1, command.ExecuteNonQuery());
            }
        }
        Assert.True(File.Exists(workspace.PortfolioStorePath));
        Assert.Contains("run setup", Assert.Throws<InvalidOperationException>(
            () => PortfolioStore.OpenReadOnly(workspace.PortfolioStorePath)).Message);

        Assert.Equal(1, Assert.Single(StoreSetupRunner.Run(workspace.OrchestratorDirectory),
            result => result.StoreName == "portfolio").Version);
        Assert.Empty(await PortfolioStore.OpenReadOnly(workspace.PortfolioStorePath).ListProjectsAsync());
    }

    private static SqliteConnection Open(string path, SqliteOpenMode mode)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = mode, Pooling = false
        }.ToString());
        connection.Open();
        return connection;
    }
}
