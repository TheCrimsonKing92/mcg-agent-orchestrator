using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using static ConductorDriverTests;

// Parallel-safe: each case owns its kernel, injected clock, writers and temporary root.
public sealed class DependencyHoldOwnershipTests
{
    [Theory(Timeout = 30_000)]
    [Trait("Category", "CrossTick")]
    [InlineData(GoalStatus.Draft)]
    [InlineData(GoalStatus.Active)]
    [InlineData(GoalStatus.Verifying)]
    [InlineData(GoalStatus.Verified)]
    [InlineData(GoalStatus.Completed)]
    [InlineData(GoalStatus.AcceptanceFailed)]
    public void Tick_ProgressingDependencyPastThreshold_ClearsHold(GoalStatus status)
    {
        using var fixture = new DependencyFixture(status);
        fixture.Run();
        Assert.Null(fixture.Goal.CurrentHold);
        Assert.Empty(fixture.Events("goal-escalation"));
        Assert.Empty(fixture.Events("goal-stalled"));
        Assert.Empty(fixture.Escalations());
        Assert.DoesNotContain(fixture.Lines, line => line.StartsWith("GOAL_STALLED ", StringComparison.Ordinal));
    }

    [Theory(Timeout = 30_000)]
    [Trait("Category", "CrossTick")]
    [InlineData(GoalStatus.Parked)]
    [InlineData(GoalStatus.WaitingForHuman)]
    [InlineData(null)]
    public void Tick_HumanOrUnknownDependencyPastThreshold_EscalatesOnce(GoalStatus? status)
    {
        using var fixture = new DependencyFixture(status);
        fixture.Run();
        Assert.NotNull(fixture.Goal.CurrentHold?.StalledAt);
        var escalation = Assert.Single(fixture.Events("goal-escalation"));
        Assert.Contains("ownerless-hold-stalled", escalation.GetProperty("detail").GetString()!, StringComparison.Ordinal);
        Assert.Contains(fixture.Blocker, escalation.GetProperty("detail").GetString()!, StringComparison.Ordinal);
        Assert.Contains("ownerless-hold-stalled", Assert.Single(fixture.Escalations()).GetProperty("reason").GetString()!,
            StringComparison.Ordinal);
        Assert.Single(fixture.Events("goal-stalled"));
        Assert.Contains(fixture.Lines, line => line.StartsWith("GOAL_STALLED ", StringComparison.Ordinal));
    }

    [Fact(Timeout = 30_000)]
    [Trait("Category", "CrossTick")]
    public void Tick_DependencyResumes_ClearsPreviouslyTrackedHold()
    {
        using var fixture = new DependencyFixture(GoalStatus.Parked);
        fixture.Run(ticks: 1);
        Assert.NotNull(fixture.Goal.CurrentHold);
        fixture.SetStatus(GoalStatus.Active);
        fixture.Now = fixture.Now.AddMinutes(11);
        fixture.Run(ticks: 1);
        Assert.Null(fixture.Goal.CurrentHold);
        Assert.Empty(fixture.Events("goal-escalation"));
        Assert.Empty(fixture.Escalations());
    }

    private sealed class DependencyFixture : IDisposable
    {
        private readonly string _root = ConductorDriverTests.CreateTempDirectory();
        private readonly AgentOrchestratorKernel _kernel;
        private readonly GoalId _dependency = GoalId.New();
        private readonly ConductorDriver _driver;
        private readonly ConductEventLogWriter _conduct;
        private readonly GoalLifecycleEventWriter _timeline;
        internal DateTimeOffset Now = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
        internal Goal Goal { get; }
        internal string Blocker => $"waiting on dependency {_dependency.Value[..8]}";
        internal List<string> Lines { get; } = [];

        internal DependencyFixture(GoalStatus? status)
        {
            var (kernel, goal) = SimpleGoal();
            var snapshot = kernel.ExportSnapshot();
            kernel.ReplaceWithSnapshot(snapshot with
            {
                Goals = snapshot.Goals.Select(item => item.Id == goal.Id.Value
                    ? item with { DependsOn = [_dependency.Value] } : item).ToArray()
            });
            _kernel = kernel;
            Goal = kernel.GetGoal(goal.Id);
            if (status is { } known) SetStatus(known);
            _driver = MakeDriver(getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true));
            _conduct = new ConductEventLogWriter(Path.Combine(_root, "conduct.log"), utcNow: () => Now);
            _timeline = new GoalLifecycleEventWriter(Path.Combine(_root, "timeline"), new FixtureClock(this),
                sessionRetentionOptions: new DispatchProviderSessionRetentionOptions(TimeSpan.Zero));
            _driver.HoldEscalationEventWriter = _timeline;
        }

        internal void SetStatus(GoalStatus status) => _kernel.MarkKnownDependencyGoalStatuses(
            [new KeyValuePair<GoalId, string>(_dependency, status.ToString())]);

        internal void Run(int ticks = 3)
        {
            var summary = new ConductorBatchLoop(conductEventLogWriter: _conduct, utcNow: () => Now).Run(
                _kernel, _driver, ConductorAutonomyPolicy.Conservative, Path.Combine(_root, "stop"),
                maxIterations: ticks, watchInterval: TimeSpan.FromSeconds(1),
                onTick: tick => Lines.AddRange(tick.ProgressLines ?? []),
                sleepFunc: _ => { Now = Now.AddMinutes(11); return false; },
                goalStallThreshold: TimeSpan.FromMinutes(10));
            Assert.Equal(ticks, summary.Ticks);
            Assert.True(summary.Held >= 1);
            Assert.Contains(Goal.Timeline, item => item.Message.Contains(Blocker, StringComparison.Ordinal));
        }

        internal List<JsonElement> Events(string kind) => ReadEvents(_conduct.CurrentPath, "eventKind", kind);
        internal List<JsonElement> Escalations() => ReadEvents(_timeline.EventFilePath(Goal.Id), "eventType", "GoalEscalated");
        public void Dispose() => Directory.Delete(_root, recursive: true);

        private sealed class FixtureClock(DependencyFixture fixture) : IClock
        {
            public DateTimeOffset UtcNow => fixture.Now;
        }
    }

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
}
