using System.Security.Cryptography;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class OperatorLessonRecordIntentTests
{
    [Theory]
    [InlineData(null, "lesson-evidence-required")]
    [InlineData("operator-evidence:missing.txt", "evidence-reference-unresolved")]
    [InlineData("operator-evidence:evidence.txt:4", "evidence-reference-unresolved")]
    [InlineData("focused-evidence:receipt", "evidence-reference-requires-goal")]
    [InlineData("acceptance-attempt:attempt", "evidence-reference-requires-goal")]
    [InlineData("free text", "evidence-reference-free-text-refused")]
    [InlineData("id=hash", "evidence-reference-free-text-refused")]
    public async Task RecordRejectsUnsupportedEvidenceWithoutChangingStore(string? reference, string reason)
    {
        using var fixture = new OperatorLessonHarness();
        File.WriteAllText(Path.Combine(fixture.Root, "evidence.txt"), "one\ntwo\nthree\n");
        var intent = await fixture.Record(reference is null ? [] : [reference]);

        fixture.Tick();

        var outcome = await fixture.IntentStore.GetAsync(intent.Id);
        Assert.Equal(OperatorIntentStatus.Rejected, outcome!.Status);
        Assert.Contains(reason, outcome.Outcome, StringComparison.Ordinal);
        Assert.Contains(reference ?? "<none>", outcome.Outcome, StringComparison.Ordinal);
        Assert.Empty(fixture.LessonStore.List(includeRetired: true));
    }

    [Fact]
    public async Task RecordAcceptsValidLineAndHashesWholeFile()
    {
        using var fixture = new OperatorLessonHarness();
        var path = Path.Combine(fixture.Root, "evidence.txt");
        File.WriteAllText(path, "one\ntwo\nthree\n");
        var intent = await fixture.Record(["operator-evidence:evidence.txt:2"]);

        fixture.Tick();

        Assert.Equal(OperatorIntentStatus.Applied, (await fixture.IntentStore.GetAsync(intent.Id))!.Status);
        var lesson = Assert.Single(fixture.LessonStore.List());
        Assert.Equal(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant(),
            Assert.Single(lesson.Evidence).ContentHash);
    }

    [Fact]
    public async Task FailedGoalLookupRejectsClaimAndDoesNotBlockLaterWorkspaceIntent()
    {
        using var fixture = new OperatorLessonHarness();
        File.WriteAllText(Path.Combine(fixture.Root, "proof.txt"), "verified proof\n");
        var invalid = await fixture.Record(["focused-evidence:receipt"], goalId: Guid.NewGuid().ToString("N"));
        var valid = await fixture.Record(["operator-evidence:proof.txt"]);
        var coordinator = new OperatorIntentCoordinator(fixture.IntentStore, utcNow: () => fixture.Now)
        {
            Lessons = new OperatorLessonIntentServices(fixture.LessonStore,
                new AdjudicationEvidenceResolver(fixture.Root), _ => throw new IOException("goal lookup failed"))
        };

        coordinator.ExecuteWorkspacePending(new AgentOrchestratorKernel());

        var rejected = await fixture.IntentStore.GetAsync(invalid.Id);
        Assert.Equal(OperatorIntentStatus.Rejected, rejected!.Status);
        Assert.Contains("goal lookup failed", rejected.Outcome, StringComparison.Ordinal);
        Assert.Equal(OperatorIntentStatus.Applied, (await fixture.IntentStore.GetAsync(valid.Id))!.Status);
        Assert.Equal(valid.Id, Assert.Single(fixture.LessonStore.List()).Id);
    }
}

internal sealed class OperatorLessonHarness : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "mcg-lessons-" + Guid.NewGuid().ToString("N"));
    public DateTimeOffset Now { get; set; } = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);
    public SqliteOperatorIntentStore IntentStore { get; }
    public SqliteOperatorLessonStore LessonStore { get; }
    public OperatorIntentCoordinator Coordinator { get; }
    public OrchestratorWorkspace Workspace { get; }

    public OperatorLessonHarness()
    {
        Directory.CreateDirectory(Root);
        Workspace = OrchestratorWorkspace.ForDirectory(Root);
        IntentStore = SqliteOperatorIntentStore.ForDirectories(Workspace.OrchestratorDirectory, Workspace.LogDirectory);
        LessonStore = new SqliteOperatorLessonStore(Workspace.OperatorLessonsStorePath);
        Coordinator = new OperatorIntentCoordinator(IntentStore, utcNow: () => Now)
        {
            Lessons = new OperatorLessonIntentServices(LessonStore, new AdjudicationEvidenceResolver(Root), _ => null)
        };
    }

    public async Task<OperatorIntentRecord> Record(IReadOnlyList<string> evidence,
        IReadOnlyList<string>? tags = null, string? goalId = null, OperatorActorKind kind = OperatorActorKind.Human)
    {
        var payload = new LessonRecordOperatorIntentPayload("When fixture fails", "Inspect the exact receipt", evidence,
            tags ?? ["tests"], goalId, Root);
        return await Enqueue(OperatorIntentVerbs.LessonRecord, payload, kind);
    }

    public Task<OperatorIntentRecord> Retire(string lessonId, string reason = "Superseded") =>
        Enqueue(OperatorIntentVerbs.LessonRetire,
            new LessonRetireOperatorIntentPayload(lessonId, reason, [], Root));

    private Task<OperatorIntentRecord> Enqueue(string verb, object payload,
        OperatorActorKind kind = OperatorActorKind.Human)
    {
        var id = Guid.NewGuid().ToString("N");
        return IntentStore.EnqueueAsync(new OperatorIntentRecord(id, id, verb, OperatorIntentScopes.Workspace,
            null, JsonSerializer.Serialize(payload, payload.GetType(), OperatorIntentJson.Options), [],
            "tester", "cli", "local-process", Now, ActorKind: kind));
    }

    public IReadOnlyList<string> Tick() => Coordinator.ExecuteWorkspacePending(new AgentOrchestratorKernel());

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { }
    }
}
