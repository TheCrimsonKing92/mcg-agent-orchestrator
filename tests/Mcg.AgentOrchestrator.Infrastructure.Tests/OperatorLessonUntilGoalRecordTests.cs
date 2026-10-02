using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: all files and SQLite stores belong to the fixture's unique root.
public sealed class OperatorLessonUntilGoalRecordTests
{
    [Fact]
    public async Task UniqueUnlandedPrefix_StoresFullGoalId()
    {
        using var fixture = new OperatorLessonHarness();
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Fix the receipt");
        var intent = await Enqueue(fixture, goal.Id.Value[..8]);

        fixture.Coordinator.ExecuteWorkspacePending(kernel);

        Assert.Equal(OperatorIntentStatus.Applied, (await fixture.IntentStore.GetAsync(intent.Id))!.Status);
        Assert.Equal(goal.Id.Value, Assert.Single(fixture.LessonStore.List()).UntilGoalId);
    }

    [Fact]
    public async Task UnknownPrefix_RejectsAndNamesPrefix()
    {
        using var fixture = new OperatorLessonHarness();
        var intent = await Enqueue(fixture, "missing");

        fixture.Coordinator.ExecuteWorkspacePending(new AgentOrchestratorKernel());

        await AssertRejected(fixture, intent, "until-goal-unknown prefix=missing");
    }

    [Fact]
    public async Task AmbiguousPrefix_RejectsAndNamesPrefix()
    {
        using var fixture = new OperatorLessonHarness();
        var kernel = new AgentOrchestratorKernel();
        kernel.CreateGoal(new GoalId("abc00000000000000000000000000001"), "First fix");
        kernel.CreateGoal(new GoalId("abc00000000000000000000000000002"), "Second fix");
        var intent = await Enqueue(fixture, "abc");

        fixture.Coordinator.ExecuteWorkspacePending(kernel);

        await AssertRejected(fixture, intent, "until-goal-ambiguous prefix=abc");
    }

    [Fact]
    public async Task LandedPrefix_RejectsAndNamesPrefix()
    {
        using var fixture = new OperatorLessonHarness();
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Already fixed");
        kernel = Complete(kernel, goal.Id);
        var prefix = goal.Id.Value[..8];
        var intent = await Enqueue(fixture, prefix);
        GoalOperationJournal.Completed(fixture.Root, goal, "conductor:land", "Landed fix");
        var coordinator = WithProductionLandedCheck(fixture);

        coordinator.ExecuteWorkspacePending(kernel);

        await AssertRejected(fixture, intent, "until-goal-already-landed prefix=" + prefix);
    }

    [Fact]
    public async Task CompletedButRetiredGoal_AcceptsCondition()
    {
        using var fixture = new OperatorLessonHarness();
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Retired without landing");
        kernel = Complete(kernel, goal.Id);
        GoalOperationJournal.RecordTerminalDisposition(fixture.Root, goal,
            new GoalTerminalDisposition(GoalTerminalDispositionKind.Retired, "Retired without landing"));
        var intent = await Enqueue(fixture, goal.Id.Value[..8]);

        WithProductionLandedCheck(fixture).ExecuteWorkspacePending(kernel);

        Assert.Equal(OperatorIntentStatus.Applied, (await fixture.IntentStore.GetAsync(intent.Id))!.Status);
        Assert.Equal(goal.Id.Value, Assert.Single(fixture.LessonStore.List()).UntilGoalId);
    }

    [Fact]
    public async Task PersistedGoalOutsideKernel_ResolvesAndDeduplicates()
    {
        using var fixture = new OperatorLessonHarness();
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Persisted fix");
        await new SqliteOrchestratorStateRepository(fixture.Workspace.SqliteStatePath).SaveAsync(kernel);
        var coordinator = OperatorIntentCoordinator.CreateDefault(fixture.Workspace);
        var first = await Enqueue(fixture, goal.Id.Value[..8]);

        coordinator.ExecuteWorkspacePending(new AgentOrchestratorKernel());
        var second = await Enqueue(fixture, goal.Id.Value[..8]);
        coordinator.ExecuteWorkspacePending(kernel);

        Assert.Equal(OperatorIntentStatus.Applied, (await fixture.IntentStore.GetAsync(first.Id))!.Status);
        Assert.Equal(OperatorIntentStatus.Applied, (await fixture.IntentStore.GetAsync(second.Id))!.Status);
        Assert.Equal(2, fixture.LessonStore.List().Count);
        Assert.All(fixture.LessonStore.List(), lesson => Assert.Equal(goal.Id.Value, lesson.UntilGoalId));
    }

    [Fact]
    public async Task RecordCommand_CarriesPrefixWithoutResolvingIt()
    {
        using var fixture = new OperatorLessonHarness();
        using var output = new StringWriter();

        Assert.Equal(0, CliLessonCommands.Run(["lesson", "record", "--situation", "Run fails",
            "--rule", "Read receipt", "--evidence", "operator-evidence:proof.txt",
            "--until-goal", "NotYetKnown"], fixture.Workspace, output, fixture.Root));

        var intent = Assert.Single(await fixture.IntentStore.ListForGoalAsync(OperatorIntentScopes.Workspace));
        var payload = JsonSerializer.Deserialize<LessonRecordOperatorIntentPayload>(intent.PayloadJson,
            OperatorIntentJson.Options)!;
        Assert.Equal("NotYetKnown", payload.UntilGoal);
        Assert.False(File.Exists(fixture.Workspace.OperatorLessonsStorePath));
    }

    [Fact]
    public void OldPayload_OmitsNullConditionAndStillDeserializes()
    {
        var payload = new LessonRecordOperatorIntentPayload("s", "r", [], [], null, "root");
        var json = JsonSerializer.Serialize(payload, OperatorIntentJson.Options);

        Assert.DoesNotContain("untilGoal", json, StringComparison.Ordinal);
        Assert.Null(JsonSerializer.Deserialize<LessonRecordOperatorIntentPayload>(json,
            OperatorIntentJson.Options)!.UntilGoal);
    }

    internal static async Task<OperatorIntentRecord> Enqueue(OperatorLessonHarness fixture, string prefix)
    {
        File.WriteAllText(Path.Combine(fixture.Root, "proof.txt"), "proof\n");
        var id = Guid.NewGuid().ToString("N");
        var payload = new LessonRecordOperatorIntentPayload("When receipt fails", "Inspect proof",
            ["operator-evidence:proof.txt"], ["steward"], null, fixture.Root, prefix);
        return await fixture.IntentStore.EnqueueAsync(new OperatorIntentRecord(id, id,
            OperatorIntentVerbs.LessonRecord, OperatorIntentScopes.Workspace, null,
            JsonSerializer.Serialize(payload, OperatorIntentJson.Options), [], "tester", "cli",
            "local-process", fixture.Now));
    }

    internal static AgentOrchestratorKernel Complete(AgentOrchestratorKernel kernel, GoalId id)
    {
        var snapshot = kernel.ExportSnapshot();
        return AgentOrchestratorKernel.FromSnapshot(snapshot with
        {
            Goals = snapshot.Goals.Select(goal => goal.Id == id.Value
                ? goal with { Status = GoalStatus.Completed } : goal).ToArray()
        });
    }

    internal static OperatorIntentCoordinator WithProductionLandedCheck(OperatorLessonHarness fixture) =>
        new(fixture.IntentStore, utcNow: () => fixture.Now)
        {
            Lessons = new OperatorLessonIntentServices(fixture.LessonStore,
                new AdjudicationEvidenceResolver(fixture.Root), _ => null,
                IsGoalLanded: OperatorIntentCoordinator.BuildLessonLandedPredicate(fixture.Root))
        };

    private static async Task AssertRejected(OperatorLessonHarness fixture, OperatorIntentRecord intent,
        string reason)
    {
        var outcome = await fixture.IntentStore.GetAsync(intent.Id);
        Assert.Equal(OperatorIntentStatus.Rejected, outcome!.Status);
        Assert.Contains(reason, outcome.Outcome, StringComparison.Ordinal);
        Assert.Empty(fixture.LessonStore.List(includeRetired: true));
    }
}
