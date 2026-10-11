using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

// Parallel-safe: unique fixture workspaces, pooling disabled, and private error writers.
public sealed class ConductorLoopStoreSetupTests
{
    [Fact]
    public async Task Run_EmptyDirectory_CreatesReadableVersionedExperiments()
    {
        using var fixture = new ConductorVerbStartTests.Fixture();
        var workspace = fixture.Workspace;
        Assert.False(File.Exists(workspace.ExperimentStorePath));

        var results = ConductorLoopStoreSetup.Run(workspace.OrchestratorDirectory);

        Assert.True(File.Exists(workspace.ExperimentStorePath));
        Assert.Equal(1, Assert.Single(results, result => result.StoreName == "experiments").Version);
        using var connection = Open(workspace.ExperimentStorePath, SqliteOpenMode.ReadOnly);
        Assert.Equal(1, StoreSchemaVersions.Read(connection, "experiments"));
        Assert.Equal(1L, Scalar(connection,
            "SELECT COUNT(*) FROM sqlite_schema WHERE type = 'table' AND name = 'experiments'"));
        Assert.Empty(await ExperimentStore.OpenReadOnly(workspace.ExperimentStorePath).ListOpenAsync());
    }

    [Fact]
    public async Task Run_UnversionedExperiments_PreservesBothIdsAndSpecs()
    {
        using var fixture = new ConductorVerbStartTests.Fixture();
        var workspace = fixture.Workspace;
        Directory.CreateDirectory(workspace.OrchestratorDirectory);
        var first = Spec("Keep the first hypothesis", "epic-one");
        var second = Spec("Keep the second hypothesis", "epic-two");
        using (var connection = Open(workspace.ExperimentStorePath, SqliteOpenMode.ReadWriteCreate))
        {
            Execute(connection, """
                CREATE TABLE experiments (
                    id TEXT PRIMARY KEY, hypothesis TEXT NOT NULL, epic_id TEXT, created_at TEXT NOT NULL,
                    intervention_json TEXT NOT NULL, baseline_json TEXT NOT NULL, metrics_json TEXT NOT NULL,
                    guardrail_json TEXT NOT NULL, stop_rule_json TEXT NOT NULL, decision_rule_json TEXT NOT NULL,
                    outcome TEXT NOT NULL CHECK(outcome IN ('open','confirmed','refuted','inconclusive')), decision_json TEXT);
                """);
            InsertExperiment(connection, "first", first);
            InsertExperiment(connection, "second", second);
            Assert.Equal(0L, Scalar(connection,
                "SELECT COUNT(*) FROM sqlite_schema WHERE name = 'store_schema_versions'"));
            Assert.Equal(2L, Scalar(connection, "SELECT COUNT(*) FROM experiments"));
        }

        ConductorLoopStoreSetup.Run(workspace.OrchestratorDirectory);

        using var readBack = Open(workspace.ExperimentStorePath, SqliteOpenMode.ReadOnly);
        Assert.Equal(1, StoreSchemaVersions.Read(readBack, "experiments"));
        Assert.Equal(2L, Scalar(readBack, "SELECT COUNT(*) FROM experiments"));
        var rows = await ExperimentStore.OpenReadOnly(workspace.ExperimentStorePath).ListOpenAsync();
        Assert.Equal(2, rows.Count);
        Assert.Equal(Json(first), Json(Assert.Single(rows, row => row.Id == "first").Spec));
        Assert.Equal(Json(second), Json(Assert.Single(rows, row => row.Id == "second").Spec));
    }

    [Fact]
    public void Run_FutureVersion_RejectsWithoutChangingVersionsOrRows()
    {
        using var fixture = new ConductorVerbStartTests.Fixture();
        var workspace = fixture.Workspace;
        StoreSetupRunner.Run(workspace.OrchestratorDirectory);
        using (var connection = Open(workspace.ExperimentStorePath, SqliteOpenMode.ReadWrite))
        {
            InsertExperiment(connection, "retained", Spec("Future store data", "epic-future"));
            Assert.Equal(1, Execute(connection,
                "UPDATE store_schema_versions SET version = 99 WHERE store_name = 'experiments'"));
        }
        var versionsBefore = Snapshot(workspace.ExperimentStorePath,
            "SELECT * FROM store_schema_versions ORDER BY store_name");
        var rowsBefore = Snapshot(workspace.ExperimentStorePath, "SELECT * FROM experiments ORDER BY id");
        Assert.Single(versionsBefore);
        Assert.Single(rowsBefore);

        var exception = Assert.Throws<InvalidOperationException>(
            () => ConductorLoopStoreSetup.Run(workspace.OrchestratorDirectory));

        Assert.Contains("experiments", exception.Message);
        Assert.Contains(workspace.ExperimentStorePath, exception.Message);
        Assert.Contains("schema version 99", exception.Message);
        Assert.Contains("expected version 1", exception.Message);
        Assert.Equal(versionsBefore, Snapshot(workspace.ExperimentStorePath,
            "SELECT * FROM store_schema_versions ORDER BY store_name"));
        Assert.Equal(rowsBefore, Snapshot(workspace.ExperimentStorePath,
            "SELECT * FROM experiments ORDER BY id"));
    }

    [Fact]
    public void Run_RepeatedCall_PreservesEveryStoresAppliedAt()
    {
        using var fixture = new ConductorVerbStartTests.Fixture();
        var directory = fixture.Workspace.OrchestratorDirectory;
        var first = ConductorLoopStoreSetup.Run(directory);
        Assert.NotEmpty(first);
        foreach (var result in first)
        {
            using var connection = Open(result.DatabasePath, SqliteOpenMode.ReadWrite);
            Assert.Equal(1, Execute(connection, "UPDATE store_schema_versions SET applied_at = 'original'"));
        }

        Assert.Equal(first, ConductorLoopStoreSetup.Run(directory));

        foreach (var result in first)
        {
            using var connection = Open(result.DatabasePath, SqliteOpenMode.ReadOnly);
            Assert.Equal(result.Version, StoreSchemaVersions.Read(connection, result.StoreName));
            Assert.Equal("original", Scalar(connection, "SELECT applied_at FROM store_schema_versions"));
        }
    }

    [Fact]
    public void Run_InjectedSetup_ReceivesExactDirectoryOncePerCall()
    {
        using var fixture = new ConductorVerbStartTests.Fixture();
        var directory = fixture.Workspace.OrchestratorDirectory;
        var received = new List<string>();
        IReadOnlyList<StoreSetupResult> Setup(string path)
        {
            received.Add(path);
            return StoreSetupRunner.Run(path);
        }

        ConductorLoopStoreSetup.Run(directory, Setup);
        Assert.Equal(new[] { directory }, received);
        ConductorLoopStoreSetup.Run(directory, Setup);
        Assert.Equal(new[] { directory, directory }, received);
    }

    [Fact]
    public void Run_OlderUnknownOrUnversionedResult_LeavesUpgradeToRunner()
    {
        var unversioned = StoreSchemaRegistry.Inventory.First(entry => entry.CurrentVersion is null);
        IReadOnlyList<StoreSetupResult> results =
        [
            new("experiments", "experiments.db", 0),
            new("unknown", "unknown.db", 99),
            new(unversioned.StoreName, unversioned.Database, 99)
        ];

        Assert.Same(results, ConductorLoopStoreSetup.Run("unused", _ => results));
    }

    [Fact]
    public void PrepareConductLoopPolicy_SetupFailure_ReportsErrorAndPreventsStart()
    {
        using var fixture = new ConductorVerbStartTests.Fixture();
        using var error = new StringWriter();
        var calls = 0;

        var resolution = CliConductLoopStartup.PrepareConductLoopPolicy(
            fixture.Workspace.OrchestratorDirectory, "NoSuchPolicy", error, _ =>
            {
                calls++;
                throw new InvalidOperationException("future store");
            });

        // Null is the branch's stop signal; an unknown policy would throw if reached.
        Assert.Null(resolution);
        Assert.Equal(1, calls);
        Assert.Equal($"Error: future store{Environment.NewLine}", error.ToString());
    }

    [Theory]
    [InlineData("io")]
    [InlineData("sqlite")]
    [InlineData("access")]
    public void PrepareConductLoopPolicy_TransientFailure_WarnsAndAllowsStart(string kind)
    {
        using var fixture = new ConductorVerbStartTests.Fixture();
        using var error = new StringWriter();
        var calls = 0;
        Exception failure = kind switch
        {
            "sqlite" => new SqliteException("locked", 5),
            "access" => new UnauthorizedAccessException("locked"),
            _ => new IOException("locked")
        };

        var resolution = CliConductLoopStartup.PrepareConductLoopPolicy(
            fixture.Workspace.OrchestratorDirectory, "Manual", error, _ =>
            {
                calls++;
                throw failure;
            });

        Assert.NotNull(resolution);
        Assert.Equal("Manual", resolution.Policy.Name);
        Assert.Equal(1, calls);
        Assert.Equal($"Warning: store setup deferred: locked{Environment.NewLine}", error.ToString());
    }

    [Fact]
    public void PrepareConductLoopPolicy_Success_RunsSetupBeforeResolvingPolicy()
    {
        using var fixture = new ConductorVerbStartTests.Fixture();
        using var error = new StringWriter();
        var received = new List<string>();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            CliConductLoopStartup.PrepareConductLoopPolicy(
                fixture.Workspace.OrchestratorDirectory, "NoSuchPolicy", error, path =>
                {
                    received.Add(path);
                    return [];
                }));

        Assert.Equal(new[] { fixture.Workspace.OrchestratorDirectory }, received);
        Assert.StartsWith("Unknown conductor policy 'NoSuchPolicy'.", exception.Message);
        Assert.Equal("", error.ToString());
    }

    private static ExperimentSpec Spec(string hypothesis, string epicId) => new(
        hypothesis,
        new(ExperimentInterventionKind.BriefOrPromptChange, "Preserve this intervention"),
        new(ExperimentBaselineKind.GoalCohort, BaselineGoalIds: ["baseline"], ComparisonGoalIds: ["comparison"]),
        ["rounds-per-landing"],
        new("rounds-per-landing", new("rounds-per-landing", ">", 20)),
        new(2, ExperimentStopUnit.Goals),
        new([new("rounds-per-landing", "<", -10)], [new("rounds-per-landing", ">", 10)]),
        epicId);

    private static string Json<T>(T value) => JsonSerializer.Serialize(value, ExperimentStore.JsonOptions);

    private static void InsertExperiment(SqliteConnection connection, string id, ExperimentSpec spec)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO experiments VALUES ($id,$hypothesis,$epic,$created,$intervention,$baseline,$metrics,
                $guardrail,$stop,$rule,'open',NULL);
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$hypothesis", spec.Hypothesis);
        command.Parameters.AddWithValue("$epic", spec.EpicId!);
        command.Parameters.AddWithValue("$created", "2026-10-10T00:00:00.0000000+00:00");
        command.Parameters.AddWithValue("$intervention", Json(spec.Intervention));
        command.Parameters.AddWithValue("$baseline", Json(spec.Baseline));
        command.Parameters.AddWithValue("$metrics", Json(spec.Metrics));
        command.Parameters.AddWithValue("$guardrail", Json(spec.Guardrail));
        command.Parameters.AddWithValue("$stop", Json(spec.StopRule));
        command.Parameters.AddWithValue("$rule", Json(spec.DecisionRule));
        Assert.Equal(1, command.ExecuteNonQuery());
    }

    private static List<string> Snapshot(string path, string sql)
    {
        using var connection = Open(path, SqliteOpenMode.ReadOnly);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var rows = new List<string>();
        while (reader.Read())
        {
            var values = new object[reader.FieldCount];
            reader.GetValues(values);
            rows.Add(JsonSerializer.Serialize(values));
        }
        return rows;
    }

    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private static int Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteNonQuery();
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
