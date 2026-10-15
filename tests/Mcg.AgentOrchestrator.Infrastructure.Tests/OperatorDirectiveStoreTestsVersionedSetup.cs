using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

// Parallel-safe: private temporary databases, no pooling or shared process state.
public sealed class OperatorDirectiveStoreTestsVersionedSetup
{
    [Fact]
    public void Setup_CreatesVersionAndTables_AndPreservesAppliedAt()
    {
        using var fixture = new ConductorVerbStartTests.Fixture();
        var path = fixture.Workspace.OperatorDirectivesStorePath;
        Assert.False(File.Exists(path));
        OperatorDirectiveStoreSetup.Setup(path);
        using (var connection = Open(path))
        {
            Assert.Equal(1, StoreSchemaVersions.Read(connection, "operator-directives"));
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT name FROM sqlite_schema WHERE type='table' ORDER BY name";
            using (var reader = command.ExecuteReader())
            {
                var names = new List<string>();
                while (reader.Read()) names.Add(reader.GetString(0));
                Assert.Equal(["directive_handoffs", "directive_retirements", "directives", "store_schema_versions"], names);
            }
            command.CommandText = "UPDATE store_schema_versions SET applied_at='original'";
            Assert.Equal(1, command.ExecuteNonQuery());
        }
        var before = File.ReadAllBytes(path);
        OperatorDirectiveStoreSetup.Setup(path);
        Assert.Equal(before, File.ReadAllBytes(path));
        using var readBack = Open(path);
        using var query = readBack.CreateCommand();
        query.CommandText = "SELECT applied_at FROM store_schema_versions WHERE store_name='operator-directives'";
        Assert.Equal("original", query.ExecuteScalar());
    }

    [Fact]
    public void NewerSchema_SetupPreservesFile_AndReadsAndWritesRefuseIt()
    {
        using var fixture = new ConductorVerbStartTests.Fixture();
        var path = fixture.Workspace.OperatorDirectivesStorePath;
        OperatorDirectiveStoreSetup.Setup(path);
        using (var connection = Open(path))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE store_schema_versions SET version=99";
            Assert.Equal(1, command.ExecuteNonQuery());
        }
        var before = File.ReadAllBytes(path);
        OperatorDirectiveStoreSetup.Setup(path);
        Assert.Contains("schema is Newer (expected version 1); run setup.",
            Assert.Throws<InvalidOperationException>(() => SqliteOperatorDirectiveStore.OpenReadOnly(path)).Message);
        Assert.Throws<InvalidOperationException>(() => new SqliteOperatorDirectiveStore(path)
            .TryAppendHandoff("handoff", "source", null, "owner", Mcg.AgentOrchestrator.Core.OperatorActorKind.Human,
                "cli", DateTimeOffset.UnixEpoch));
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void Runner_RegistersDirectiveVersionAndWorkspacePath()
    {
        using var fixture = new ConductorVerbStartTests.Fixture();
        var result = Assert.Single(StoreSetupRunner.Run(fixture.Workspace.OrchestratorDirectory),
            row => row.StoreName == "operator-directives");
        Assert.Equal(1, result.Version);
        Assert.Equal(fixture.Workspace.OperatorDirectivesStorePath, result.DatabasePath);
        Assert.Empty(SqliteOperatorDirectiveStore.OpenReadOnly(result.DatabasePath).List(DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public void MissingStore_ReadPathsNeverCreateFileOrDirectory()
    {
        using var fixture = new ConductorVerbStartTests.Fixture();
        var path = Path.Combine(fixture.Workspace.RootDirectory, "absent", "operator-directives.db");
        var store = new SqliteOperatorDirectiveStore(path);
        Assert.Empty(store.List(DateTimeOffset.UnixEpoch));
        Assert.False(store.HasRecordSource("record"));
        Assert.False(store.HasRetirementSource("retire"));
        Assert.False(store.HasHandoffSource("handoff"));
        Assert.Contains("run setup", Assert.Throws<InvalidOperationException>(() =>
            SqliteOperatorDirectiveStore.OpenReadOnly(path)).Message);
        Assert.False(Directory.Exists(Path.GetDirectoryName(path)));
    }

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false
        }.ToString());
        connection.Open();
        return connection;
    }
}
