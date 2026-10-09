using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using static ConductorDriverTests;

// Parallel-safe: process liveness/launch and build slots are seamed; all state and files are private.
public sealed class FindingEvidenceRecoveryHoldOwnerTests
{
    [Fact(Timeout = 30_000)]
    [Trait("Category", "CrossTick")]
    public void Recovery_StartedAndRunningEvidencePastThreshold_ClearsHold()
    {
        using var fixture = new EvidenceFixture(buildSlotsBusy: false);
        var started = fixture.Advance();
        Assert.Equal(ConductorHoldOwner.BackgroundAttempt, started.Owner);
        Assert.Contains("Background finding-requested focused evidence is running in attempt", started.Reason,
            StringComparison.Ordinal);
        fixture.Observe(started, 0);

        var running = fixture.Advance(minutes: 11);
        Assert.Equal(ConductorHoldOwner.BackgroundAttempt, running.Owner);
        Assert.Equal(started.Reason, running.Reason);
        fixture.Observe(running, 11);
        fixture.Observe(running, 22);
        Assert.Equal(1, fixture.Launches);
        Assert.Equal(0, fixture.Runs);
        Assert.Null(fixture.Goal.CurrentHold);
        Assert.Empty(fixture.Escalations());
        Assert.Empty(fixture.ConductEscalations());
        Assert.Empty(fixture.Lines);
    }

    [Fact]
    public void Recovery_BuildSlotsBusyPastThreshold_KeepsOwnerlessEscalation()
    {
        using var fixture = new EvidenceFixture(buildSlotsBusy: true);
        var held = fixture.Advance();
        Assert.Equal(ConductorHoldOwner.None, held.Owner);
        Assert.Contains("Background finding-requested focused evidence did not run", held.Reason, StringComparison.Ordinal);
        Assert.Equal(1, fixture.Runs);
        Assert.Equal(0, fixture.Launches);
        fixture.Observe(held, 0);
        fixture.Observe(held, 11);
        fixture.Observe(held, 22);
        Assert.NotNull(fixture.Goal.CurrentHold?.StalledAt);
        Assert.Contains("ownerless-hold-stalled", Assert.Single(fixture.Escalations()).GetProperty("reason").GetString()!,
            StringComparison.Ordinal);
        Assert.Contains("ownerless-hold-stalled", Assert.Single(fixture.ConductEscalations()).GetProperty("detail").GetString()!,
            StringComparison.Ordinal);
        Assert.Single(fixture.Lines, line => line.StartsWith("GOAL_STALLED ", StringComparison.Ordinal));
    }

    private sealed class EvidenceFixture : IDisposable
    {
        private static readonly DateTimeOffset Start = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
        private readonly string _root = ConductorDriverTests.CreateTempDirectory();
        private readonly AgentOrchestratorKernel _kernel;
        private readonly ConductorDriver _driver;
        private readonly ConductEventLogWriter _conduct;
        private readonly GoalLifecycleEventWriter _timeline;
        private readonly HashSet<GoalId> _changed = [];
        private DateTimeOffset _now = Start;
        internal Goal Goal { get; }
        internal List<string> Lines { get; } = [];
        internal int Launches;
        internal int Runs;

        internal EvidenceFixture(bool buildSlotsBusy)
        {
            (_kernel, Goal) = SoftwareGoal();
            var developer = Goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
            var reviewer = Goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
            foreach (var task in Goal.Tasks.Where(task => task != reviewer))
                PassVerification(_kernel, Goal, task, hasCommittedChanges: task == developer);
            FailReviewerNeedsWork(_kernel, Goal, reviewer, "focused evidence required",
                findings: [EvidenceFindingWithRequest("Verify the finding", id: "hold-owner",
                    classes: [nameof(FindingEvidenceRecoveryHoldOwnerTests)])]);

            var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
                _root, Mcg.AgentOrchestrator.Infrastructure.TrunkBranchName.Default, runInline: buildSlotsBusy, utcNow: () => _now,
                isProcessAlive: _ => true,
                launchOwnedProcess: _ => new ConductorParallelAcceptanceOwnedProcessLaunchResult(7300 + ++Launches),
                acquireStableSlotLease: (_, _) => null);
            _driver = MakeDriver(
                getPreReviewEvidenceContext: _ => NoPreReviewContext("abc1234"),
                focusedEvidenceAttemptCoordinator: coordinator,
                executionDirectory: _root,
                getLandingFileScopes: _ => [],
                runFocusedEvidence: (_, _) =>
                {
                    Runs++;
                    if (!buildSlotsBusy) throw new InvalidOperationException("Live evidence must not run inline.");
                    throw new DotnetBuildSlotsBusyException(new DotnetBuildLeaseAcquisition.SlotsBusy("focused", []));
                },
                recordFindingEvidenceRequest: (goalId, taskId, message) =>
                    _kernel.RecordFindingEvidenceRequest(goalId, taskId, message));
            _conduct = new ConductEventLogWriter(Path.Combine(_root, "conduct.log"), utcNow: () => Start);
            _timeline = new GoalLifecycleEventWriter(Path.Combine(_root, "timeline"), new FixedClock(),
                sessionRetentionOptions: new DispatchProviderSessionRetentionOptions(TimeSpan.Zero));
            _driver.HoldEscalationEventWriter = _timeline;
        }

        internal ConductorAdvanceOutcome.Held Advance(int minutes = 0)
        {
            _now = Start.AddMinutes(minutes);
            var held = Assert.IsType<ConductorAdvanceOutcome.Held>(
                _driver.AdvanceOnce(Goal, ConductorAutonomyPolicy.Permissive).Outcome);
            Assert.Equal(GoalLifecycleState.Failed, held.State);
            return held;
        }

        internal void Observe(ConductorAdvanceOutcome.Held held, int minutes) =>
            ConductorBatchLoop.TrackGoalOutcome(_kernel, _driver, Goal, held, Start.AddMinutes(minutes),
                TimeSpan.FromMinutes(10), _changed, Lines, _conduct);

        internal List<JsonElement> Escalations() => ReadEvents(_timeline.EventFilePath(Goal.Id), "eventType", "GoalEscalated");
        internal List<JsonElement> ConductEscalations() => ReadEvents(_conduct.CurrentPath, "eventKind", "goal-escalation");
        public void Dispose() => Directory.Delete(_root, recursive: true);
        private sealed class FixedClock : IClock
        {
            public DateTimeOffset UtcNow => Start;
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
