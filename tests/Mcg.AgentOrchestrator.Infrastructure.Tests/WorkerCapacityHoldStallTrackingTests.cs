using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

// Parallel-safe: each case owns its kernel, injected timestamps and temporary event log.
public sealed class WorkerCapacityHoldStallTrackingTests(ITestOutputHelper output)
    : ConductorBatchLoopTests(output)
{
    private static readonly DateTimeOffset Start = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Threshold = TimeSpan.FromMinutes(10);
    private const string Reason = "At worker cap (4/4); will advance when a slot opens";

    [Xunit.Theory]
    [Xunit.InlineData(ConductorHoldOwner.WorkerCapacity, false)]
    [Xunit.InlineData(ConductorHoldOwner.None, true)]
    public void WorkspaceReady_PastThreshold_OnlyOwnerlessHoldEscalates(
        ConductorHoldOwner owner, bool expectStall)
    {
        var (kernel, goal) = SimpleGoal();
        var driver = MakeDriver();
        var lines = new List<string>();
        var changed = new HashSet<GoalId>();
        var root = Path.Combine(Path.GetTempPath(), "capacity-hold-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var conduct = new ConductEventLogWriter(Path.Combine(root, "conduct.log"), utcNow: () => Start);
            var held = new ConductorAdvanceOutcome.Held(GoalLifecycleState.WorkspaceReady, Reason) { Owner = owner };
            foreach (var minutes in new[] { 0, 11, 12 })
                ConductorBatchLoop.TrackGoalOutcome(kernel, driver, goal, held,
                    Start.AddMinutes(minutes), Threshold, changed, lines, conduct);

            var events = File.Exists(conduct.CurrentPath)
                ? File.ReadAllLines(conduct.CurrentPath).Select(line => JsonSerializer.Deserialize<JsonElement>(line)).ToArray()
                : [];
            if (expectStall)
            {
                var escalation = Assert.Single(events, item => item.GetProperty("eventKind").GetString() == "goal-escalation");
                Assert.Equal("ownerless-hold-stalled state=WorkspaceReady heldForSeconds=660 blocker=" + Reason,
                    escalation.GetProperty("detail").GetString());
                var stalled = Assert.Single(lines, line => line.StartsWith("GOAL_STALLED ", StringComparison.Ordinal));
                Assert.Contains("owner=none repeatedForSeconds=660", stalled, StringComparison.Ordinal);
                Assert.Equal(Start.AddMinutes(11), goal.CurrentHold?.StalledAt);
            }
            else
            {
                Assert.Empty(events);
                Assert.DoesNotContain(lines, line => line.StartsWith("GOAL_STALLED ", StringComparison.Ordinal));
                Assert.Null(goal.CurrentHold);
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Xunit.Fact]
    public void CapacityOwner_AfterOwnerlessObservation_ClearsExistingHold()
    {
        var (kernel, goal) = SimpleGoal();
        var driver = MakeDriver();
        var lines = new List<string>();
        var changed = new HashSet<GoalId>();
        var held = new ConductorAdvanceOutcome.Held(GoalLifecycleState.WorkspaceReady, Reason);
        ConductorBatchLoop.TrackGoalOutcome(kernel, driver, goal, held, Start, Threshold, changed, lines);
        Assert.NotNull(goal.CurrentHold);
        changed.Clear();

        ConductorBatchLoop.TrackGoalOutcome(kernel, driver, goal,
            held with { Owner = ConductorHoldOwner.WorkerCapacity }, Start.AddMinutes(11), Threshold, changed, lines);

        Assert.Null(goal.CurrentHold);
        Assert.Contains(goal.Id, changed);
        Assert.Empty(lines);
    }
}
