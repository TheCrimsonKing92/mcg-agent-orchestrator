using System.Globalization;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

// Parallel safe: each real SQLite store has a private root; console capture is AsyncLocal.
public sealed class CliOperatorIntentCoverageReadOnlyRouteTests : CliTaskQueryTestSupport
{
    private static readonly string[] Window = ["--since", "2026-10-13T00:00:00Z", "--until", "2026-10-16T00:00:00Z"];

    [Theory]
    [InlineData("intent-coverage")]
    [InlineData("INTENT-COVERAGE")]
    [InlineData("intent-coverage", "--json")]
    [InlineData("intent-coverage", "--min-cases", "5")]
    [InlineData("intent-coverage", "--since", "2026-10-13T00:00:00Z")]
    public void EachArgumentFormUsesQueryOnlyWithoutFilesOrState(params string[] args)
    {
        Assert.True(CliReadOnlyCommandRunner.IsReadOnlyCommand(args));
        Assert.Equal(CliCommandCapability.QueryOnly, CliCommandCapabilities.Classify(args));
        var root = CreateTempDirectory();
        try
        {
            var repository = new ProbeStateRepository(new AgentOrchestratorKernel()) { ThrowOnOutbox = true };
            var stdout = "";
            var stderr = AsyncLocalConsoleRouter.CaptureError(() =>
                stdout = Run([.. args, "--until", "2026-10-16T00:00:00Z"],
                    OrchestratorWorkspace.ForDirectory(root), repository));
            Assert.Equal(new[] {
                "Warning: intent-coverage: operator-intents.db is missing.",
                "Warning: intent-coverage: steward-triggers.db is missing."
            }, stderr.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
            if (args.Contains("--json"))
            {
                using var json = JsonDocument.Parse(stdout);
                Assert.Equal(0, json.RootElement.GetProperty("humanIntents").GetInt32());
                Assert.Empty(json.RootElement.GetProperty("verbs").EnumerateArray());
            }
            else Assert.Contains("human_intents=0", stdout);
            AssertNoStateCalls(repository);
            Assert.Empty(Directory.GetFileSystemEntries(root));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void SeededStoresGiveMatchingCountsWithoutChangingDatabasesOrState()
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            Seed(workspace);
            var before = Snapshot(root);
            var repository = new ProbeStateRepository(new AgentOrchestratorKernel()) { ThrowOnOutbox = true };
            var table = "";
            var errors = AsyncLocalConsoleRouter.CaptureError(() =>
                table = Run(["intent-coverage", .. Window], workspace, repository));
            Assert.Equal("", errors);
            AssertUnchanged(root, before);
            var stdout = "";
            errors = AsyncLocalConsoleRouter.CaptureError(() =>
                stdout = Run(["intent-coverage", .. Window, "--json"], workspace, repository));
            Assert.Equal("", errors);
            AssertUnchanged(root, before);
            using var document = JsonDocument.Parse(stdout);
            var json = document.RootElement;
            Assert.Equal(6, json.GetProperty("humanIntents").GetInt32());
            Assert.Equal(1, json.GetProperty("agreed").GetInt32());
            Assert.Equal(1, json.GetProperty("silent").GetInt32());
            Assert.Equal(4, json.GetProperty("uncovered").GetInt32());
            var lines = table.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToArray();
            Assert.Contains("human_intents=6", lines[0]);
            var tableVerbs = lines.Where(l => l.StartsWith("verb\t", StringComparison.Ordinal))
                .Select(l => l.Split('\t')).ToDictionary(f => f[1], f => Tokens(f.Skip(2)));
            var verbs = json.GetProperty("verbs").EnumerateArray().ToArray();
            Assert.Equal(3, verbs.Length);
            Assert.Equal(new[] { "adjudicate", "criterion-evidence-map", "retry" }, tableVerbs.Keys.Order(StringComparer.Ordinal));
            foreach (var verb in verbs)
            {
                var name = verb.GetProperty("verb").GetString()!;
                var expected = name switch
                {
                    "criterion-evidence-map" => (Total: 4, Agreed: 0, Silent: 0, Uncovered: 4),
                    "adjudicate" => (Total: 1, Agreed: 1, Silent: 0, Uncovered: 0),
                    _ => (Total: 1, Agreed: 0, Silent: 1, Uncovered: 0)
                };
                foreach (var (key, count) in new[] { ("total", expected.Total), ("agreed", expected.Agreed),
                    ("silent", expected.Silent), ("uncovered", expected.Uncovered) })
                {
                    Assert.Equal(count, verb.GetProperty(key).GetInt32());
                    Assert.Equal(count.ToString(CultureInfo.InvariantCulture), tableVerbs[name][key]);
                }
            }
            var classes = json.GetProperty("classes").EnumerateArray().ToArray();
            var rebind = Assert.Single(classes.Where(c => c.GetProperty("classKey").GetString() ==
                "criterion-evidence-map scope=acceptance binding=rebind"));
            Assert.Equal(1, rebind.GetProperty("total").GetInt32());
            Assert.True(rebind.GetProperty("structural").GetBoolean());
            Assert.False(rebind.GetProperty("automatableCandidate").GetBoolean());
            Assert.Equal(3, Assert.Single(classes.Where(c => c.GetProperty("classKey").GetString() ==
                "criterion-evidence-map scope=acceptance binding=first")).GetProperty("total").GetInt32());
            var tableClasses = lines.Where(l => l.StartsWith("class\t", StringComparison.Ordinal)).ToArray();
            Assert.Equal(classes.Select(c => c.GetProperty("classKey").GetString()), tableClasses.Select(l => l.Split('\t')[1]));
            AssertNoStateCalls(repository);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData("--min-cases", "0")]
    [InlineData("--min-cases", "-1")]
    [InlineData("--min-cases", "bad")]
    [InlineData("--min-cases")]
    [InlineData("--since", "bad")]
    [InlineData("--until")]
    [InlineData("--since", "2026-10-16", "--until", "2026-10-16")]
    [InlineData("--since", "2026-10-17", "--until", "2026-10-16")]
    [InlineData("--unknown")]
    public void InvalidArgumentsPrintOnlyUsageWithoutReadingStores(params string[] flags)
    {
        var root = CreateTempDirectory();
        try
        {
            var repository = new ProbeStateRepository(new AgentOrchestratorKernel()) { ThrowOnOutbox = true };
            var stdout = "";
            var stderr = AsyncLocalConsoleRouter.CaptureError(() =>
                stdout = Run(["intent-coverage", .. flags], OrchestratorWorkspace.ForDirectory(root), repository));
            Assert.Equal("", stdout);
            Assert.Equal(CliCommandHelp.IntentCoverageUsage + Environment.NewLine, stderr);
            AssertNoStateCalls(repository);
            Assert.Empty(Directory.GetFileSystemEntries(root));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void HelpDeclinesExecutionAndExplicitUntilDefaultsToSevenDays()
    {
        Assert.False(CliReadOnlyCommandRunner.IsReadOnlyCommand(["intent-coverage", "--help"]));
        Assert.Contains(CliCommandHelp.IntentCoverageUsage, Assert.Throws<ArgumentException>(() =>
            CliCommandHelp.ThrowIfInvalidFlags(["intent-coverage", "--bogus"])).Message);
        var root = CreateTempDirectory();
        try
        {
            var repository = new ProbeStateRepository(new AgentOrchestratorKernel());
            var stdout = Run(["intent-coverage", "--until", "2026-10-15T19:00:00-05:00", "--min-cases", "5", "--json"],
                OrchestratorWorkspace.ForDirectory(root), repository);
            using var json = JsonDocument.Parse(stdout);
            Assert.Equal("2026-10-09T00:00:00+00:00", json.RootElement.GetProperty("since").GetString());
            Assert.Equal("2026-10-16T00:00:00+00:00", json.RootElement.GetProperty("until").GetString());
            Assert.Equal(5, json.RootElement.GetProperty("minCases").GetInt32());
            AssertNoStateCalls(repository);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void InvalidStoredTimestampsAreSkippedWithAggregatedWarnings()
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            Seed(workspace);
            using (var connection = Open(Path.Combine(workspace.OrchestratorDirectory, "operator-intents.db")))
            {
                using var command = connection.CreateCommand();
                command.CommandText = "UPDATE operator_intents SET created_at = 'invalid' WHERE id IN ('map-a', 'map-b')";
                command.ExecuteNonQuery();
            }
            using (var connection = Open(Path.Combine(workspace.OrchestratorDirectory, "steward-triggers.db")))
            {
                using var command = connection.CreateCommand();
                command.CommandText = "UPDATE steward_triggers SET first_occurrence = 'invalid'";
                command.ExecuteNonQuery();
            }
            var before = Snapshot(root);
            var repository = new ProbeStateRepository(new AgentOrchestratorKernel());
            var stdout = "";
            var warnings = AsyncLocalConsoleRouter.CaptureError(() =>
                stdout = Run(["intent-coverage", .. Window, "--json"], workspace, repository));
            Assert.Contains("operator-intents.db: skipped 2 invalid rows.", warnings);
            Assert.Contains("steward-triggers.db: skipped 1 invalid rows.", warnings);
            using var json = JsonDocument.Parse(stdout);
            Assert.Equal(4, json.RootElement.GetProperty("humanIntents").GetInt32());
            Assert.Equal(0, json.RootElement.GetProperty("silent").GetInt32());
            AssertUnchanged(root, before);
            AssertNoStateCalls(repository);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static string Run(string[] args, OrchestratorWorkspace workspace, ProbeStateRepository repository)
    {
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var profiles = WorkerProfileCatalog.Default();
        Goal? goal = null;
        var stdout = CaptureConsole(() =>
        {
            Assert.True(CliReadOnlyCommandRunner.TryExecute(args, repository, workspace,
                new InMemoryModelProviderRegistry([]), null, ref agents, ref profiles, ref goal, out var changed));
            Assert.False(changed);
        });
        Assert.Null(goal);
        return stdout;
    }

    private static void AssertNoStateCalls(ProbeStateRepository repository)
    {
        Assert.Empty(repository.ObservedWriteOperationTags);
        Assert.Equal(0, repository.FullLoadAttempts + repository.LoadGoalsCount + repository.LoadGoalCount +
            repository.ListGoalMetadataCount + repository.SaveAttempts + repository.MergeSaveAttempts +
            repository.MutationAttempts + repository.ListOutboxMessagesCount + repository.OutboxClaimAttempts);
    }

    private static Dictionary<string, string> Tokens(IEnumerable<string> fields) => fields
        .Select(f => f.Split('=', 2)).ToDictionary(f => f[0], f => f[1]);

    private static Dictionary<string, (byte[] Bytes, DateTime Written)> Snapshot(string root) =>
        Directory.GetFiles(root, "*", SearchOption.AllDirectories).ToDictionary(p => p,
            p => (File.ReadAllBytes(p), File.GetLastWriteTimeUtc(p)));

    private static void AssertUnchanged(string root, Dictionary<string, (byte[] Bytes, DateTime Written)> before)
    {
        foreach (var (path, snapshot) in before)
        {
            Assert.Equal(snapshot.Bytes, File.ReadAllBytes(path));
            Assert.Equal(snapshot.Written, File.GetLastWriteTimeUtc(path));
        }
        var allowed = before.Keys.Where(p => p.EndsWith(".db", StringComparison.Ordinal))
            .SelectMany(p => new[] { p + "-wal", p + "-shm" }).ToHashSet(StringComparer.Ordinal);
        Assert.All(Directory.GetFiles(root, "*", SearchOption.AllDirectories),
            p => Assert.True(before.ContainsKey(p) || allowed.Contains(p), $"Unexpected file: {p}"));
    }

    private static void Seed(OrchestratorWorkspace workspace)
    {
        var intentsPath = Path.Combine(workspace.OrchestratorDirectory, "operator-intents.db");
        _ = new SqliteOperatorIntentStore(intentsPath, Path.Combine(workspace.OrchestratorDirectory, "wake"));
        var triggersPath = Path.Combine(workspace.OrchestratorDirectory, "steward-triggers.db");
        StewardTriggerStoreSetup.Setup(triggersPath);
        using (var connection = Open(intentsPath))
        {
            Insert("map-a", "criterion-evidence-map", "maps", Map("sha-a", 1), "2026-10-13T01:00:00Z");
            Insert("map-b", "criterion-evidence-map", "maps", Map("sha-b", 1), "2026-10-14T01:00:00Z");
            Insert("map-c", "criterion-evidence-map", "maps", Map("sha-c", 2), "2026-10-14T01:00:00Z");
            Insert("map-d", "criterion-evidence-map", "maps", Map("sha-d", 3), "2026-10-14T01:00:00Z");
            Insert("adjudicate", "adjudicate", "adjudicate-task", "{\"shape\":\"route\",\"cause\":\"X\"}", "2026-10-14T01:00:00Z");
            Insert("steward", "adjudicate", "adjudicate-task", "{\"shape\":\"route\",\"cause\":\"X\"}", "2026-10-14T00:59:00Z", "conductor-steward", "Agent");
            Insert("retry", "retry", "retry-task", "{\"retryCause\":2}", "2026-10-14T01:00:00Z");
            Insert("author", "answer", null, "{\"targetKind\":2,\"targetId\":\"question\"}", "2026-10-14T01:00:00Z", "conductor-author", "Agent");

            void Insert(string id, string verb, string? task, string payload, string at,
                string channel = "cli", string actorKind = "Human")
            {
                using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO operator_intents
                    (id, idempotency_key, verb, goal_id, task_id, payload_json, payload_file_references_json,
                     actor, channel, authentication_assurance, created_at, status, actor_kind)
                    VALUES ($id, $id, $verb, 'goal', $task, $payload, '[]', 'test', $channel, 'None', $at, 'Applied', $kind)
                    """;
                command.Parameters.AddWithValue("$id", id);
                command.Parameters.AddWithValue("$verb", verb);
                command.Parameters.AddWithValue("$task", (object?)task ?? DBNull.Value);
                command.Parameters.AddWithValue("$payload", payload);
                command.Parameters.AddWithValue("$channel", channel);
                command.Parameters.AddWithValue("$at", at);
                command.Parameters.AddWithValue("$kind", actorKind);
                command.ExecuteNonQuery();
            }
        }
        using (var connection = Open(triggersPath))
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO steward_triggers
                (key, goal_id, task_id, candidate_sha, kind, trigger_json, first_occurrence, status, intent_id)
                VALUES ('silent', 'goal', 'retry-task', 'sha', 'PlannerOutputContractRejected', '{}',
                        '2026-10-14T00:59:00Z', 'Serviced', NULL)
                """;
            command.ExecuteNonQuery();
        }
    }

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }

    private static string Map(string sha, int criterion) =>
        $$"""{"criterionIndex":{{criterion}},"requiredScope":"acceptance:full-gate","candidateSha":"{{sha}}"}""";
}
