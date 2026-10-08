using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: all state, event data and time come from per-test fakes.
public sealed class OwnerConsoleViewModelBuilderTests
{
    [Fact]
    public async Task EmptyWorkspaceBuildsWithTheRealQuestionReadModel()
    {
        var harness = new OwnerConsoleHarness();
        var state = new EmptyOwnerConsoleStateQueries();
        var directory = Path.Combine(Path.GetTempPath(), "owner-console-absent-" + Guid.NewGuid().ToString("N"));
        var builder = new OwnerConsoleViewModelBuilder(state, new OwnerQuestionReadModel(state, directory),
            harness.Liveness, new Epics(), harness.Clock);

        var model = await builder.BuildAsync(Inputs(harness));

        Assert.Empty(model.Decisions);
        Assert.Empty(model.Board);
        Assert.Equal(0, model.Status.HiddenQuestions);
    }

    [Fact]
    public async Task StatusIncludesLivenessCountsEventAgeAndSessionLandings()
    {
        var harness = new OwnerConsoleHarness();
        harness.AddGoal("11111111", "Search", AgentRole.Developer);
        var question = new OwnerQuestion("q1", "11111111", OwnerQuestionKind.HumanInput, "Proceed?");
        var questions = new SnapshotQuestions(new([question], [new(question with { ItemId = "hidden" }, "duplicate")]));
        var builder = Builder(harness, questions);
        var now = harness.Clock.GetUtcNow();
        var model = await builder.BuildAsync(new(now.AddMinutes(-5), now.AddSeconds(-20), [], 3));

        Assert.Equal(new OwnerConsoleStatus(true, 1, 1, 1, TimeSpan.FromSeconds(20), 3), model.Status);
        var stopped = await Builder(harness, questions, new Stopped()).BuildAsync(new(now, null, [], 0));
        Assert.False(stopped.Status.ConductorRunning);
        Assert.Null(stopped.Status.LastEventAge);
    }

    [Fact]
    public async Task DecisionsKeepSourceOrderFullTextFieldsAndStableNumbers()
    {
        var harness = new OwnerConsoleHarness();
        harness.Questions.Items.Add(new("q1", "111111111111", OwnerQuestionKind.HumanInput,
            "  Ship   it?\r\nExplain the risks.", "portfolio", "high", "ship"));
        harness.Questions.Items.Add(new("q2", "222222222222", OwnerQuestionKind.Clarification, new string('x', 200)));
        var builder = Builder(harness, harness.Questions);
        var model = await builder.BuildAsync(Inputs(harness));

        Assert.Equal(["q1", "q2"], model.Decisions.Select(item => item.Id));
        Assert.Equal(new OwnerConsoleDecision("q1", 1, "111111111111", "11111111", OwnerQuestionKind.HumanInput,
            "Ship it?", "  Ship   it?\r\nExplain the risks.", "portfolio", "high", "ship"), model.Decisions[0]);
        Assert.Equal(OwnerConsoleViewModelBuilder.SummaryMaxLength, model.Decisions[1].Summary.Length);
        Assert.EndsWith("…", model.Decisions[1].Summary);
        Assert.Equal(new string('x', 200), model.Decisions[1].FullText);
        harness.Questions.Items.Reverse();
        var reordered = await builder.BuildAsync(Inputs(harness));
        Assert.Equal([2, 1], reordered.Decisions.Select(item => item.Number));
    }

    [Fact]
    public async Task BoardIncludesEpicTitleStateAgeAndFirstIncompleteStageOrDash()
    {
        var harness = new OwnerConsoleHarness();
        harness.AddGoal("11111111", "# Finished tasks\n\nLong objective", AgentRole.Developer);
        harness.Kernel.CreateGoal(new GoalId("22222222"), "# Review search\n\nMore text",
            [new TaskSpec(TaskId.New(), "Build", AgentRole.Developer), new TaskSpec(TaskId.New(), "Review", AgentRole.Reviewer)]);
        harness.Kernel.ActivateGoal(new GoalId("22222222"), AgentCatalog.Default().Agents);
        var before = harness.Kernel.ExportSnapshot();
        harness.Kernel.ReplaceWithSnapshot(before with
        {
            Goals = before.Goals.Select(goal => goal with
            {
                Timeline = goal.Timeline.Select(item => item with { OccurredAt = harness.Clock.GetUtcNow().AddMinutes(-2) }).ToArray(),
                Tasks = goal.Tasks.Select((task, index) => task with
                { Status = index == 0 ? WorkTaskStatus.Completed : WorkTaskStatus.Assigned }).ToArray()
            }).ToArray()
        });
        var model = await Builder(harness, harness.Questions).BuildAsync(Inputs(harness));

        Assert.Equal(2, model.Board.Length);
        var finished = Assert.Single(model.Board, row => row.GoalPrefix == "11111111");
        Assert.Equal("Control plane", finished.Epic);
        Assert.Equal("Finished tasks", finished.Title);
        Assert.Equal("Active", finished.State);
        Assert.Equal("-", finished.Stage);
        Assert.Equal(TimeSpan.FromMinutes(2), finished.Age);
        var review = Assert.Single(model.Board, row => row.GoalPrefix == "22222222");
        Assert.Equal(string.Empty, review.Epic);
        Assert.Equal("Review search", review.Title);
        Assert.Equal("Reviewer", review.Stage);
    }

    [Fact]
    public async Task ActivityIncludesOnlyTaggedEventsNewestFirstAndCapped()
    {
        var harness = new OwnerConsoleHarness();
        var now = harness.Clock.GetUtcNow();
        var events = Enumerable.Range(0, OwnerConsoleViewModelBuilder.MaxActivityItems + 5)
            .Select(index => new OwnerConductEvent(now.AddSeconds(index), "acceptance", "11111111", $"result=passed index={index}"))
            .Append(new(now.AddHours(1), "watch-transition", null, "running")).Reverse().ToArray();
        var model = await Builder(harness, harness.Questions).BuildAsync(new(now, now, events, 0));

        Assert.Equal(OwnerConsoleViewModelBuilder.MaxActivityItems, model.Activity.Length);
        Assert.Equal(now.AddSeconds(OwnerConsoleViewModelBuilder.MaxActivityItems + 4), model.Activity[0].Timestamp);
        Assert.Equal(now.AddSeconds(5), model.Activity[^1].Timestamp);
        Assert.All(model.Activity, item => { Assert.Equal("outcome", item.Tag); Assert.Equal("acceptance", item.Kind); });
        Assert.Equal(model.Activity.OrderByDescending(item => item.Timestamp), model.Activity);
        Assert.False(OwnerActivityNarrator.IsLanding(events[1]));
        Assert.True(OwnerActivityNarrator.IsLanding(new(now, "loop-relaunch", "11111111", "LOOP_RELAUNCH_SCHEDULED")));
        Assert.False(OwnerActivityNarrator.IsLanding(new(now, "acceptance", null, "result=passed-later")));
    }

    private static OwnerConsoleViewInputs Inputs(OwnerConsoleHarness harness) => new(harness.Clock.GetUtcNow(), null, [], 0);
    private static OwnerConsoleViewModelBuilder Builder(OwnerConsoleHarness harness, IOwnerQuestionSource questions,
        IConductorLiveness? liveness = null) => new(harness.State, questions, liveness ?? harness.Liveness, new Epics(), harness.Clock);

    private sealed class SnapshotQuestions(OwnerQuestionSnapshot snapshot) : IOwnerQuestionSource
    {
        public Task<IReadOnlyList<OwnerQuestion>> ListOpenAsync(CancellationToken token) => Task.FromResult(snapshot.Live);
        public Task<OwnerQuestionSnapshot> ReadAsync(CancellationToken token) => Task.FromResult(snapshot);
    }

    private sealed class Epics : IOwnerGoalEpicLookup
    {
        public Task<string> GetTitleAsync(string goalId, CancellationToken token) =>
            Task.FromResult(goalId == "11111111" ? "Control plane" : string.Empty);
    }

    private sealed class Stopped : IConductorLiveness
    {
        public bool IsRunning() => false;
    }
}
