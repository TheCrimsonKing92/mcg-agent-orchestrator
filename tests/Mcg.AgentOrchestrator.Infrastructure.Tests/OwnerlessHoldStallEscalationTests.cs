using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each test owns its kernel, clocks, writers and unique temporary directory.
public sealed class OwnerlessHoldStallEscalationTests(ITestOutputHelper output)
    : ConductorBatchLoopTests(output)
{
    [Xunit.Fact]
    public void OwnerlessHold_RepeatedStalledTicks_EscalatesOnceThenAgainForNewIdentity()
    {
        using var fixture = new EscalationFixture();
        const string blocker = "waiting for the owner to choose a repair";
        var held = new ConductorAdvanceOutcome.Held(GoalLifecycleState.WorkspaceReady, blocker);
        fixture.Observe(held, 0);
        Assert.Empty(fixture.Timeline());
        Assert.Empty(fixture.Conduct());
        foreach (var minutes in new[] { 11, 12, 13 }) fixture.Observe(held, minutes);

        var escalation = Assert.Single(fixture.Timeline());
        Assert.Equal("GoalEscalated", escalation.GetProperty("eventType").GetString());
        Assert.Equal("ownerless-hold-stall", escalation.GetProperty("source").GetString());
        Assert.Equal(fixture.Goal.Id.Value, escalation.GetProperty("goalId").GetString());
        Assert.Equal("WorkspaceReady", escalation.GetProperty("state").GetString());
        var reason = escalation.GetProperty("reason").GetString()!;
        Assert.Contains(blocker, reason, StringComparison.Ordinal);
        Assert.Contains("heldForSeconds=660", reason, StringComparison.Ordinal);
        Assert.Equal(reason, Assert.Single(fixture.Conduct()).GetProperty("detail").GetString());
        Assert.Single(fixture.Lines, line => line.StartsWith("GOAL_STALLED ", StringComparison.Ordinal));

        var next = held with { Reason = "waiting for a different owner" };
        fixture.Observe(next, 14);
        Assert.Single(fixture.Timeline());
        fixture.Observe(next, 25);
        fixture.Observe(next, 26);
        Assert.Equal(2, fixture.Timeline().Count);
        Assert.Equal(2, fixture.Conduct().Count);
        Assert.Contains(next.Reason, fixture.Timeline()[1].GetProperty("reason").GetString()!, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void OwnerlessHold_ClearedThenReappearing_EscalatesForNewOccurrence()
    {
        using var fixture = new EscalationFixture();
        var held = new ConductorAdvanceOutcome.Held(GoalLifecycleState.Verified, "waiting for an owner");
        fixture.Observe(held, 0);
        fixture.Observe(held, 11);
        fixture.Observe(new ConductorAdvanceOutcome.Executed(GoalLifecycleState.Verified, "resolved"), 12);
        Assert.Null(fixture.Goal.CurrentHold);
        fixture.Observe(held, 13);
        Assert.Null(fixture.Goal.CurrentHold?.StalledAt);
        Assert.Single(fixture.Timeline());
        fixture.Observe(held, 24);
        Assert.Equal(2, fixture.Timeline().Count);
        Assert.Equal(2, fixture.Conduct().Count);
    }

    [Xunit.Fact]
    public void OwnerlessHold_StableIdentityWithChangingDetail_DoesNotEscalateTwice()
    {
        using var fixture = new EscalationFixture();
        var held = new ConductorAdvanceOutcome.Held(
            GoalLifecycleState.WorkspaceReady, "waiting age=0", "same-lease");
        fixture.Observe(held, 0);
        fixture.Observe(held with { Reason = "waiting age=11" }, 11);
        fixture.Observe(held with { Reason = "waiting age=12" }, 12);
        Assert.Contains("waiting age=11", Assert.Single(fixture.Timeline()).GetProperty("reason").GetString()!,
            StringComparison.Ordinal);
        Assert.Single(fixture.Conduct());
    }

    [Xunit.Theory]
    [Xunit.InlineData(ConductorHoldOwner.BackgroundAttempt)]
    [Xunit.InlineData(ConductorHoldOwner.DurableOutbox)]
    public void OwnedHold_PastThreshold_ProducesNeitherEscalationChannel(ConductorHoldOwner owner)
    {
        using var fixture = new EscalationFixture();
        var held = new ConductorAdvanceOutcome.Held(GoalLifecycleState.Verified, "owned progress") { Owner = owner };
        fixture.Observe(held, 0);
        fixture.Observe(held, 11);
        Assert.Null(fixture.Goal.CurrentHold);
        Assert.Empty(fixture.Lines);
        Assert.Empty(fixture.Timeline());
        Assert.Empty(fixture.Conduct());
    }

    [Xunit.Fact]
    public void OwnerlessHold_ConductWriteFails_StillWritesTimelineAndStall()
    {
        using var fixture = new EscalationFixture(failConduct: true);
        var held = new ConductorAdvanceOutcome.Held(GoalLifecycleState.Verified, "waiting for an owner");
        fixture.Observe(held, 0);
        fixture.Observe(held, 11);
        fixture.Observe(held, 12);
        Assert.Single(fixture.Timeline());
        Assert.Empty(fixture.Conduct());
        Assert.Single(fixture.Lines, line => line.StartsWith("GOAL_STALLED ", StringComparison.Ordinal));
    }

    [Xunit.Fact]
    public void OwnerlessHold_TimelineWriteFails_StillWritesConductAndStall()
    {
        using var fixture = new EscalationFixture(failTimeline: true);
        var held = new ConductorAdvanceOutcome.Held(GoalLifecycleState.Verified, "waiting for an owner");
        fixture.Observe(held, 0);
        fixture.Observe(held, 11);
        fixture.Observe(held, 12);
        Assert.Single(fixture.Conduct());
        Assert.Single(fixture.Lines, line => line.StartsWith("GOAL_STALLED ", StringComparison.Ordinal));
    }

    private sealed class EscalationFixture : IDisposable
    {
        private static readonly DateTimeOffset Start = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
        private readonly string _root = Path.Combine(Path.GetTempPath(), "ownerless-stall-" + Guid.NewGuid().ToString("N"));
        private readonly AgentOrchestratorKernel _kernel;
        private readonly ConductorDriver _driver;
        private readonly ConductEventLogWriter _conduct;
        private readonly GoalLifecycleEventWriter _timeline;
        private readonly HashSet<GoalId> _changed = [];
        private int _attentionEscalationCalls;
        internal Goal Goal { get; }
        internal List<string> Lines { get; } = [];

        internal EscalationFixture(bool failConduct = false, bool failTimeline = false)
        {
            (_kernel, Goal) = SimpleGoal();
            _driver = MakeDriver(writeEscalation: (_, _, _) => _attentionEscalationCalls++);
            Directory.CreateDirectory(_root);
            var eventsDirectory = Path.Combine(_root, "timeline");
            if (failTimeline) File.WriteAllText(eventsDirectory, "block directory creation");
            _timeline = new GoalLifecycleEventWriter(eventsDirectory, new FixedClock(),
                sessionRetentionOptions: new DispatchProviderSessionRetentionOptions(TimeSpan.Zero));
            _driver.HoldEscalationEventWriter = _timeline;
            _conduct = new ConductEventLogWriter(Path.Combine(_root, "conduct.log"), utcNow: () => Start,
                beforeAppendCommit: () =>
                {
                    if (failConduct) throw new IOException("forced conduct append failure");
                });
        }

        internal void Observe(ConductorAdvanceOutcome outcome, int minutes)
        {
            ConductorBatchLoop.TrackGoalOutcome(_kernel, _driver, Goal, outcome, Start.AddMinutes(minutes),
                TimeSpan.FromMinutes(10), _changed, Lines, _conduct);
            Assert.Equal(0, _attentionEscalationCalls);
        }

        internal List<JsonElement> Timeline() => ReadEvents(_timeline.EventFilePath(Goal.Id), "eventType", "GoalEscalated");
        internal List<JsonElement> Conduct() => ReadEvents(_conduct.CurrentPath, "eventKind", "goal-escalation");

        private static List<JsonElement> ReadEvents(string path, string key, string value)
        {
            if (!File.Exists(path)) return [];
            var events = new List<JsonElement>();
            foreach (var line in File.ReadAllLines(path))
            {
                using var document = JsonDocument.Parse(line);
                if (document.RootElement.GetProperty(key).GetString() == value)
                    events.Add(document.RootElement.Clone());
            }
            return events;
        }

        public void Dispose() => Directory.Delete(_root, recursive: true);

        private sealed class FixedClock : IClock
        {
            public DateTimeOffset UtcNow => Start;
        }
    }
}
