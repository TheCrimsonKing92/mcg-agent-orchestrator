using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;

// Parallel-safe: per-test kernels and a recording event tail.
public sealed class OwnerGoalDetailQueryTests
{
    private const string Id = "22222222222222222222222222222222";
    private static readonly DateTimeOffset At = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task VerifiedGoalLoadsQueueAndOneTailWithParsedRoleAnnotatedEventsInTailOrder()
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
        var tail = new RecordingTail(
        [
            "{bad JSON}", "legacy raw line",
            JsonSerializer.Serialize(new { timestamp = At, eventType = "TaskDispatched", taskId = goal.Tasks[0].Id.Value }),
            JsonSerializer.Serialize(new { timestamp = At.AddSeconds(-1), eventType = "TaskCompleted", taskId = goal.Tasks[0].Id.Value })
        ]);
        var ordered = ConductorBatchLoop.OrderParallelAcceptanceEligibleGoals(harness.Kernel.Goals
            .Where(item => item.Status == GoalStatus.Verified && item.CurrentHold is null).ToArray());

        var detail = await new OwnerGoalDetailQuery(harness.State, tail).LoadAsync(Id, CancellationToken.None);

        Assert.NotNull(detail);
        Assert.Same(goal, detail.Goal);
        Assert.Equal(ordered.ToList().FindIndex(item => item.Id == goal.Id) + 1, detail.LandingQueuePosition);
        Assert.Equal(1, harness.State.AcceptanceQueueReadCount);
        Assert.Equal(new[] { "goals" }, harness.State.Calls);
        Assert.Equal((Id, 100), Assert.Single(tail.Calls));
        Assert.Collection(detail.RecentEvents,
            item => { Assert.Equal(At, item.Timestamp); Assert.Contains("TaskDispatched role=Developer", item.Detail); },
            item => { Assert.Equal(At.AddSeconds(-1), item.Timestamp); Assert.Contains("TaskCompleted", item.Detail); });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActiveOrHeldVerifiedGoalSkipsQueue(bool heldVerified)
    {
        var harness = new OwnerConsoleHarness();
        harness.AddGoal(Id, "Goal", AgentRole.Developer);
        if (heldVerified)
        {
            var snapshot = harness.Kernel.ExportSnapshot();
            harness.Kernel.ReplaceWithSnapshot(snapshot with { Goals = snapshot.Goals.Select(goal => goal with
            {
                Status = GoalStatus.Verified,
                CurrentHold = new GoalHoldSnapshot("hold", "owner-review-hold", "waiting_for_approval", At)
            }).ToArray() });
        }
        var tail = new RecordingTail([]);

        var detail = await new OwnerGoalDetailQuery(harness.State, tail).LoadAsync(Id, CancellationToken.None);

        Assert.NotNull(detail);
        Assert.Null(detail.LandingQueuePosition);
        Assert.Equal(0, harness.State.AcceptanceQueueReadCount);
        Assert.DoesNotContain("metadata", harness.State.Calls);
        Assert.Equal(new[] { "goals" }, harness.State.Calls);
        Assert.Equal((Id, 100), Assert.Single(tail.Calls));
    }

    [Fact]
    public async Task MissingGoalReturnsNullWithoutReadingTailOrQueue()
    {
        var harness = new OwnerConsoleHarness();
        harness.AddGoal(Id, "Other goal", AgentRole.Developer);
        var tail = new RecordingTail([]);

        var detail = await new OwnerGoalDetailQuery(harness.State, tail)
            .LoadAsync("44444444444444444444444444444444", CancellationToken.None);

        Assert.Null(detail);
        Assert.Equal(0, harness.State.AcceptanceQueueReadCount);
        Assert.Empty(tail.Calls);
        Assert.Equal(new[] { "goals" }, harness.State.Calls);
        Assert.DoesNotContain("metadata", harness.State.Calls);
    }

    private sealed class RecordingTail(IReadOnlyList<string> lines) : IGoalEventTail
    {
        internal readonly List<(string Id, int Count)> Calls = [];
        public IReadOnlyList<string> ReadLast(string goalId, int count)
        {
            Calls.Add((goalId, count));
            return lines;
        }
    }
}
