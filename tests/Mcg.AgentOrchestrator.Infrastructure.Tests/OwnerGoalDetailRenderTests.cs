using System.Text;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: per-test snapshots, kernels and output buffers.
public sealed class OwnerGoalDetailRenderTests
{
    private const string Id = "22222222222222222222222222222222";
    private static readonly DateTimeOffset At = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void SnapshotRendersShortHeaderSecondLandingAndNoRecentEvents()
    {
        var detail = new OwnerGoalDetailSnapshot(VerifiedGoal(), 2, []);
        var output = new Output();

        OwnerConsoleGoalDetailFormatter.Render(detail, output, TimeZoneInfo.Utc);

        var lines = output.Text.Split(Environment.NewLine);
        Assert.Equal(Id[..8] + "  Second goal", lines[0]);
        Assert.Contains("Landing: waiting for the gate (2nd)", lines);
        Assert.Contains("Recent events: none", lines);
    }

    [Fact]
    public void ConsecutiveDeveloperDispatchesRenderOnceWithLocalTimestamp()
    {
        var goal = VerifiedGoal();
        var dispatched = new OwnerConductEvent(At, "goal-lifecycle", Id,
            "TaskDispatched role=Developer task=" + goal.Tasks[0].Id.Value);
        var detail = new OwnerGoalDetailSnapshot(goal, null, [dispatched, dispatched]);
        var output = new Output();
        var zone = TimeZoneInfo.CreateCustomTimeZone("render-test", TimeSpan.FromHours(14), "render-test", "render-test");

        OwnerConsoleGoalDetailFormatter.Render(detail, output, zone);

        var line = Assert.Single(output.Text.Split(Environment.NewLine)
            .Where(line => line.EndsWith("Developer started", StringComparison.Ordinal)));
        Assert.Equal($"  {At.ToLocalTime():HH:mm:ss} Developer started", line);
    }

    [Fact]
    public async Task ComposeMatchesQuerySnapshotRenderForVerifiedQueueAndEvents()
    {
        var harness = new OwnerConsoleHarness();
        harness.AddGoal("11111111111111111111111111111111", "First", AgentRole.Developer);
        harness.AddGoal(Id, "Second goal", AgentRole.Developer);
        harness.AddGoal("33333333333333333333333333333333", "Third", AgentRole.Developer);
        var snapshot = harness.Kernel.ExportSnapshot();
        harness.Kernel.ReplaceWithSnapshot(snapshot with { Goals = snapshot.Goals.Select((goal, index) => goal with
        {
            Status = GoalStatus.Verified,
            Timeline = [new(goal.Id, null, ProgressKind.GoalPolicyDecision, "entered Verified", At.AddMinutes(index))]
        }).ToArray() });
        var goal = harness.Kernel.Goals.Single(goal => goal.Id.Value == Id);
        var dispatched = JsonSerializer.Serialize(new
        {
            timestamp = At, eventType = "TaskDispatched", taskId = goal.Tasks[0].Id.Value
        });
        var tail = new Tail(["legacy raw line", dispatched, dispatched]);
        var composed = new Output();
        var rendered = new Output();

        var composedGoal = await OwnerConsoleGoalDetailFormatter.ComposeAsync(
            harness.State, tail, Id, composed, CancellationToken.None);
        var detail = await new OwnerGoalDetailQuery(harness.State, tail).LoadAsync(Id, CancellationToken.None);
        Assert.NotNull(detail);
        OwnerConsoleGoalDetailFormatter.Render(detail, rendered, TimeZoneInfo.Local);

        Assert.Same(detail.Goal, composedGoal);
        Assert.Equal(composed.Text, rendered.Text);
        Assert.Contains("Landing: waiting for the gate (2nd)", rendered.Text);
        Assert.Contains("  Developer: ", rendered.Text);
        Assert.Contains("Developer started", rendered.Text);
    }

    private static Goal VerifiedGoal()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal(new GoalId(Id), "Second goal",
            [new TaskSpec(TaskId.New(), "Work", AgentRole.Developer)]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        var snapshot = kernel.ExportSnapshot();
        kernel.ReplaceWithSnapshot(snapshot with { Goals = snapshot.Goals.Select(item => item with
        {
            Status = GoalStatus.Verified
        }).ToArray() });
        return kernel.Goals.Single();
    }

    private sealed class Output : IOwnerConsoleOutput
    {
        private readonly StringBuilder _buffer = new();
        internal string Text => _buffer.ToString();
        public void Write(string text) => _buffer.Append(text);
        public void WriteLine(string text) => _buffer.AppendLine(text);
    }

    private sealed class Tail(IReadOnlyList<string> lines) : IGoalEventTail
    {
        public IReadOnlyList<string> ReadLast(string goalId, int count) => lines;
    }
}
