using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: private workspace and input files, injected times, no process or global mutation.
public sealed class OperatorDirectiveIntentTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Record_QueuesOnly_ThenDefaultCoordinatorAppliesAttributedDirective()
    {
        using var fixture = new Harness();
        var intent = await fixture.QueueRecord("  One at a time\r\n", "--expires-at", "2026-10-02T12:00:00Z");
        Assert.Equal(OperatorIntentScopes.Workspace, intent.GoalId);
        Assert.Equal(OperatorIntentVerbs.DirectiveRecord, intent.Verb);
        Assert.Equal("owner", intent.Actor);
        Assert.Equal(OperatorActorKind.Human, intent.ActorKind);
        Assert.False(File.Exists(fixture.Workspace.OperatorDirectivesStorePath));
        OperatorIntentCoordinator.CreateDefault(fixture.Workspace).ExecuteWorkspacePending(new AgentOrchestratorKernel());
        Assert.Equal(OperatorIntentStatus.Applied, (await fixture.Intents.GetAsync(intent.Id))!.Status);
        var row = Assert.Single(fixture.Store.List(At));
        Assert.Matches("^[0-9a-f]{32}$", row.Id);
        Assert.NotEqual(intent.Id, row.Id);
        Assert.Equal("intake", row.Kind);
        Assert.Equal("  One at a time", row.Text);
        Assert.Equal(intent.Actor, row.Actor);
        Assert.Equal(intent.ActorKind, row.ActorKind);
        Assert.Equal(intent.Channel, row.Channel);
        Assert.Equal(At, row.RecordedAt);
        Assert.Equal(At.AddDays(1), row.ExpiresAt);
        Assert.False(row.Expired);
    }

    [Fact]
    public async Task HandoffAndRetirement_UseSubmissionTimes_AndUniquePrefix()
    {
        using var fixture = new Harness();
        await fixture.QueueRecord("Pace work", "--until-handoff");
        fixture.Tick();
        var row = Assert.Single(fixture.Store.List(At));
        Assert.False(row.Expired);
        var handoff = await fixture.Queue(["directive", "handoff", "--note", "new owner"], At.AddMinutes(1));
        Assert.Equal(OperatorIntentVerbs.DirectiveHandoff, handoff.Verb);
        fixture.Tick();
        Assert.Equal(OperatorIntentStatus.Applied, (await fixture.Intents.GetAsync(handoff.Id))!.Status);
        Assert.True(Assert.Single(fixture.Store.List(At)).Expired);
        var retire = await fixture.Queue(["directive", "retire", row.Id[..8], "--reason", "replaced"], At.AddMinutes(2));
        Assert.Equal(OperatorIntentVerbs.DirectiveRetire, retire.Verb);
        fixture.Tick();
        Assert.Equal(OperatorIntentStatus.Applied, (await fixture.Intents.GetAsync(retire.Id))!.Status);
        Assert.Empty(fixture.Store.List(At));
        var retired = Assert.Single(fixture.Store.List(At, true));
        Assert.Equal("replaced", retired.RetireReason);
        Assert.Equal(retire.Actor, retired.RetiredBy);
        Assert.Equal(retire.CreatedAt, retired.RetiredAt);
        var second = await fixture.Queue(["directive", "retire", row.Id, "--reason", "again"]);
        fixture.Tick();
        await fixture.Rejected(second, "directive-already-retired");
        Assert.False(fixture.Store.HasRetirementSource(second.Id));
    }

    [Theory]
    [InlineData("urgent", "Text", null, "directive-kind-unsupported")]
    [InlineData("intake", " \r\n", null, "directive-text-empty")]
    [InlineData("intake", "Text", "2026-10-02T12:00:00+02:00", "directive-expires-at-invalid")]
    [InlineData("intake", "Text", "2026-10-02T12:00:00", "directive-expires-at-invalid")]
    [InlineData("intake", "Text", "invalid", "directive-expires-at-invalid")]
    public async Task CliSemanticInvalidity_QueuesThenRejectsWithoutDomainRows(
        string kind, string text, string? expiry, string reason)
    {
        using var fixture = new Harness();
        var path = fixture.TextFile(text);
        var args = new List<string> { "directive", "record", "--kind", kind, "--text-file", path };
        if (expiry is not null) args.AddRange(["--expires-at", expiry]);
        var intent = await fixture.Queue(args);
        Assert.Equal(OperatorIntentStatus.Pending, intent.Status);
        fixture.Tick();
        await fixture.Rejected(intent, reason);
        Assert.Empty(fixture.Store.List(At, true));
        Assert.False(File.Exists(fixture.Workspace.OperatorDirectivesStorePath));
    }

    [Fact]
    public async Task ExpiryConflict_CliRefusesStructure_HandlerRejectsDirectPayload()
    {
        using var fixture = new Harness();
        using var output = new StringWriter();
        Assert.Equal(1, CliDirectiveCommands.Run(["directive", "record", "--kind", "intake", "--text-file",
            fixture.TextFile("Text"), "--expires-at", "2026-10-02T12:00:00Z", "--until-handoff"], fixture.Workspace, output));
        Assert.Empty(await fixture.Intents.ListActionableGoalIdsAsync());
        var intent = await fixture.Enqueue(OperatorIntentVerbs.DirectiveRecord,
            JsonSerializer.Serialize(new DirectiveRecordOperatorIntentPayload("intake", "Text", "2026-10-02T12:00:00Z", true), OperatorIntentJson.Options));
        fixture.Tick();
        await fixture.Rejected(intent, "directive-expiry-conflict");
        Assert.False(File.Exists(fixture.Workspace.OperatorDirectivesStorePath));
    }

    [Fact]
    public async Task InvalidPayloadAndSteward_AreRejectedWithoutWrites()
    {
        using var fixture = new Harness();
        var malformed = await fixture.Enqueue(OperatorIntentVerbs.DirectiveRecord, "{");
        var steward = await fixture.Enqueue(OperatorIntentVerbs.DirectiveHandoff, "{}", OperatorIntentAdjudication.StewardAssurance);
        fixture.Tick();
        await fixture.Rejected(malformed, "outcome-payload-invalid");
        await fixture.Rejected(steward, "steward-capability-boundary");
        Assert.False(File.Exists(fixture.Workspace.OperatorDirectivesStorePath));
    }

    [Theory]
    [InlineData("ffffffff", "directive-not-found")]
    [InlineData("aaaa", "directive-id-prefix-too-short")]
    [InlineData("aaaaaaaa", "directive-id-ambiguous")]
    public async Task RetirementPrefixes_RejectPreciselyAndPreserveRows(string prefix, string reason)
    {
        using var fixture = new Harness();
        foreach (var id in new[] { "aaaaaaaa111111111111111111111111", "aaaaaaaa222222222222222222222222" })
            fixture.Store.TryAppendDirective(new(id, "scope", "Text", "owner", OperatorActorKind.Human,
                "cli", At, null, false, null, null, null, false), id);
        var intent = await fixture.Queue(["directive", "retire", prefix, "--reason", "obsolete"]);
        fixture.Tick();
        await fixture.Rejected(intent, reason);
        Assert.Equal(2, fixture.Store.List(At).Count);
        Assert.False(fixture.Store.HasRetirementSource(intent.Id));
    }

    [Fact]
    public async Task Replay_RecoversEachVerbFromDomainSourceWithoutReapplying()
    {
        using var fixture = new Harness();
        var record = await fixture.QueueRecord("Text", "--until-handoff");
        fixture.Tick();
        var row = Assert.Single(fixture.Store.List(At));
        var handoff = await fixture.Queue(["directive", "handoff"], At.AddMinutes(1));
        fixture.Tick();
        var retire = await fixture.Queue(["directive", "retire", row.Id, "--reason", "obsolete"], At.AddMinutes(2));
        fixture.Tick();
        var before = File.ReadAllBytes(fixture.Workspace.OperatorDirectivesStorePath);
        // Remove completion storage only; replay must recover from the independent event tables.
        File.Delete(Path.Combine(fixture.Workspace.OrchestratorDirectory, SqliteOperatorIntentStore.DatabaseFileName));
        foreach (var intent in new[] { record, handoff, retire })
            await fixture.Intents.EnqueueAsync(intent);
        var lines = fixture.Tick();
        Assert.Equal(3, lines.Count);
        Assert.All(lines, line => Assert.Contains("result=applied-recovered", line));
        foreach (var intent in new[] { record, handoff, retire })
            Assert.Equal(OperatorIntentStatus.Applied, (await fixture.Intents.GetAsync(intent.Id))!.Status);
        Assert.Equal(before, File.ReadAllBytes(fixture.Workspace.OperatorDirectivesStorePath));
        Assert.Equal(row.Id, Assert.Single(fixture.Store.List(At, true)).Id);
    }

    [Fact]
    public async Task Submission_IdempotencyKey_ReturnsSameIntentAndEnvelope()
    {
        using var fixture = new Harness();
        var first = await fixture.Queue(["directive", "handoff", "--idempotency-key", "same-key"]);
        var second = await fixture.Queue(["directive", "handoff", "--idempotency-key", "same-key"]);
        Assert.Equal(first.Id, second.Id);
        fixture.Tick();
        Assert.True(fixture.Store.HasHandoffSource(first.Id));
    }

    private sealed class Harness : IDisposable
    {
        private readonly ConductorVerbStartTests.Fixture fixture = new();
        internal OrchestratorWorkspace Workspace => fixture.Workspace;
        internal SqliteOperatorIntentStore Intents => SqliteOperatorIntentStore.ForDirectories(Workspace.OrchestratorDirectory, Workspace.LogDirectory);
        internal SqliteOperatorDirectiveStore Store => new(Workspace.OperatorDirectivesStorePath);

        internal string TextFile(string text)
        {
            var path = Path.Combine(Workspace.RootDirectory, Guid.NewGuid().ToString("N") + ".txt");
            File.WriteAllText(path, text);
            return path;
        }

        internal Task<OperatorIntentRecord> QueueRecord(string text, params string[] expiry) => Queue(
            new[] { "directive", "record", "--kind", "intake", "--text-file", TextFile(text) }.Concat(expiry).ToArray());

        internal async Task<OperatorIntentRecord> Queue(IReadOnlyList<string> args, DateTimeOffset? at = null)
        {
            using var output = new StringWriter();
            Assert.Equal(0, CliDirectiveCommands.Run(args.Concat(["--operator-actor", "owner", "--actor-kind", "human", "--json"]).ToArray(),
                Workspace, output, new FixedTime(at ?? At)));
            using var json = JsonDocument.Parse(output.ToString());
            Assert.Equal("mcg.operator-intent", json.RootElement.GetProperty("schema").GetString());
            var id = json.RootElement.GetProperty("payload").GetProperty("id").GetString()!;
            return (await Intents.GetAsync(id))!;
        }

        internal Task<OperatorIntentRecord> Enqueue(string verb, string json, string assurance = "local-process")
        {
            var id = Guid.NewGuid().ToString("N");
            return Intents.EnqueueAsync(new(id, id, verb, OperatorIntentScopes.Workspace, null,
                json, [], "owner", "cli", assurance, At, ActorKind: OperatorActorKind.Human));
        }

        internal IReadOnlyList<string> Tick() => new OperatorIntentCoordinator(Intents, utcNow: () => At.AddDays(3))
        {
            Directives = new OperatorDirectiveIntentHandler(Workspace.OperatorDirectivesStorePath)
        }.ExecuteWorkspacePending(new AgentOrchestratorKernel());

        internal async Task Rejected(OperatorIntentRecord intent, string reason)
        {
            var result = (await Intents.GetAsync(intent.Id))!;
            Assert.Equal(OperatorIntentStatus.Rejected, result.Status);
            Assert.Equal($"Rejected {intent.Verb}: {reason}", result.Outcome);
        }

        public void Dispose() => fixture.Dispose();
    }

    private sealed class FixedTime(DateTimeOffset at) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => at;
    }
}
