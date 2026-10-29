using System.Globalization;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: private SQLite roots, injected clocks and async-local console capture.
public sealed class CliOperatorIntentAgentReadContractTests : CliTaskQueryTestSupport
{
    private static readonly DateTimeOffset Start = new(2026, 10, 6, 0, 0, 0, TimeSpan.Zero);
    private const string GoalId = "123456789abcdef0123456789abcdef0";
    private const string TaskId = "abcdef0123456789abcdef0123456789";

    [Fact]
    public async Task Status_SeededIntents_PinsEnvelopePayloadAndHumanText()
    {
        using var fixture = new IntentFixture(CreateTempDirectory());
        var applied = await fixture.Store.EnqueueAsync(Record("applied", null));
        Assert.NotNull(await fixture.Store.ClaimNextAsync(GoalId, "fixture"));
        await fixture.Store.CompleteAsync(applied.Id, "fixture", OperatorIntentStatus.Applied,
            "applied outcome", Start.AddMinutes(1));
        var pending = await fixture.Store.EnqueueAsync(Record("pending", TaskId));

        Assert.Equal("mcg.operator-intent", AgentReadEnvelope.OperatorIntentSchema);
        Assert.Equal(1, AgentReadEnvelope.OperatorIntentSchemaVersion);
        foreach (var seed in new[] { applied, pending })
        {
            var persisted = (await fixture.Store.GetAsync(seed.Id))!;
            string[] args = ["operator-intent-status", seed.Id, "--json"];
            CliCommandHelp.ThrowIfInvalidFlags(args);
            var (output, error, code) = RunRoute(args, fixture.Workspace);
            Assert.Equal(0, code);
            Assert.Empty(error);
            using var json = JsonDocument.Parse(output);
            var envelope = json.RootElement;
            Assert.Equal(new[] { "schema", "schemaVersion", "generatedAt", "payload" },
                envelope.EnumerateObject().Select(property => property.Name));
            Assert.Equal("mcg.operator-intent", envelope.GetProperty("schema").GetString());
            Assert.Equal(1, envelope.GetProperty("schemaVersion").GetInt32());
            AssertUtc(envelope.GetProperty("generatedAt"));
            var payload = envelope.GetProperty("payload");
            Assert.Equal(new[] { "id", "idempotencyKey", "verb", "goalId", "taskId", "status",
                "actor", "actorKind", "channel", "createdAt", "claimedAt", "completedAt", "outcome" },
                payload.EnumerateObject().Select(property => property.Name));
            Assert.Equal(persisted.Id, payload.GetProperty("id").GetString());
            Assert.Equal(persisted.IdempotencyKey, payload.GetProperty("idempotencyKey").GetString());
            Assert.Equal("retry", payload.GetProperty("verb").GetString());
            Assert.Equal(GoalId, payload.GetProperty("goalId").GetString());
            Assert.Equal("operator", payload.GetProperty("actor").GetString());
            Assert.Equal("Human", payload.GetProperty("actorKind").GetString());
            Assert.Equal("cli", payload.GetProperty("channel").GetString());
            Assert.Equal(Start, AssertUtc(payload.GetProperty("createdAt")));
            if (seed == applied)
            {
                Assert.Equal("Applied", payload.GetProperty("status").GetString());
                Assert.Equal(JsonValueKind.Null, payload.GetProperty("taskId").ValueKind);
                AssertUtc(payload.GetProperty("claimedAt"));
                Assert.Equal(Start.AddMinutes(1), AssertUtc(payload.GetProperty("completedAt")));
                Assert.Equal("applied outcome", payload.GetProperty("outcome").GetString());
            }
            else
            {
                Assert.Equal("Pending", payload.GetProperty("status").GetString());
                Assert.Equal(TaskId, payload.GetProperty("taskId").GetString());
                foreach (var name in new[] { "claimedAt", "completedAt", "outcome" })
                    Assert.Equal(JsonValueKind.Null, payload.GetProperty(name).ValueKind);
            }

            var text = RunRoute(["operator-intent-status", seed.Id], fixture.Workspace);
            Assert.Equal(0, text.Code);
            Assert.Empty(text.Error);
            Assert.Equal($"Operator intent {persisted.Id}: verb=retry goal=12345678 " +
                $"task={(persisted.TaskId is null ? "none" : "abcdef01")} status={persisted.Status} " +
                $"actor=operator channel=cli auth=local outcome={persisted.Outcome ?? "pending"}{Environment.NewLine}",
                text.Output);
        }
    }

    [Fact]
    public async Task Status_LegacyDiscordIntent_ReadsListsAndRendersStoredAttribution()
    {
        using var fixture = new IntentFixture(CreateTempDirectory());
        var seed = await fixture.Store.EnqueueAsync(Record("legacy", TaskId) with
        {
            Channel = "discord",
            AuthenticationAssurance = "discord-operator-allowlist"
        });

        var persisted = Assert.IsType<OperatorIntentRecord>(await fixture.Store.GetAsync(seed.Id));
        Assert.Equal("discord", persisted.Channel);
        Assert.Equal("discord-operator-allowlist", persisted.AuthenticationAssurance);
        var listed = Assert.Single(await fixture.Store.ListForGoalAsync(GoalId));
        Assert.Equal(persisted.Id, listed.Id);
        Assert.Equal(persisted.Channel, listed.Channel);
        Assert.Equal(persisted.AuthenticationAssurance, listed.AuthenticationAssurance);

        var text = RunRoute(["operator-intent-status", seed.Id], fixture.Workspace);
        Assert.Equal(0, text.Code);
        Assert.Empty(text.Error);
        Assert.Equal($"Operator intent {seed.Id}: verb=retry goal=12345678 " +
            $"task=abcdef01 status=Pending actor=operator channel=discord " +
            $"auth=discord-operator-allowlist outcome=pending{Environment.NewLine}", text.Output);

        var json = RunRoute(["operator-intent-status", seed.Id, "--json"], fixture.Workspace);
        Assert.Equal(0, json.Code);
        Assert.Empty(json.Error);
        using var document = JsonDocument.Parse(json.Output);
        var payload = document.RootElement.GetProperty("payload");
        Assert.Equal(seed.Id, payload.GetProperty("id").GetString());
        Assert.Equal("discord", payload.GetProperty("channel").GetString());
        var afterRead = Assert.IsType<OperatorIntentRecord>(await fixture.Store.GetAsync(seed.Id));
        Assert.Equal(persisted.Channel, afterRead.Channel);
        Assert.Equal(persisted.AuthenticationAssurance, afterRead.AuthenticationAssurance);
    }

    [Theory]
    [InlineData(OperatorIntentStatus.Applied, 0)]
    [InlineData(OperatorIntentStatus.Rejected, 2)]
    [InlineData(OperatorIntentStatus.Pending, 3)]
    public async Task Status_JsonWait_EmitsOnlyFinalObservationAndKeepsExitCode(
        OperatorIntentStatus finalStatus, int expectedCode)
    {
        using var fixture = new IntentFixture(CreateTempDirectory());
        var intent = await fixture.Store.EnqueueAsync(Record("waiting", TaskId));
        var clock = new ManualClock();
        using var output = new StringWriter();
        var delays = 0;
        var code = CliCriterionEvidenceIntents.PrintStatus(
            ["operator-intent-status", intent.Id, "--json", "--wait", "1"], fixture.Store,
            output, clock, TimeSpan.FromSeconds(1), duration =>
            {
                Assert.Empty(output.ToString());
                Assert.Equal(TimeSpan.FromSeconds(1), duration);
                delays++;
                if (finalStatus != OperatorIntentStatus.Pending)
                {
                    Assert.NotNull(fixture.Store.ClaimNextAsync(GoalId, "fixture").GetAwaiter().GetResult());
                    fixture.Store.CompleteAsync(intent.Id, "fixture", finalStatus, "final outcome", Start)
                        .GetAwaiter().GetResult();
                }
                clock.Advance(duration);
            });
        Assert.Equal(1, delays);
        Assert.Equal(expectedCode, code);
        using var json = JsonDocument.Parse(output.ToString());
        Assert.Equal(finalStatus.ToString(), json.RootElement.GetProperty("payload").GetProperty("status").GetString());
        Assert.Equal(Start.AddSeconds(1), AssertUtc(json.RootElement.GetProperty("generatedAt")));
    }

    [Theory]
    [InlineData("operator-intent-status --json")]
    [InlineData("operator-intent-status a b --json")]
    [InlineData("operator-intent-status a --json --json")]
    [InlineData("operator-intent-status a --json --unknown")]
    [InlineData("operator-intent-status a --json=true")]
    public void Status_InvalidJsonArguments_ThrowsUsageWithoutOutput(string command)
    {
        using var fixture = new IntentFixture(CreateTempDirectory());
        using var output = new StringWriter();
        var error = Assert.Throws<ArgumentException>(() => CliCriterionEvidenceIntents.PrintStatus(
            command.Split(' '), fixture.Store, output, new ManualClock(), TimeSpan.FromSeconds(1),
            _ => throw new InvalidOperationException("Invalid arguments must not wait.")));
        Assert.Equal(CliCommandHelp.OperatorIntentStatusUsage, error.Message);
        Assert.Empty(output.ToString());
    }

    [Fact]
    public void Status_MissingJsonIntent_FailsWithoutOutput()
    {
        using var fixture = new IntentFixture(CreateTempDirectory());
        using var output = new StringWriter();
        var error = Assert.Throws<KeyNotFoundException>(() => CliCriterionEvidenceIntents.PrintStatus(
            ["operator-intent-status", "missing", "--json"], fixture.Store, output,
            new ManualClock(), TimeSpan.FromSeconds(1), _ => throw new InvalidOperationException()));
        Assert.Equal("Operator intent 'missing' was not found.", error.Message);
        Assert.Empty(output.ToString());
        var routed = RunRoute(["operator-intent-status", "missing", "--json"], fixture.Workspace);
        Assert.Equal(1, routed.Code);
        Assert.Empty(routed.Output);
        Assert.Contains("Operator intent 'missing' was not found.", routed.Error);
    }

    private static OperatorIntentRecord Record(string label, string? taskId) => new(
        label == "applied" ? "11111111111111111111111111111111" : "22222222222222222222222222222222",
        $"key-{label}", "retry", GoalId, taskId, "{\"private\":\"body\"}", ["private-file"],
        "operator", "cli", "local", Start);

    private static DateTimeOffset AssertUtc(JsonElement value)
    {
        Assert.Equal(JsonValueKind.String, value.ValueKind);
        Assert.EndsWith("Z", value.GetString());
        return DateTimeOffset.Parse(value.GetString()!, CultureInfo.InvariantCulture);
    }

    private static (string Output, string Error, int Code) RunRoute(
        IReadOnlyList<string> args, OrchestratorWorkspace workspace)
    {
        var code = -1;
        var error = "";
        var output = CaptureConsole(() => error = AsyncLocalConsoleRouter.Error.CaptureLocal(
            () => code = CliOperatorIntentRoute.Run(args, workspace)));
        return (output, error, code);
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = Start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }

    private sealed class IntentFixture : IDisposable
    {
        private readonly string _root;
        public OrchestratorWorkspace Workspace { get; }
        public SqliteOperatorIntentStore Store { get; }
        public IntentFixture(string root)
        {
            _root = root;
            Workspace = OrchestratorWorkspace.ForDirectory(root);
            Store = SqliteOperatorIntentStore.ForDirectories(Workspace.OrchestratorDirectory, Workspace.LogDirectory);
        }
        public void Dispose() => Directory.Delete(_root, true);
    }
}
