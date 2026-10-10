using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;

// Parallel-safe: state, delegate call records and clocks are per test.
public sealed class OwnerConsoleGateMotionBoardTests
{
    private const string GateGoal = "57cf8f07be1944ce91efaa1b16dd85e0";
    private const string WaitingGoal = "22222222abcdef0123456789abcdef01";
    private const string WorkerGoal = "11111111abcdef0123456789abcdef01";

    [Fact]
    public async Task MotionIsAppendedOnlyToRunningGateAndCalledOnceWithFullId()
    {
        var clock = new OwnerConsoleTestClock();
        var harness = Harness(clock);
        var calls = new List<string>();
        var builder = Builder(harness, clock, id => { calls.Add(id); return "no output change 14m"; });

        var model = await builder.BuildBoardAsync();

        Assert.Equal([GateGoal], calls);
        Assert.Equal("gate running 6m, no output change 14m", model.Board.Single(row => row.GoalId == GateGoal).Stage);
        Assert.Equal("gate running 6m, no output change 14m", model.Board.Single(row => row.GoalId == GateGoal).StageWithoutQuestion);
        Assert.Equal("waiting for gate (1st in queue)", model.Board.Single(row => row.GoalId == WaitingGoal).Stage);
        Assert.Equal("Developer", model.Board.Single(row => row.GoalId == WorkerGoal).Stage);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public async Task AbsentMotionAndDefaultBuilderPreserveEveryStage(string? motion)
    {
        var clock = new OwnerConsoleTestClock();
        var harness = Harness(clock);
        var calls = new List<string>();
        var withDelegate = await Builder(harness, clock, id => { calls.Add(id); return motion; }).BuildBoardAsync();
        var withoutParameter = await new OwnerConsoleViewModelBuilder(harness.State, harness.Questions,
            harness.Liveness, new Epics(), clock).BuildBoardAsync();

        Assert.Equal([GateGoal], calls);
        Assert.Equal(["gate running 6m", "waiting for gate (1st in queue)", "Developer"],
            withoutParameter.Board.Select(row => row.Stage));
        Assert.Equal(withoutParameter.Board.Select(row => (row.GoalId, row.Stage, row.StageWithoutQuestion)),
            withDelegate.Board.Select(row => (row.GoalId, row.Stage, row.StageWithoutQuestion)));
    }

    private static OwnerConsoleHarness Harness(OwnerConsoleTestClock clock)
    {
        var harness = new OwnerConsoleHarness();
        foreach (var id in new[] { GateGoal, WaitingGoal, WorkerGoal }) harness.AddGoal(id, id, AgentRole.Developer);
        var snapshot = harness.Kernel.ExportSnapshot();
        harness.Kernel.ReplaceWithSnapshot(snapshot with
        {
            Goals = snapshot.Goals.Select(goal => goal with
            {
                Status = goal.Id == GateGoal ? GoalStatus.Verifying : goal.Id == WaitingGoal ? GoalStatus.Verified : GoalStatus.Active,
                Tasks = goal.Tasks.Select(task => task with
                { Status = goal.Id == WorkerGoal ? WorkTaskStatus.Running : WorkTaskStatus.Completed }).ToArray(),
                Timeline = [new(goal.Id, null, ProgressKind.GoalPolicyDecision, "state changed", clock.Now.AddMinutes(-6))]
            }).ToArray()
        });
        return harness;
    }

    private static OwnerConsoleViewModelBuilder Builder(OwnerConsoleHarness harness, TimeProvider clock,
        Func<string, string?> motion) => new(harness.State, harness.Questions, harness.Liveness, new Epics(), clock, gateMotion: motion);

    private sealed class Epics : IOwnerGoalEpicLookup
    {
        public Task<string> GetTitleAsync(string id, CancellationToken token) => Task.FromResult(string.Empty);
    }
}
