using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;

public sealed class PromptRolloutWatchEvaluationTests
{
    internal static readonly DateTimeOffset LandedAt = new(2026, 10, 2, 1, 10, 0, TimeSpan.Zero);
    private const string LandingGoal = "11111111aaaaaaaaaaaaaaaaaaaaaaaa";
    private const string LandingSha = "0123456789abcdef0123456789abcdef01234567";

    [Xunit.Fact]
    public void FailedAndRetriedOtherGoalsFireOnceWhileTaskNoteIsIgnored()
    {
        using var fixture = new Fixture();
        var kernel = new AgentOrchestratorKernel(new FixedClock());
        AddEvent(kernel, "aaaaaaaa", AgentRole.Tester, ProgressKind.TaskFailed, "revert-src", LandedAt.AddSeconds(1));
        AddEvent(kernel, "bbbbbbbb", AgentRole.Tester, ProgressKind.TaskRetried, "negative_control", LandedAt.AddSeconds(2));
        AddEvent(kernel, "cccccccc", AgentRole.Tester, ProgressKind.TaskNote, "revert-src", LandedAt.AddSeconds(3));
        var coordinator = fixture.Coordinator();
        coordinator.EvaluateTick(kernel);
        Assert.Empty(fixture.Events);
        Assert.Equal(new[] { "aaaaaaaa", "bbbbbbbb" }, fixture.Watch().MatchedGoalIds);
        AddEvent(kernel, "dddddddd", AgentRole.Tester, ProgressKind.TaskRetried, "revert-src", LandedAt.AddSeconds(4));
        coordinator.EvaluateTick(kernel);
        coordinator.EvaluateTick(kernel);
        Assert.Equal($"PROMPT_ROLLOUT_SUSPECT landing={LandingSha} goal={LandingGoal} " +
            "phrases=revert-src,negative_control goals=aaaaaaaa,bbbbbbbb,dddddddd", Assert.Single(fixture.Events));
        Assert.Equal("fired", fixture.Watch().State);
        fixture.Coordinator().EvaluateTick(kernel);
        Assert.Single(fixture.Events);
    }

    [Xunit.Fact]
    public void LandingGoalOldEventsOtherRolesAndCaseMismatchesAreExcluded()
    {
        using var fixture = new Fixture();
        var kernel = new AgentOrchestratorKernel(new FixedClock());
        AddEvent(kernel, LandingGoal, AgentRole.Tester, ProgressKind.TaskFailed, "revert-src", LandedAt.AddSeconds(1));
        AddEvent(kernel, "oldgoal1", AgentRole.Reviewer, ProgressKind.TaskRetried, "revert-src", LandedAt.AddSeconds(-1));
        AddEvent(kernel, "equal001", AgentRole.Reviewer, ProgressKind.TaskRetried, "revert-src", LandedAt);
        AddEvent(kernel, "develop1", AgentRole.Developer, ProgressKind.TaskFailed, "negative_control", LandedAt.AddSeconds(2));
        AddEvent(kernel, "planner1", AgentRole.Planner, ProgressKind.TaskRetried, "revert-src", LandedAt.AddSeconds(3));
        AddEvent(kernel, "casegoal", AgentRole.Tester, ProgressKind.TaskRetried, "REVERT-SRC", LandedAt.AddSeconds(4));
        fixture.Coordinator().EvaluateTick(kernel);
        Assert.Empty(fixture.Events);
        Assert.Empty(fixture.Watch().MatchedGoalIds);
        Assert.Empty(fixture.Watch().CountedRoundKeys);
    }

    [Xunit.Fact]
    public void FiftyCompletedOrFailedRoundsCloseSilentlyAndCannotFireLater()
    {
        using var fixture = new Fixture();
        var kernel = new AgentOrchestratorKernel(new FixedClock());
        for (var index = 0; index < 49; index++)
            AddEvent(kernel, "rounds01", index % 2 == 0 ? AgentRole.Tester : AgentRole.Reviewer,
                index % 2 == 0 ? ProgressKind.TaskCompleted : ProgressKind.TaskFailed,
                "ordinary result", LandedAt.AddSeconds(index + 1));
        fixture.Coordinator().EvaluateTick(kernel);
        Assert.Equal("open", fixture.Watch().State);
        Assert.Equal(49, fixture.Watch().CountedRoundKeys.Count);
        // Reloading and rescanning identical rounds must not spend the budget twice.
        var coordinator = fixture.Coordinator();
        coordinator.EvaluateTick(kernel);
        Assert.Equal("open", fixture.Watch().State);
        AddEvent(kernel, "rounds01", AgentRole.Reviewer, ProgressKind.TaskFailed, "ordinary result", LandedAt.AddSeconds(50));
        coordinator.EvaluateTick(kernel);
        Assert.Equal("exhausted", fixture.Watch().State);
        Assert.Equal(50, fixture.Watch().CountedRoundKeys.Count);
        foreach (var id in new[] { "later001", "later002", "later003" })
            AddEvent(kernel, id, AgentRole.Tester, ProgressKind.TaskRetried, "revert-src", LandedAt.AddSeconds(51));
        fixture.Coordinator().EvaluateTick(kernel);
        Assert.Empty(fixture.Events);
        Assert.Equal("exhausted", fixture.Watch().State);
    }

    [Xunit.Fact]
    public void MatchedGoalsAndRoundBudgetSurviveReloadWithoutOldKernelGoals()
    {
        using var fixture = new Fixture();
        var first = new AgentOrchestratorKernel(new FixedClock());
        AddEvent(first, "aaaaaaaa", AgentRole.Tester, ProgressKind.TaskFailed, "revert-src", LandedAt.AddSeconds(1));
        AddEvent(first, "bbbbbbbb", AgentRole.Reviewer, ProgressKind.TaskRetried, "negative_control", LandedAt.AddSeconds(2));
        fixture.Coordinator().EvaluateTick(first);
        var next = new AgentOrchestratorKernel(new FixedClock());
        AddEvent(next, "dddddddd", AgentRole.Reviewer, ProgressKind.TaskRetried, "revert-src", LandedAt.AddSeconds(3));
        fixture.Coordinator().EvaluateTick(next);
        Assert.Contains("goals=aaaaaaaa,bbbbbbbb,dddddddd", Assert.Single(fixture.Events));
        Assert.Single(fixture.Watch().CountedRoundKeys);
    }

    [Xunit.Fact]
    public void MalformedAndTruncatedStoreRecordsDoNotDiscardValidWatches()
    {
        using var fixture = new Fixture();
        File.AppendAllText(fixture.Path, "malformed\n{\"landingSha\":");
        var diagnostics = new List<string>();
        var store = new PromptRolloutWatchStore(fixture.Path);
        var watch = Assert.Single(store.Load(diagnostics.Add));
        Assert.Equal(LandingSha, watch.LandingSha);
        Assert.Equal(2, diagnostics.Count);
        store.Append(watch with { State = "exhausted" });
        Assert.Equal("exhausted", Assert.Single(store.Load(_ => { })).State);
    }

    [Xunit.Fact]
    public void StoreWriteFailureDoesNotPublishOrThrow()
    {
        using var fixture = new Fixture();
        var coordinator = fixture.Coordinator();
        coordinator.EvaluateTick(new AgentOrchestratorKernel(new FixedClock()));
        File.Delete(fixture.Path);
        Directory.CreateDirectory(fixture.Path);
        var kernel = new AgentOrchestratorKernel(new FixedClock());
        foreach (var id in new[] { "aaaaaaaa", "bbbbbbbb", "dddddddd" })
            AddEvent(kernel, id, AgentRole.Reviewer, ProgressKind.TaskFailed, "revert-src", LandedAt.AddSeconds(1));
        coordinator.EvaluateTick(kernel);
        Assert.Empty(fixture.Events);
        Assert.Single(fixture.Diagnostics);
    }

    internal static void AddEvent(AgentOrchestratorKernel kernel, string id, AgentRole role,
        ProgressKind kind, string message, DateTimeOffset time)
    {
        var goalId = new GoalId(id);
        var goal = kernel.Goals.FirstOrDefault(goal => goal.Id == goalId) ?? kernel.CreateGoal(goalId,
            "Observe prompt rollout", [new TaskSpec(TaskId.New(), "Observe tester result", AgentRole.Tester),
                new TaskSpec(TaskId.New(), "Observe reviewer result", AgentRole.Reviewer),
                new TaskSpec(TaskId.New(), "Observe developer result", AgentRole.Developer),
                new TaskSpec(TaskId.New(), "Observe planner result", AgentRole.Planner)]);
        var task = goal.Tasks.Single(task => task.RequiredRole == role);
        var snapshot = kernel.ExportGoalSnapshot(goalId);
        kernel.ReplaceGoalWithSnapshot(snapshot with { Timeline = [.. snapshot.Timeline,
            new ProgressEventSnapshot(id, task.Id.Value, kind, message, time)] });
    }

    internal sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => LandedAt;
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Directory.CreateTempSubdirectory("mcg-rollout-watch-").FullName;
        internal string Path => System.IO.Path.Combine(_root, PromptRolloutWatchStore.FileName);
        internal List<string> Events { get; } = [];
        internal List<string> Diagnostics { get; } = [];

        internal Fixture() => new PromptRolloutWatchStore(Path).Append(
            new(LandingSha, LandingGoal, LandedAt, ["negative_control", "revert-src"], [], [], []));
        internal PromptRolloutWatch Watch() => Assert.Single(new PromptRolloutWatchStore(Path).Load(Diagnostics.Add));
        internal PromptRolloutWatchCoordinator Coordinator() => new(_root, new PromptRolloutWatchStore(Path), line =>
        {
            if (line.StartsWith("PROMPT_ROLLOUT_SUSPECT ", StringComparison.Ordinal)) Events.Add(line);
            else Diagnostics.Add(line);
        });
        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
