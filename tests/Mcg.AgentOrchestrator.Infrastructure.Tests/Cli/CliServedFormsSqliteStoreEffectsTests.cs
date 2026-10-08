using System.Reflection;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

// Parallel-safe: every case owns its databases; all connections disable pooling,
// and console capture is async-local. No process-wide pool clearing is needed.
public sealed class CliServedFormsSqliteStoreEffectsTests : CliTaskQueryTestSupport
{
    private const string GoalIdValue = "abc10000aaaaaaaaaaaaaaaaaaaaaaaa";
    private static readonly IReadOnlyDictionary<string, string> Exemptions =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["backlog-list"] = "Pending backlog slice switches to read-only opens and removes this exemption.",
            ["backlog-show"] = "Pending backlog slice switches to read-only opens and removes this exemption."
        };

    private static IReadOnlyDictionary<string, string[][]> ReadServedForms()
    {
        var field = typeof(CliCommandCapabilitiesTestsQueryRouteConvention)
            .GetField("ServedForms", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);
        var forms = Assert.IsAssignableFrom<IReadOnlyDictionary<string, string[][]>>(field.GetValue(null));
        Assert.NotEmpty(forms);
        Assert.All(forms.Values, value => Assert.NotEmpty(value));
        return forms;
    }

    public static TheoryData<string, string[]> ServedFormCases()
    {
        var cases = new TheoryData<string, string[]>();
        foreach (var (verb, forms) in ReadServedForms())
            foreach (var args in forms)
                cases.Add(verb, args);
        return cases;
    }

    [Theory]
    [MemberData(nameof(ServedFormCases))]
    public void ServedForm_Execution_LeavesSqliteStoresUnchanged(string verb, string[] args)
    {
        WithWorkspace((workspace, kernel) =>
        {
            Assert.False(File.Exists(workspace.PortfolioStorePath));
            var before = SnapshotDatabases(workspace.OrchestratorDirectory);
            Assert.NotEmpty(before);
            ExecuteReadOnly(args, workspace, kernel);
            var differences = DescribeDifferences(string.Join(" ", args), before,
                SnapshotDatabases(workspace.OrchestratorDirectory));
            if (Exemptions.TryGetValue(verb, out var reason))
            {
                TestContext.Current.SendDiagnosticMessage($"{verb}: {reason} Tolerated differences: " +
                    (differences.Count == 0 ? "none" : string.Join(Environment.NewLine, differences)));
                return;
            }
            Assert.True(differences.Count == 0, string.Join(Environment.NewLine, differences));
        });
    }

    [Fact]
    public void Exemptions_BacklogReadForms_OnlyPendingBacklogSliceIsExempt()
    {
        Assert.Equal(new[] { "backlog-list", "backlog-show" },
            Exemptions.Keys.OrderBy(key => key, StringComparer.Ordinal));
        var served = ReadServedForms();
        foreach (var (verb, reason) in Exemptions)
        {
            Assert.True(served.ContainsKey(verb), $"{verb}: exemption must name a served form.");
            Assert.False(string.IsNullOrWhiteSpace(reason));
            Assert.Contains("backlog slice", reason, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void SnapshotHelper_WriterForm_ReportsFormAndCreatedFile()
    {
        WithWorkspace((workspace, kernel) =>
        {
            var before = SnapshotDatabases(workspace.OrchestratorDirectory);
            Assert.NotEmpty(before);
            Assert.Empty(DescribeDifferences("untouched", before,
                SnapshotDatabases(workspace.OrchestratorDirectory)));
            Assert.False(File.Exists(workspace.PortfolioStorePath));
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var profiles = WorkerProfileCatalog.Default();
            Goal? currentGoal = null;
            string[] args = ["epic-add", "Snapshot negative control"];
            var output = CaptureConsole(() => CliCommandDispatcher.ExecuteCommand(
                args, kernel, workspace, ref agents, new InMemoryModelProviderRegistry([]),
                ref profiles, ref currentGoal));
            Assert.Contains("Added epic:", output);
            var differences = DescribeDifferences(string.Join(" ", args), before,
                SnapshotDatabases(workspace.OrchestratorDirectory));
            Assert.Contains(differences, difference =>
                difference.Contains("epic-add", StringComparison.Ordinal) &&
                difference.Contains("portfolio.db", StringComparison.Ordinal) &&
                difference.Contains("existence changed (absent -> present)", StringComparison.Ordinal));
        });
    }

    [Fact]
    public void Status_NoPortfolioDatabase_PrintsNoEpicAndCreatesNoFile()
    {
        WithWorkspace((workspace, kernel) =>
        {
            Assert.False(File.Exists(workspace.PortfolioStorePath));
            var before = SnapshotDatabases(workspace.OrchestratorDirectory);
            var output = ExecuteReadOnly(["status", "abc10000"], workspace, kernel);
            Assert.Contains("Query route goal", output);
            Assert.DoesNotContain("Portfolio:", output);
            Assert.False(File.Exists(workspace.PortfolioStorePath));
            AssertUnchanged("status abc10000", workspace, before);
        });
    }

    [Fact]
    public void Status_CurrentPortfolioDatabase_PrintsMembershipWithoutChanges()
    {
        WithWorkspace((workspace, kernel) =>
        {
            SeedMembership(workspace);
            var before = SnapshotDatabases(workspace.OrchestratorDirectory);
            Assert.Contains("portfolio.db", before.Keys);
            var output = ExecuteReadOnly(["status", "abc10000"], workspace, kernel);
            Assert.Contains("Portfolio: epic=Snapshot epic", output);
            AssertUnchanged("status abc10000", workspace, before);
        });
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("older")]
    [InlineData("newer")]
    public void Status_NonCurrentPortfolioSchema_PrintsNoEpicWithoutChanges(string schema)
    {
        WithWorkspace((workspace, kernel) =>
        {
            var absentOutput = ExecuteReadOnly(["status", "abc10000"], workspace, kernel);
            SeedMembership(workspace);
            using (var connection = OpenDatabase(workspace.PortfolioStorePath, SqliteOpenMode.ReadWrite))
            {
                using var command = connection.CreateCommand();
                command.CommandText = schema == "missing"
                    ? "DELETE FROM store_schema_versions WHERE store_name = 'portfolio'"
                    : "UPDATE store_schema_versions SET version = $version WHERE store_name = 'portfolio'";
                if (schema != "missing")
                    command.Parameters.AddWithValue("$version", StoreSchemaRegistry.Portfolio.CurrentVersion!.Value +
                        (schema == "older" ? -1 : 1));
                Assert.Equal(1, command.ExecuteNonQuery());
            }
            var before = SnapshotDatabases(workspace.OrchestratorDirectory);
            var output = ExecuteReadOnly(["status", "abc10000"], workspace, kernel);
            Assert.Equal(absentOutput, output);
            AssertUnchanged("status abc10000", workspace, before);
        });
    }

    [Fact]
    public void Status_CorruptPortfolioDatabase_PropagatesFailure()
    {
        WithWorkspace((workspace, kernel) =>
        {
            File.WriteAllText(workspace.PortfolioStorePath, "not a SQLite database");
            Assert.Throws<SqliteException>(() => ExecuteReadOnly(["status", "abc10000"], workspace, kernel));
            Assert.Equal("not a SQLite database", File.ReadAllText(workspace.PortfolioStorePath));
        });
    }

    [Theory]
    [InlineData("size")]
    [InlineData("schema-version rows")]
    [InlineData("deleted")]
    public void SnapshotHelper_NestedUnknownStore_ReportsChangedAttribute(string change)
    {
        WithWorkspace((workspace, _) =>
        {
            var path = Path.Combine(workspace.OrchestratorDirectory, "future", "unknown.db");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using (var connection = OpenDatabase(path, SqliteOpenMode.ReadWriteCreate))
            {
                using var command = connection.CreateCommand();
                command.CommandText = "CREATE TABLE future_schema_versions (version INTEGER); " +
                    "INSERT INTO future_schema_versions VALUES (1);";
                command.ExecuteNonQuery();
            }
            var before = SnapshotDatabases(workspace.OrchestratorDirectory);
            var relativePath = Path.GetRelativePath(workspace.OrchestratorDirectory, path);
            Assert.Contains(relativePath, before.Keys);
            if (change == "deleted")
                File.Delete(path);
            else
            {
                using var connection = OpenDatabase(path, SqliteOpenMode.ReadWrite);
                using var command = connection.CreateCommand();
                command.CommandText = change == "size"
                    ? "CREATE TABLE payload (value BLOB); INSERT INTO payload VALUES (zeroblob(65536));"
                    : "UPDATE future_schema_versions SET version = 2";
                command.ExecuteNonQuery();
            }
            var after = SnapshotDatabases(workspace.OrchestratorDirectory);
            if (change == "schema-version rows")
                Assert.Equal(before[relativePath].ByteLength, after[relativePath].ByteLength);
            var differences = DescribeDifferences("snapshot control", before, after);
            var attribute = change == "deleted" ? "existence" : change;
            Assert.Contains(differences, difference =>
                difference.Contains("snapshot control", StringComparison.Ordinal) &&
                difference.Contains(relativePath, StringComparison.Ordinal) &&
                difference.Contains(attribute, StringComparison.Ordinal));
        });
    }

    private static void SeedMembership(OrchestratorWorkspace workspace)
    {
        var store = new PortfolioStore(workspace.PortfolioStorePath);
        var epic = store.AddEpicAsync("Snapshot epic").GetAwaiter().GetResult();
        store.AssignGoalToEpicAsync(GoalIdValue, epic.Id).GetAwaiter().GetResult();
    }

    private static string ExecuteReadOnly(string[] args, OrchestratorWorkspace workspace,
        AgentOrchestratorKernel kernel)
    {
        var repository = new ProbeStateRepository(kernel) { ThrowOnOutbox = true };
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;
        return CaptureConsole(() =>
        {
            Assert.True(CliReadOnlyCommandRunner.TryExecute(args, repository, workspace,
                new InMemoryModelProviderRegistry([]), null, ref agents, ref profiles, ref currentGoal,
                out var changed), $"{string.Join(" ", args)}: read-only runner declined the form.");
            Assert.False(changed);
        });
    }

    private static void WithWorkspace(Action<OrchestratorWorkspace, AgentOrchestratorKernel> action)
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            StateDbMigrations.EnsureUpToDate(workspace.SqliteStatePath);
            _ = new BacklogStore(workspace.BacklogStorePath);
            using (var connection = OpenDatabase(workspace.BacklogStorePath, SqliteOpenMode.ReadWrite))
            {
                using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO backlog (id, title, body, status, created_at, updated_at, source_goal_id)
                    VALUES ('abc10000eeeeeeeeeeeeeeeeeeeeeeee', 'Query route seed', '', 'Open',
                            '2026-10-07T00:00:00Z', '2026-10-07T00:00:00Z', NULL);
                    """;
                command.ExecuteNonQuery();
            }
            _ = CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory);
            var kernel = new AgentOrchestratorKernel();
            kernel.CreateGoal(new GoalId(GoalIdValue), "Query route goal",
                [new TaskSpec(new TaskId("abc10000dddddddddddddddddddddddd"),
                    "Query route task", AgentRole.Developer)]);
            action(workspace, kernel);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed record DatabaseSnapshot(long ByteLength, string[] SchemaVersionRows);

    private static SqliteConnection OpenDatabase(string path, SqliteOpenMode mode)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = path, Mode = mode, Pooling = false }.ToString());
        try { connection.Open(); return connection; }
        catch { connection.Dispose(); throw; }
    }

    private static Dictionary<string, DatabaseSnapshot> SnapshotDatabases(string directory)
    {
        var snapshots = new Dictionary<string, DatabaseSnapshot>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(directory, "*.db", SearchOption.AllDirectories))
        {
            using var connection = OpenDatabase(path, SqliteOpenMode.ReadOnly);
            using var tablesCommand = connection.CreateCommand();
            tablesCommand.CommandText = """
                SELECT name FROM sqlite_master WHERE type = 'table'
                AND (name LIKE '%schema_version%' OR name LIKE '%schema_migration%') ORDER BY name
                """;
            var tables = new List<string>();
            using (var reader = tablesCommand.ExecuteReader())
                while (reader.Read()) tables.Add(reader.GetString(0));
            var versionRows = new List<string>();
            foreach (var table in tables)
            {
                versionRows.Add(JsonSerializer.Serialize(table)); // Include empty version tables.
                using var rowsCommand = connection.CreateCommand();
                rowsCommand.CommandText = $"SELECT * FROM \"{table.Replace("\"", "\"\"")}\"";
                using var reader = rowsCommand.ExecuteReader();
                var rows = new List<string>();
                while (reader.Read())
                    rows.Add(JsonSerializer.Serialize(Enumerable.Range(0, reader.FieldCount)
                        .Select(index => reader.IsDBNull(index) ? null : reader.GetValue(index)).ToArray()));
                versionRows.AddRange(rows.OrderBy(row => row, StringComparer.Ordinal));
            }
            snapshots.Add(Path.GetRelativePath(directory, path),
                new DatabaseSnapshot(new FileInfo(path).Length, versionRows.ToArray()));
        }
        return snapshots;
    }

    private static List<string> DescribeDifferences(string form,
        IReadOnlyDictionary<string, DatabaseSnapshot> before,
        IReadOnlyDictionary<string, DatabaseSnapshot> after)
    {
        var differences = new List<string>();
        foreach (var path in before.Keys.Union(after.Keys).OrderBy(path => path, StringComparer.Ordinal))
        {
            var existed = before.TryGetValue(path, out var previous);
            var exists = after.TryGetValue(path, out var current);
            if (existed != exists)
            {
                differences.Add($"{form}: {path} existence changed " +
                    $"({(existed ? "present" : "absent")} -> {(exists ? "present" : "absent")})");
                continue;
            }
            if (previous!.ByteLength != current!.ByteLength)
                differences.Add($"{form}: {path} size changed ({previous.ByteLength} -> {current.ByteLength})");
            if (!previous.SchemaVersionRows.SequenceEqual(current.SchemaVersionRows, StringComparer.Ordinal))
                differences.Add($"{form}: {path} schema-version rows changed " +
                    $"({JsonSerializer.Serialize(previous.SchemaVersionRows)} -> " +
                    $"{JsonSerializer.Serialize(current.SchemaVersionRows)})");
        }
        return differences;
    }

    private static void AssertUnchanged(string form, OrchestratorWorkspace workspace,
        IReadOnlyDictionary<string, DatabaseSnapshot> before)
    {
        var differences = DescribeDifferences(form, before, SnapshotDatabases(workspace.OrchestratorDirectory));
        Assert.True(differences.Count == 0, string.Join(Environment.NewLine, differences));
    }
}
