using System.Security.Cryptography;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each test owns its workspace, SQLite files and evidence files; clocks are fixed.
public sealed class OperatorEscapeRecordIntentTests
{
    [Fact(DisplayName = "The tick resolves full goal ids and evidence and recovers the same intent once")]
    public async Task ApplyPersistsRecordAndReplayIsIdempotent()
    {
        using var fixture = new EscapeHarness();
        var intent = await fixture.Enqueue(foundBy: "bbbb", kind: OperatorActorKind.Agent);
        fixture.Tick();

        Assert.Equal(OperatorIntentStatus.Applied, (await fixture.Intents.GetAsync(intent.Id))!.Status);
        var record = Assert.Single(fixture.Store.List());
        Assert.Equal(EscapeHarness.LandedId, record.GoalId);
        Assert.Equal(EscapeHarness.FoundId, record.FoundByGoalId);
        Assert.Equal("Feature never worked", record.Reason);
        Assert.Equal("tester", record.Actor);
        Assert.Equal(OperatorActorKind.Agent, record.ActorKind);
        Assert.Equal("cli", record.Channel);
        Assert.Equal(fixture.Now, record.RecordedAt);
        var evidence = Assert.Single(record.Evidence);
        Assert.Equal("operator-evidence:" + fixture.Proof, evidence.ReceiptId);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(fixture.Proof))).ToLowerInvariant(),
            evidence.ContentHash);

        // Lose the intent completion receipt to exercise durable-store replay, not queue dedupe.
        File.Delete(Path.Combine(fixture.Workspace.OrchestratorDirectory, SqliteOperatorIntentStore.DatabaseFileName));
        await fixture.Intents.EnqueueAsync(intent);
        fixture.Tick();

        var recovered = await fixture.Intents.GetAsync(intent.Id);
        Assert.Equal(OperatorIntentStatus.Applied, recovered!.Status);
        Assert.Contains("recovered", recovered.Outcome, StringComparison.Ordinal);
        var replayed = Assert.Single(fixture.Store.List());
        Assert.Equal(record.Id, replayed.Id);
        Assert.Equal(record.RecordedAt, replayed.RecordedAt);
        Assert.Equal(record.Evidence.ToArray(), replayed.Evidence.ToArray());
    }

    [Theory(DisplayName = "Invalid goal selection and missing evidence have distinct rejection causes")]
    [InlineData("missing", null, true, "escape-goal-not-found")]
    [InlineData("aaaa", null, true, "escape-goal-ambiguous")]
    [InlineData("bbbb", null, true, "escape-goal-not-landed")]
    [InlineData("aaaaaaaa1", null, false, "escape-evidence-required")]
    [InlineData("aaaaaaaa1", "missing", true, "escape-found-by-goal-not-found")]
    [InlineData("aaaaaaaa1", "aaaa", true, "escape-found-by-goal-ambiguous")]
    public async Task RejectsInvalidRecord(string goal, string? foundBy, bool evidence, string cause)
    {
        using var fixture = new EscapeHarness();
        var intent = await fixture.Enqueue(goal, foundBy, evidence: evidence);
        fixture.Tick();

        var outcome = await fixture.Intents.GetAsync(intent.Id);
        Assert.Equal(OperatorIntentStatus.Rejected, outcome!.Status);
        Assert.Contains(cause, outcome.Outcome, StringComparison.Ordinal);
        Assert.Empty(fixture.Store.List());
        Assert.False(File.Exists(fixture.Workspace.OperatorEscapesStorePath));
    }

    [Fact(DisplayName = "Rejected evidence does not prevent a later valid workspace intent")]
    public async Task EvidenceFailureDoesNotBlockNextIntent()
    {
        using var fixture = new EscapeHarness();
        var invalid = await fixture.Enqueue(reference: "operator-evidence:missing.txt");
        var valid = await fixture.Enqueue();
        fixture.Tick();

        var outcome = await fixture.Intents.GetAsync(invalid.Id);
        Assert.Equal(OperatorIntentStatus.Rejected, outcome!.Status);
        Assert.Contains("evidence-reference-unresolved", outcome.Outcome, StringComparison.Ordinal);
        Assert.Equal(OperatorIntentStatus.Applied, (await fixture.Intents.GetAsync(valid.Id))!.Status);
        Assert.Equal(valid.Id, Assert.Single(fixture.Store.List()).Id);
    }

    [Fact(DisplayName = "The store deduplicates source intents and rejects an id owned by another intent")]
    public void StoreDeduplicatesAndRejectsConflictingId()
    {
        using var fixture = new EscapeHarness();
        var record = new OperatorEscape("record", EscapeHarness.LandedId, "Defect", [], null,
            "tester", OperatorActorKind.Human, "cli", fixture.Now);
        Assert.Empty(fixture.Store.List());
        Assert.False(fixture.Store.HasRecordSource("intent"));
        Assert.False(File.Exists(fixture.Workspace.OperatorEscapesStorePath));
        Assert.True(fixture.Store.TryAppendEscape(record, "intent"));
        Assert.False(fixture.Store.TryAppendEscape(record, "intent"));
        Assert.Single(fixture.Store.List());
        Assert.Throws<InvalidOperationException>(() => fixture.Store.TryAppendEscape(record, "another"));
    }

    [Fact(DisplayName = "The escape CLI only queues prefixes and attribution without opening state or escape stores")]
    public async Task CliOnlyQueuesIntent()
    {
        using var fixture = new EscapeHarness();
        using var output = new StringWriter();
        Assert.False(File.Exists(fixture.Workspace.SqliteStatePath));
        Assert.False(File.Exists(fixture.Workspace.OperatorEscapesStorePath));
        Assert.Equal(0, CliEscapeCommands.Run(["escape", "record", "--goal", "aaaaaaaa1",
            "--reason", "Feature never worked", "--evidence", "operator-evidence:" + fixture.Proof,
            "--evidence", "operator-evidence:" + fixture.Proof, "--found-by-goal", "bbbb",
            "--actor-kind", "agent", "--operator-actor", "tester"], fixture.Workspace, output));

        var queued = (await fixture.Intents.ClaimNextAsync(OperatorIntentScopes.Workspace, "test"))!;
        Assert.NotNull(queued);
        Assert.Equal(OperatorIntentVerbs.EscapeRecord, queued.Verb);
        Assert.Equal(OperatorActorKind.Agent, queued.ActorKind);
        var payload = JsonSerializer.Deserialize<EscapeRecordOperatorIntentPayload>(queued.PayloadJson,
            OperatorIntentJson.Options)!;
        Assert.Equal("aaaaaaaa1", payload.GoalPrefix);
        Assert.Equal("bbbb", payload.FoundByGoalPrefix);
        Assert.Equal(2, payload.EvidenceReferences.Count);
        Assert.Contains("Operator intent queued:", output.ToString(), StringComparison.Ordinal);
        Assert.False(File.Exists(fixture.Workspace.SqliteStatePath));
        Assert.False(File.Exists(fixture.Workspace.OperatorEscapesStorePath));
    }

    [Fact(DisplayName = "Landing recognition requires a matching GoalLanded event and ignores malformed lines")]
    public void LandingEvidenceBelongsToResolvedGoal()
    {
        using var fixture = new EscapeHarness();
        var directory = fixture.Workspace.GoalLifecycleEventsDirectory;
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, EscapeHarness.LandedId + ".jsonl");
        File.WriteAllText(path, "malformed\n" + JsonSerializer.Serialize(new
        {
            eventType = "GoalLanded", goalId = EscapeHarness.FoundId
        }) + "\n");
        Assert.False(OperatorIntentCoordinator.HasGoalLandedEvent(directory, EscapeHarness.LandedId));
        File.AppendAllText(path, JsonSerializer.Serialize(new
        {
            eventType = "GoalLanded", goalId = EscapeHarness.LandedId
        }) + "\n");
        Assert.True(OperatorIntentCoordinator.HasGoalLandedEvent(directory, EscapeHarness.LandedId));
    }

    private sealed class EscapeHarness : IDisposable
    {
        internal const string LandedId = "aaaaaaaa111111111111111111111111";
        internal const string OtherId = "aaaaaaaa222222222222222222222222";
        internal const string FoundId = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        internal DateTimeOffset Now { get; } = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        internal OrchestratorWorkspace Workspace { get; }
        internal SqliteOperatorIntentStore Intents { get; }
        internal SqliteOperatorEscapeStore Store { get; }
        internal string Proof { get; }

        internal EscapeHarness()
        {
            Workspace = OrchestratorWorkspace.ForDirectory(CreateTempDirectory());
            Intents = SqliteOperatorIntentStore.ForDirectories(Workspace.OrchestratorDirectory, Workspace.LogDirectory);
            Store = new SqliteOperatorEscapeStore(Workspace.OperatorEscapesStorePath);
            Proof = Path.Combine(Workspace.RootDirectory, "proof.txt");
            File.WriteAllText(Proof, "verified proof\n");
        }

        internal Task<OperatorIntentRecord> Enqueue(string goal = "aaaaaaaa1", string? foundBy = null,
            OperatorActorKind kind = OperatorActorKind.Human, bool evidence = true, string? reference = null)
        {
            var id = Guid.NewGuid().ToString("N");
            var payload = new EscapeRecordOperatorIntentPayload(goal, "Feature never worked",
                evidence ? [reference ?? "operator-evidence:" + Proof] : [], foundBy, Workspace.RootDirectory);
            return Intents.EnqueueAsync(new OperatorIntentRecord(id, id, OperatorIntentVerbs.EscapeRecord,
                OperatorIntentScopes.Workspace, null, JsonSerializer.Serialize(payload, OperatorIntentJson.Options),
                [], "tester", "cli", "local-process", Now, ActorKind: kind));
        }

        internal IReadOnlyList<string> Tick() => new OperatorIntentCoordinator(Intents, utcNow: () => Now)
        {
            Lessons = new OperatorLessonIntentServices(new SqliteOperatorLessonStore(Workspace.OperatorLessonsStorePath),
                new AdjudicationEvidenceResolver(Workspace.OrchestratorDirectory), _ => null),
            Escapes = new OperatorEscapeIntentServices(Store, () => [LandedId, OtherId, FoundId], id => id == LandedId)
        }.ExecuteWorkspacePending(new AgentOrchestratorKernel());

        public void Dispose() => Directory.Delete(Workspace.RootDirectory, recursive: true);
    }
}
