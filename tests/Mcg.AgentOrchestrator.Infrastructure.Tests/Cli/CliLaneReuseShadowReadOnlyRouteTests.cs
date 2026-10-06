using System.Globalization;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel safe: private temporary roots and AsyncLocal console capture; no shared state.
public sealed class CliLaneReuseShadowReadOnlyRouteTests : CliTaskQueryTestSupport
{
    private static readonly string[] Window = ["--since", "2026-09-29T00:00:00Z", "--until", "2026-10-06T00:00:00Z"];

    [Theory]
    [InlineData("lane-reuse-shadow")]
    [InlineData("LANE-REUSE-SHADOW")]
    [InlineData("lane-reuse-shadow", "--since", "2000-01-01T00:00:00Z")]
    [InlineData("lane-reuse-shadow", "--until", "2026-10-06T00:00:00Z")]
    [InlineData("lane-reuse-shadow", "--json")]
    public void EachFlagTakesTheQueryOnlyReadOnlyRoute(params string[] args)
    {
        Assert.True(CliReadOnlyCommandRunner.IsReadOnlyCommand(args));
        Assert.Equal(CliCommandCapability.QueryOnly, CliCommandCapabilities.Classify(args));
        var root = CreateTempDirectory();
        try
        {
            var repository = new ProbeStateRepository(new AgentOrchestratorKernel()) { ThrowOnOutbox = true };
            Run(args, OrchestratorWorkspace.ForDirectory(root), repository);
            AssertNoStateCalls(repository);
            Assert.Empty(Directory.GetFiles(root, "*", SearchOption.AllDirectories));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void SeededRecordsGiveIdenticalTableAndJsonWithoutChangingFilesOrOpeningState()
    {
        var root = CreateTempDirectory();
        try
        {
            // A tenant workspace still reads the recorder's host-root directory.
            var workspace = OrchestratorWorkspace.ForDirectory(root, tenantName: "report-tenant");
            Seed(root);
            var before = Snapshot(root);
            var repository = new ProbeStateRepository(new AgentOrchestratorKernel()) { ThrowOnOutbox = true };
            var table = Run(["lane-reuse-shadow", .. Window], workspace, repository);
            using var document = JsonDocument.Parse(Run(["lane-reuse-shadow", .. Window, "--json"], workspace, repository));
            var json = document.RootElement;
            var lines = table.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToArray();
            var summary = lines[0].Split('\t').ToDictionary(token => token.Split('=')[0],
                token => token.Split('=')[1].Split(' ')[0]);
            (string Table, string Json, double Expected)[] numbers = [
                ("gates", "gates", 2), ("lane_rows", "laneRows", 5),
                ("would_reuse_rows", "wouldReuseRows", 3), ("would_reuse_share", "wouldReuseShare", 0.6),
                ("executed_lane_seconds", "executedLaneSeconds", 6),
                ("saved_lane_seconds", "savedLaneSeconds", 2.5), ("saved_share", "savedShare", 0.4167),
                ("flake_confirmed_misses", "flakeConfirmedMisses", 1),
                ("untimed_records", "untimedRecords", 1), ("unreadable_records", "unreadableRecords", 1)
            ];
            foreach (var (tableKey, jsonKey, expected) in numbers)
            {
                Assert.Equal(expected, json.GetProperty(jsonKey).GetDouble());
                Assert.Equal(expected, double.Parse(summary[tableKey], CultureInfo.InvariantCulture));
            }
            Assert.Equal("2", summary["misses"]);
            Assert.Equal("2026-09-29T00:00:00+00:00", json.GetProperty("since").GetString());
            Assert.Equal("2026-10-06T00:00:00+00:00", json.GetProperty("until").GetString());
            Assert.Equal("must-run reason\tlanes", lines[1]);
            Assert.Equal(new[] { "always-affected:built-binary\t1", "always-affected:process-spawning\t1" }, lines[2..4]);
            Assert.Equal(new[] { ("always-affected:built-binary", 1), ("always-affected:process-spawning", 1) },
                json.GetProperty("mustRunReasons").EnumerateArray().Select(r =>
                    (r.GetProperty("family").GetString(), r.GetProperty("lanes").GetInt32())));

            var tableMisses = lines.Where(l => l.StartsWith("miss\t", StringComparison.Ordinal) &&
                !l.StartsWith("miss\tgoal\t", StringComparison.Ordinal)).Select(l => l.Split('\t')).ToArray();
            var jsonMisses = json.GetProperty("misses").EnumerateArray().ToArray();
            Assert.Equal(2, jsonMisses.Length);
            Assert.Equal(new[] { ("goal-a", "attempt-a", "reuse-a"), ("goal-b", "attempt-z", "reuse-z") },
                jsonMisses.Select(m => (m.GetProperty("goalId").GetString(), m.GetProperty("attemptId").GetString(),
                    m.GetProperty("lane").GetString())));
            Assert.Equal(jsonMisses.Length, tableMisses.Length);
            for (var i = 0; i < jsonMisses.Length; i++)
            {
                var miss = jsonMisses[i];
                var fields = tableMisses[i];
                Assert.Equal(miss.GetProperty("goalId").GetString(), fields[1]);
                Assert.Equal(miss.GetProperty("attemptId").GetString(), fields[2]);
                Assert.Equal(miss.GetProperty("lane").GetString(), fields[3]);
                Assert.Equal("reference-green-execution-red", fields[4]);
                Assert.Equal(miss.GetProperty("missReason").GetString(), fields[4]);
                Assert.Equal(miss.GetProperty("referenceSource").GetString(), fields[5]);
                Assert.Equal(miss.GetProperty("flakeConfirmed").GetBoolean(), bool.Parse(fields[6]));
                Assert.Equal(miss.GetProperty("recordedAt").GetDateTimeOffset(),
                    DateTimeOffset.Parse(fields[7], CultureInfo.InvariantCulture));
                Assert.Equal(miss.GetProperty("reason").GetString(), fields[8]);
                Assert.Equal(miss.GetProperty("failedPredicate").GetString(), fields[9]);
                Assert.Equal(miss.GetProperty("failingClasses").EnumerateArray().Select(c => c.GetString()),
                    JsonSerializer.Deserialize<string[]>(fields[10]));
            }
            Assert.True(jsonMisses[0].GetProperty("flakeConfirmed").GetBoolean());
            Assert.False(jsonMisses[1].GetProperty("flakeConfirmed").GetBoolean());
            Assert.Equal("predicate-z", jsonMisses[1].GetProperty("failedPredicate").GetString());
            Assert.Equal(new[] { "FailingZ" }, jsonMisses[1].GetProperty("failingClasses").EnumerateArray().Select(c => c.GetString()));
            AssertNoStateCalls(repository);
            AssertUnchanged(root, before);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData("--since", "2026-10-06T00:00:00Z", "--until", "2026-10-01T00:00:00Z")]
    [InlineData("--since", "2026-10-06T00:00:00Z", "--until", "2026-10-06T00:00:00Z")]
    [InlineData("--since", "invalid")]
    [InlineData("--until", "invalid")]
    [InlineData("--since")]
    [InlineData("--until")]
    [InlineData("--bogus")]
    public void InvalidArgumentsPrintOnlyUsageAndLeaveAllInputsUnchanged(params string[] flags)
    {
        var root = CreateTempDirectory();
        try
        {
            Seed(root);
            var before = Snapshot(root);
            var repository = new ProbeStateRepository(new AgentOrchestratorKernel()) { ThrowOnOutbox = true };
            var stdout = "";
            var stderr = AsyncLocalConsoleRouter.CaptureError(() =>
                stdout = Run(["lane-reuse-shadow", .. flags], OrchestratorWorkspace.ForDirectory(root), repository));
            Assert.Equal("", stdout);
            Assert.Equal(CliCommandHelp.LaneReuseShadowUsage + Environment.NewLine, stderr);
            AssertNoStateCalls(repository);
            AssertUnchanged(root, before);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void HelpDeclinesReadOnlyExecutionAndUnknownFlagThrowsWithUsage()
    {
        Assert.False(CliReadOnlyCommandRunner.IsReadOnlyCommand(["lane-reuse-shadow", "--help"]));
        Assert.True(CliReadOnlyCommandRunner.IsReadOnlyCommand(["lane-reuse-shadow", "--bogus"]));
        var error = Assert.Throws<ArgumentException>(() =>
            CliCommandHelp.ThrowIfInvalidFlags(["lane-reuse-shadow", "--bogus"]));
        Assert.Contains(CliCommandHelp.LaneReuseShadowUsage, error.Message);
    }

    [Theory]
    [InlineData("2026-10-06T00:00:00Z")]
    [InlineData("2026-10-05T19:00:00-05:00")]
    [InlineData("2026-10-06T00:00:00")]
    public void DefaultSinceIsSevenDaysBeforeExplicitUntilAndOffsetsNormalizeToUtc(string until)
    {
        var root = CreateTempDirectory();
        try
        {
            Seed(root);
            var repository = new ProbeStateRepository(new AgentOrchestratorKernel()) { ThrowOnOutbox = true };
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            Assert.Equal(Run(["lane-reuse-shadow", .. Window, "--json"], workspace, repository),
                Run(["lane-reuse-shadow", "--until", until, "--json"], workspace, repository));
            AssertNoStateCalls(repository);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void MissingDirectoryAndTimestampedRecordsWithoutLanesAreValidEmptyReports()
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            var repository = new ProbeStateRepository(new AgentOrchestratorKernel()) { ThrowOnOutbox = true };
            using var empty = JsonDocument.Parse(Run(["lane-reuse-shadow", .. Window, "--json"], workspace, repository));
            Assert.Equal(0, empty.RootElement.GetProperty("gates").GetInt32());
            Assert.Equal(0, empty.RootElement.GetProperty("wouldReuseShare").GetDouble());
            Assert.Equal(0, empty.RootElement.GetProperty("savedShare").GetDouble());
            Assert.Empty(Directory.GetFiles(root, "*", SearchOption.AllDirectories));
            Write(root, "goal", "empty", "{\"recorded_at\":\"2026-10-01T00:00:00Z\"}");
            Write(root, "goal", "null", "{\"recorded_at\":\"2026-10-01T00:00:00Z\",\"lanes\":null}");
            var before = Snapshot(root);
            using var report = JsonDocument.Parse(Run(["lane-reuse-shadow", .. Window, "--json"], workspace, repository));
            Assert.Equal(2, report.RootElement.GetProperty("gates").GetInt32());
            Assert.Equal(0, report.RootElement.GetProperty("laneRows").GetInt32());
            Assert.Equal(0, report.RootElement.GetProperty("unreadableRecords").GetInt32());
            AssertNoStateCalls(repository);
            AssertUnchanged(root, before);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData("{\"recorded_at\":17}")]
    [InlineData("{\"recorded_at\":\"invalid\"}")]
    [InlineData("{\"recorded_at\":\"2026-10-01T00:00:00Z\",\"lanes\":{}}")]
    [InlineData("{\"recorded_at\":\"2026-10-01T00:00:00Z\",\"lanes\":[{\"lane\":\"partial\",\"decision\":\"would-reuse\",\"executed\":true,\"duration_ms\":1000},{}]}")]
    [InlineData("[]")]
    public void MalformedRecordIsExcludedAsAWhole(string record)
    {
        var root = CreateTempDirectory();
        try
        {
            Write(root, "goal", "bad", record);
            Write(root, "goal", "untimed", "{\"recorded_at\":null}");
            var repository = new ProbeStateRepository(new AgentOrchestratorKernel()) { ThrowOnOutbox = true };
            using var json = JsonDocument.Parse(Run(["lane-reuse-shadow", .. Window, "--json"],
                OrchestratorWorkspace.ForDirectory(root), repository));
            Assert.Equal(0, json.RootElement.GetProperty("gates").GetInt32());
            Assert.Equal(0, json.RootElement.GetProperty("laneRows").GetInt32());
            Assert.Equal(1, json.RootElement.GetProperty("unreadableRecords").GetInt32());
            Assert.Equal(1, json.RootElement.GetProperty("untimedRecords").GetInt32());
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

    private static void AssertNoStateCalls(ProbeStateRepository repository) => Assert.Equal(0,
        repository.FullLoadAttempts + repository.LoadGoalsCount + repository.ListGoalMetadataCount +
        repository.SaveAttempts + repository.MergeSaveAttempts + repository.MutationAttempts +
        repository.ListOutboxMessagesCount + repository.OutboxClaimAttempts);

    private static Dictionary<string, byte[]> Snapshot(string root) =>
        Directory.GetFiles(root, "*", SearchOption.AllDirectories).ToDictionary(p => p, File.ReadAllBytes);

    private static void AssertUnchanged(string root, Dictionary<string, byte[]> before)
    {
        Assert.Equal(before.Keys.Order(), Directory.GetFiles(root, "*", SearchOption.AllDirectories).Order());
        foreach (var (path, bytes) in before) Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    private static void Write(string root, string goal, string attempt, string record)
    {
        var directory = Path.Combine(root, ".orchestrator", "lane-reuse-shadow", goal);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, attempt + ".json"), record);
    }

    private static void Seed(string root)
    {
        Write(root, "goal-b", "attempt-z", Record("2026-10-05T00:00:00Z", """
            {"lane":"reuse-z","partition_id":"reuse-z","decision":"would-reuse","reason":"unaffected","executed":true,"verdict":"RED","duration_ms":1500,"miss_evaluated":true,"reference_verdict":"GREEN","reference_source":"baseline/attempt","shadow_miss":true,"miss_reason":"reference-green-execution-red","failed_predicate":"predicate-z","flake_confirmed":null,"failing_classes":["FailingZ"]},
            {"lane":"built","partition_id":"built","decision":"must-run","reason":"always-affected:built-binary:consumer","executed":true,"verdict":"GREEN","duration_ms":2500},
            {"lane":"dormant","partition_id":"dormant","decision":"would-reuse","reason":"unaffected","executed":false,"verdict":null,"duration_ms":9000}
            """));
        var early = Record("2026-10-04T00:00:00Z", """
            {"lane":"reuse-a","partition_id":"reuse-a","decision":"would-reuse","reason":"unaffected","executed":true,"verdict":"RED","duration_ms":1000,"miss_evaluated":true,"reference_verdict":"GREEN","reference_source":"baseline/attempt","shadow_miss":true,"miss_reason":"reference-green-execution-red","failed_predicate":"predicate-a","flake_confirmed":true,"failing_classes":["FailingA"]},
            {"lane":"process","partition_id":"process","decision":"must-run","reason":"always-affected:process-spawning:consumer","executed":true,"verdict":"GREEN","duration_ms":1000}
            """);
        Write(root, "goal-a", "attempt-a", early);
        Write(root, "goal-a", "outside", early.Replace("2026-10-04", "2026-09-28"));
        Write(root, "goal-a", "untimed", Record(null,
            "{\"lane\":\"legacy\",\"partition_id\":\"legacy\",\"decision\":\"would-reuse\",\"reason\":\"unaffected\",\"executed\":true,\"verdict\":\"GREEN\",\"duration_ms\":1000}"));
        Write(root, "goal-a", "malformed", "{torn-record");
        var directory = Path.Combine(root, ".orchestrator", "lane-reuse-shadow", "goal-a");
        File.WriteAllText(Path.Combine(directory, "ignored.tmp"), "{torn-record");
        Directory.CreateDirectory(Path.Combine(directory, "nested"));
        File.WriteAllText(Path.Combine(directory, "nested", "ignored.json"), "{torn-record");
    }

    private static string Record(string? timestamp, string lanes) => $$"""
        {"main_sha":"main","candidate_tree_sha":"tree","verifying_commit_sha":"candidate","changed_file_count":1,"status":"resolved","ignored_paths":[],"lanes":[{{lanes}}],"summary":{"lane_count":0,"would_reuse_count":0,"must_run_count":0,"would_reuse_executed_count":0,"shadow_miss_count":0}{{(timestamp is null ? "" : ",\"recorded_at\":\"" + timestamp + "\",\"main_tree_sha\":\"main-tree\",\"unreadable_reference_records\":0")}}}
        """;
}
