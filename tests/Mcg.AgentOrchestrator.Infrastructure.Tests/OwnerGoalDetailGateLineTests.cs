// Parallel-safe: fixed snapshots and models; no files or process.
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class OwnerGoalDetailGateLineTests
{
    private const string Id = "22222222222222222222222222222222";
    private static readonly DateTimeOffset At = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(false, "Gate: train, 3 members, 22/27 lanes, 0 failed, ~9m left (g opens the gate view)")]
    [InlineData(true, "Gate: train, 3 members, 22/27 lanes, 1 failed, failing (g opens the gate view)")]
    public void Render_LiveTrain_WritesExactLineAfterLanding(bool failing, string expected)
    {
        var gate = Train();
        if (failing) gate = gate with { Lanes = [Failed(false), Failed(true)] };
        var detail = new OwnerGoalDetailSnapshot(VerifyingGoal(), null, [], gate);

        var lines = Render(detail).Split(Environment.NewLine);

        var landing = Array.FindIndex(lines, line => line.StartsWith("Landing:", StringComparison.Ordinal));
        Assert.True(landing >= 0);
        Assert.Equal(expected, lines[landing + 1]);
        Assert.Single(lines.Where(line => line.StartsWith("Gate:", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData(360)]
    [InlineData(399)]
    public void Render_SoloWithoutLaneCount_WritesWholeElapsedMinutes(int elapsedSeconds)
    {
        var gate = Train() with { Kind = "solo", Members = [new(Id, "Member")], TotalCount = 0,
            Elapsed = TimeSpan.FromSeconds(elapsedSeconds) };

        var text = Render(new(VerifyingGoal(), null, [], gate));

        Assert.Contains("Gate: solo, 1 member, running 6m (g opens the gate view)", text.Split(Environment.NewLine));
    }

    [Fact]
    public void Render_NullGate_EqualsThreeArgumentSnapshotWithoutGateLine()
    {
        var goal = VerifyingGoal();
        var text = Render(new(goal, null, [], Gate: null));

        Assert.Equal(Render(new(goal, null, [])), text);
        Assert.DoesNotContain(text.Split(Environment.NewLine), line => line.StartsWith("Gate:", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("primary")]
    [InlineData("other")]
    [InlineData("nonmember")]
    [InlineData("idle")]
    public async Task Load_GateSource_SelectsMembershipWithoutExtraReads(string scenario)
    {
        var harness = new OwnerConsoleHarness();
        harness.AddGoal(Id, "Member", AgentRole.Developer);
        var memberGate = Train();
        var otherGate = Train() with { Kind = "solo", Members = [new("11111111", "Other")] };
        var source = scenario switch
        {
            "primary" => memberGate,
            "other" => otherGate with { OtherLiveGates = [memberGate] },
            "nonmember" => otherGate,
            _ => OwnerConsoleGateProgress.Idle()
        };
        var tail = new RecordingTail(harness.Tail);
        var baseline = await new OwnerGoalDetailQuery(harness.State, tail).LoadAsync(Id, CancellationToken.None);
        Assert.NotNull(baseline);
        Assert.Null(baseline.Gate);
        var stateCalls = harness.State.Calls.ToArray();
        var tailCalls = tail.Calls.ToArray();
        Assert.NotEmpty(stateCalls);
        Assert.Equal((Id, 100), Assert.Single(tailCalls));
        harness.State.Calls.Clear();
        tail.Calls.Clear();
        var sourceCalls = 0;

        var detail = await new OwnerGoalDetailQuery(harness.State, tail, () => { sourceCalls++; return source; })
            .LoadAsync(Id, CancellationToken.None);

        Assert.NotNull(detail);
        Assert.Equal(1, sourceCalls);
        if (scenario is "primary" or "other")
        {
            Assert.Same(memberGate, detail.Gate);
            Assert.Same(source.ForMember(Id), detail.Gate);
        }
        else Assert.Null(detail.Gate);
        Assert.Equal(stateCalls, harness.State.Calls);
        Assert.Equal(tailCalls, tail.Calls);

        var output = new OwnerConsoleHarness.FakeOutput();
        var composedGoal = await OwnerConsoleGoalDetailFormatter.ComposeAsync(harness.State, tail, Id, output,
            CancellationToken.None, TimeZoneInfo.Utc, () => source);
        Assert.Same(detail.Goal, composedGoal);
        Assert.Equal(Render(detail), output.Text);
    }

    private static Goal VerifyingGoal()
    {
        var harness = new OwnerConsoleHarness();
        harness.AddGoal(Id, "Member", AgentRole.Developer);
        var snapshot = harness.Kernel.ExportSnapshot();
        harness.Kernel.ReplaceWithSnapshot(snapshot with { Goals = snapshot.Goals.Select(goal => goal with
        {
            Status = GoalStatus.Verifying,
            Timeline = [new(goal.Id, null, ProgressKind.GoalPolicyDecision, "entered Verifying", At)]
        }).ToArray() });
        return harness.Kernel.Goals.Single();
    }

    private static OwnerConsoleGateProgress Train() => new(true, "train",
        [new(Id, "Member"), new("33333333", "Second"), new("44444444", "Third")], At,
        TimeSpan.FromMinutes(6), [], .8, 540, 22, 27);

    private static OwnerConsoleGateLane Failed(bool attribution) => new("Failed", OwnerConsoleGateLaneState.Done,
        "failed", "local", TimeSpan.FromSeconds(20), 20, false, IsAttribution: attribution);

    private static string Render(OwnerGoalDetailSnapshot detail)
    {
        var output = new OwnerConsoleHarness.FakeOutput();
        OwnerConsoleGoalDetailFormatter.Render(detail, output, TimeZoneInfo.Utc);
        return output.Text;
    }

    private sealed class RecordingTail(IGoalEventTail inner) : IGoalEventTail
    {
        internal readonly List<(string Id, int Count)> Calls = [];
        public IReadOnlyList<string> ReadLast(string goalId, int count)
        { Calls.Add((goalId, count)); return inner.ReadLast(goalId, count); }
    }
}
