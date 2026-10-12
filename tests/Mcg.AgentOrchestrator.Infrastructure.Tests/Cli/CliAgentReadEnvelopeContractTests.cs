using System.Globalization;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Private roots, injected clocks and AsyncLocal console capture; safe to run in parallel.
public sealed class CliAgentReadEnvelopeContractTests : CliTaskQueryTestSupport
{
    [Fact]
    public void RemoteExecutors_SeededLedgers_PinsEnvelopeAndPreservesPayload()
    {
        var root = CreateTempDirectory();
        try
        {
            var directory = Path.Combine(root, ".orchestrator");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "remote-lane-executors.json"),
                """{"executors":[{"id":"one","slots":2}],"lanes":[]}""");
            File.WriteAllLines(Path.Combine(directory, RemoteExecutorHealthLedger.FileName),
            [
                """{"observed_at":"2026-10-06T00:00:00Z","executor_id":"one","gate_attempt_id":"a","lane":"lane","outcome":"accepted","reason":null}""",
                """{"observed_at":"2026-10-06T01:00:00Z","executor_id":"one","gate_attempt_id":"b","lane":"lane","outcome":"unreachable","reason":"<ssh failure>"}""",
                "{"
            ]);
            RemoteExecutorProbeLedger.Append(Path.Combine(directory, RemoteExecutorProbeLedger.FileName),
                new RemoteExecutorProbeRow(new DateTimeOffset(2026, 10, 6, 2, 0, 0, TimeSpan.Zero),
                    "one", true, 0, false, "Ready", true, [], "tail"));
            var before = Directory.GetFiles(root, "*", SearchOption.AllDirectories)
                .ToDictionary(path => path, File.ReadAllBytes);
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            var report = RemoteExecutorReportReader.Read(workspace, null, 10);
            Assert.Equal(2, Assert.Single(report.Executors).Attempts);
            Assert.Equal(1, report.UnreadableOutcomeLines);
            var repository = new ProbeStateRepository(new AgentOrchestratorKernel()) { ThrowOnOutbox = true };

            using var json = JsonDocument.Parse(Run(["remote-executors", "--json"], workspace, repository));
            Assert.Equal("mcg.remote-executors", AgentReadEnvelope.RemoteExecutorsSchema);
            Assert.Equal(1, AgentReadEnvelope.RemoteExecutorsSchemaVersion);
            var payload = AssertEnvelope(json.RootElement, AgentReadEnvelope.RemoteExecutorsSchema,
                AgentReadEnvelope.RemoteExecutorsSchemaVersion);
            Assert.Equal(new[] { "executors", "unreadableOutcomeLines", "unreadableProbeLines" },
                payload.EnumerateObject().Select(property => property.Name));
            Assert.Equal(JsonSerializer.Serialize(report, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                payload.GetRawText());
            AssertNoStateEffects(repository);
            Assert.Equal(0, repository.ListGoalMetadataCount);
            Assert.Equal(before.Keys.Order(), Directory.GetFiles(root, "*", SearchOption.AllDirectories).Order());
            foreach (var (path, bytes) in before) Assert.Equal(bytes, File.ReadAllBytes(path));
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Goals_SeededOrEmptyStore_PinsFullRowsWithoutStateEffects(bool seed)
    {
        var root = CreateTempDirectory();
        try
        {
            var clock = new FixedClock { UtcNow = new DateTimeOffset(2026, 10, 6, 0, 0, 0, TimeSpan.Zero) };
            var kernel = new AgentOrchestratorKernel(clock);
            var fullObjective = "Second goal " + new string('x', 650) + " <full text>";
            if (seed)
            {
                kernel.CreateGoal(new GoalId("abc10000aaaaaaaaaaaaaaaaaaaaaaaa"), "First goal");
                clock.UtcNow = clock.UtcNow.AddHours(1);
                kernel.CreateGoal(new GoalId("abc20000bbbbbbbbbbbbbbbbbbbbbbbb"), fullObjective);
            }
            var repository = new ProbeStateRepository(kernel) { ThrowOnOutbox = true };
            string[] args = ["goals", "--json"];
            CliCommandHelp.ThrowIfInvalidFlags(args);
            Assert.Equal(CliCommandCapability.QueryOnly, CliCommandCapabilities.Classify(args));
            Assert.True(CliReadOnlyCommandRunner.IsReadOnlyCommand(args));

            using var json = JsonDocument.Parse(Run(args, OrchestratorWorkspace.ForDirectory(root), repository));
            Assert.Equal("mcg.goals", AgentReadEnvelope.GoalsSchema);
            Assert.Equal(1, AgentReadEnvelope.GoalsSchemaVersion);
            var payload = AssertEnvelope(json.RootElement, AgentReadEnvelope.GoalsSchema,
                AgentReadEnvelope.GoalsSchemaVersion);
            Assert.Equal(new[] { "goals" }, payload.EnumerateObject().Select(property => property.Name));
            var rows = payload.GetProperty("goals").EnumerateArray().ToArray();
            Assert.Equal(seed ? 2 : 0, rows.Length);
            foreach (var row in rows)
            {
                AssertGoalProperties(row);
                Assert.Equal("Draft", row.GetProperty("status").GetString());
                Assert.Equal(JsonValueKind.Null, row.GetProperty("condition").ValueKind);
            }
            if (seed)
            {
                Assert.Equal("abc20000bbbbbbbbbbbbbbbbbbbbbbbb", rows[0].GetProperty("id").GetString());
                Assert.Equal(fullObjective, rows[0].GetProperty("objective").GetString());
                Assert.Equal("abc10000aaaaaaaaaaaaaaaaaaaaaaaa", rows[1].GetProperty("id").GetString());
                Assert.Equal("First goal", rows[1].GetProperty("objective").GetString());
            }
            Assert.Equal(1, repository.ListGoalMetadataCount);
            AssertNoStateEffects(repository);
            Assert.Empty(Directory.GetFiles(root, "*", SearchOption.AllDirectories));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Goals_Conditions_PreservesTextAndNormalizesAbsentValues()
    {
        using var json = JsonDocument.Parse(GoalsAgentReadView.Serialize(
        [
            new GoalSummary("one", "Active", "Full objective", "2026-10-06", Condition: "active-with-failed-task"),
            new GoalSummary("two", "Completed", "Other objective", "2026-10-06", Condition: " \t"),
            new GoalSummary("three", "Draft", "Third objective", "2026-10-06")
        ]));
        var payload = AssertEnvelope(json.RootElement, AgentReadEnvelope.GoalsSchema,
            AgentReadEnvelope.GoalsSchemaVersion);
        Assert.Equal(new[] { "goals" }, payload.EnumerateObject().Select(property => property.Name));
        var rows = payload.GetProperty("goals").EnumerateArray().ToArray();
        Assert.Equal(3, rows.Length);
        foreach (var row in rows) AssertGoalProperties(row);
        Assert.Equal("active-with-failed-task", rows[0].GetProperty("condition").GetString());
        Assert.Equal("Active", rows[0].GetProperty("status").GetString());
        Assert.Equal("Completed", rows[1].GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, rows[1].GetProperty("condition").ValueKind);
        Assert.Equal(JsonValueKind.Null, rows[2].GetProperty("condition").ValueKind);
    }

    [Fact]
    public void Serialize_FixedClock_EmitsExactUtcTimestamp()
    {
        var time = new FixedTimeProvider(new DateTimeOffset(2026, 10, 6, 2, 0, 0, TimeSpan.FromHours(2)));
        using var json = JsonDocument.Parse(AgentReadEnvelope.Serialize(AgentReadEnvelope.GoalsSchema,
            AgentReadEnvelope.GoalsSchemaVersion, new { goals = Array.Empty<object>() },
            new JsonSerializerOptions(JsonSerializerDefaults.Web), time));
        AssertEnvelope(json.RootElement, AgentReadEnvelope.GoalsSchema, AgentReadEnvelope.GoalsSchemaVersion);
        Assert.Equal("2026-10-06T00:00:00.0000000Z", json.RootElement.GetProperty("generatedAt").GetString());
    }

    [Theory]
    [InlineData("goals", "--epic", "epic", "--json")]
    [InlineData("goals", "subscribe")]
    [InlineData("goals", "--json", "extra")]
    [InlineData("goals", "--json", "--json")]
    [InlineData("goals", "--unknown-flag")]
    public void Goals_OtherForms_DoNotEnterBareInspectionRoute(params string[] args) =>
        Assert.False(CliInspectionQueryCommand.IsInspectionQueryCommand(args));

    [Fact]
    public void Goals_Help_ListsJsonForBareForm()
    {
        var output = CaptureConsole(() => Assert.True(CliCommandHelp.TryPrintStartupHelp(["goals", "--help"])));
        Assert.Contains("Usage: goals [--epic <id-or-title>] [--json]", output);
        Assert.Contains("--json emits the mcg.goals agent envelope for the bare form and the --epic <ref> form.", output);
    }

    private static JsonElement AssertEnvelope(JsonElement envelope, string schema, int version)
    {
        Assert.Equal(new[] { "schema", "schemaVersion", "generatedAt", "payload" },
            envelope.EnumerateObject().Select(property => property.Name));
        Assert.Equal(schema, envelope.GetProperty("schema").GetString());
        Assert.Equal(JsonValueKind.Number, envelope.GetProperty("schemaVersion").ValueKind);
        Assert.Equal(version, envelope.GetProperty("schemaVersion").GetInt32());
        var timestamp = envelope.GetProperty("generatedAt").GetString()!;
        var parsed = DateTimeOffset.ParseExact(timestamp, "O", CultureInfo.InvariantCulture);
        Assert.Equal(TimeSpan.Zero, parsed.Offset);
        Assert.EndsWith("Z", timestamp);
        return envelope.GetProperty("payload");
    }

    private static void AssertGoalProperties(JsonElement row) =>
        Assert.Equal(new[] { "id", "status", "condition", "objective" },
            row.EnumerateObject().Select(property => property.Name));

    private static void AssertNoStateEffects(ProbeStateRepository repository) => Assert.Equal(0,
        repository.FullLoadAttempts + repository.LoadGoalsCount + repository.LoadGoalCount +
        repository.SaveAttempts + repository.MergeSaveAttempts + repository.MutationAttempts +
        repository.ListOutboxMessagesCount + repository.OutboxClaimAttempts);

    private static string Run(string[] args, OrchestratorWorkspace workspace, ProbeStateRepository repository)
    {
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var profiles = WorkerProfileCatalog.Default();
        Goal? goal = null;
        var output = CaptureConsole(() =>
        {
            Assert.True(CliReadOnlyCommandRunner.TryExecute(args, repository, workspace,
                new InMemoryModelProviderRegistry([]), null, ref agents, ref profiles, ref goal, out var changed));
            Assert.False(changed);
        });
        Assert.Null(goal);
        return output;
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
