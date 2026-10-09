using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using static ConductorDriverTests;

// Process liveness and build slots are seamed; all artifacts and clocks belong to this fixture.
[Xunit.Collection("IsolatedProcessSpawning")]
public sealed class ConductorDriverTestsPreReviewHoldOwner
{
    [Fact]
    public void InFlightPreReviewEvidence_HeldCarriesBackgroundAttemptOwner()
    {
        using var fixture = new EvidenceFixture();
        var started = fixture.Advance();
        Assert.StartsWith("PRE_REVIEW_FOCUSED_EVIDENCE_RUNNING", started.Reason, StringComparison.Ordinal);
        Assert.Equal(ConductorHoldOwner.BackgroundAttempt, started.Owner);
        Assert.Equal($"pre-review-evidence:{fixture.Handle.Attempt.AttemptId}", started.StableIdentity);

        var running = fixture.Advance();
        Assert.StartsWith("PRE_REVIEW_FOCUSED_EVIDENCE_RUNNING", running.Reason, StringComparison.Ordinal);
        Assert.Equal(ConductorHoldOwner.BackgroundAttempt, running.Owner);
        Assert.Equal(started.StableIdentity, running.StableIdentity);
        Assert.Equal(0, fixture.Runs);
        Assert.Equal(0, fixture.Dispatches);
    }

    [Fact]
    [Trait("Category", "CrossTick")]
    public void InFlightPreReviewEvidence_PastStallThreshold_ClearsHoldWithoutEscalation()
    {
        using var fixture = new EvidenceFixture();
        var held = fixture.Advance();
        Assert.StartsWith("PRE_REVIEW_FOCUSED_EVIDENCE_RUNNING", held.Reason, StringComparison.Ordinal);
        foreach (var minutes in new[] { 0, 11, 22 })
        {
            fixture.Observe(held, minutes);
            Assert.Null(fixture.Goal.CurrentHold);
        }

        Assert.Empty(fixture.Escalations());
        Assert.Empty(fixture.ConductEscalations());
        Assert.DoesNotContain(fixture.Lines,
            line => line.StartsWith("GOAL_STALLED ", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildSlotsBusyPreReviewEvidence_DidNotRunHeldKeepsNoOwner()
    {
        using var fixture = new EvidenceFixture(buildSlotsBusy: true);
        var started = fixture.Advance();
        Assert.Equal(ConductorHoldOwner.BackgroundAttempt, started.Owner);
        fixture.Handle.CompleteForTests();

        var held = fixture.Advance();
        Assert.Contains("did not run (BlockedBuildSlot)", held.Reason, StringComparison.Ordinal);
        Assert.Equal(ConductorHoldOwner.None, held.Owner);
        Assert.Null(fixture.Reviewer.PreReviewEvidenceReceipt);
        Assert.Equal(0, fixture.Runs);
        Assert.Equal(0, fixture.Dispatches);
    }

    private sealed class EvidenceFixture : IDisposable
    {
        private static readonly DateTimeOffset Start = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
        private readonly string _root = ConductorDriverTests.CreateTempDirectory();
        private readonly AgentOrchestratorKernel _kernel;
        private readonly ConductorDriver _driver;
        private readonly ConductorParallelAcceptanceAttemptCompletionGateForTests _gate = new();
        private readonly ConductEventLogWriter _conduct;
        private readonly GoalLifecycleEventWriter _timeline;
        private readonly HashSet<GoalId> _changed = [];
        internal Goal Goal { get; }
        internal TaskSpec Reviewer { get; }
        internal List<string> Lines { get; } = [];
        internal int Runs;
        internal int Dispatches;
        internal ConductorParallelAcceptanceAttemptCompletionGateForTests.Handle Handle =>
            _gate.RequiredHandleForTests(Goal.Id.Value);

        internal EvidenceFixture(bool buildSlotsBusy = false)
        {
            (_kernel, Goal) = SoftwareGoal();
            Reviewer = Goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
            foreach (var task in Goal.Tasks.TakeWhile(task => task.Id != Reviewer.Id))
                PassVerification(_kernel, Goal, task);

            var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
                Path.Combine(_root, "pre-review-evidence-attempts"), Mcg.AgentOrchestrator.Infrastructure.TrunkBranchName.Default,
                executionDirectory: _root,
                utcNow: () => Start,
                isProcessAlive: _ => true,
                attemptCompletionGateForTests: _gate,
                acquireStableSlotLease: (_, _) => buildSlotsBusy
                    ? throw new DotnetBuildSlotsBusyException(
                        new DotnetBuildLeaseAcquisition.SlotsBusy("pre-review-evidence", []))
                    : null);
            _driver = MakeDriver(
                getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                getPreReviewEvidenceContext: _ => FocusedPreReviewContext("hold-owner-sha"),
                runFocusedEvidence: (_, request) =>
                {
                    Runs++;
                    return PassingPreReviewEvidence(request);
                },
                recordPreReviewEvidence: (goalId, taskId, receipt) =>
                    _kernel.RecordPreReviewEvidence(goalId, taskId, receipt),
                dispatchAndStart: _ =>
                {
                    Dispatches++;
                    return DispatchStartOutcome.Started();
                },
                focusedEvidenceAttemptCoordinator: coordinator,
                executionDirectory: _root);
            _conduct = new ConductEventLogWriter(Path.Combine(_root, "conduct.log"), utcNow: () => Start);
            _timeline = new GoalLifecycleEventWriter(Path.Combine(_root, "timeline"), new FixedClock(),
                sessionRetentionOptions: new DispatchProviderSessionRetentionOptions(TimeSpan.Zero));
            _driver.HoldEscalationEventWriter = _timeline;
        }

        internal ConductorAdvanceOutcome.Held Advance()
        {
            var held = Assert.IsType<ConductorAdvanceOutcome.Held>(
                _driver.AdvanceOnce(Goal, ConductorAutonomyPolicy.Permissive).Outcome);
            Assert.Equal(GoalLifecycleState.WorkspaceReady, held.State);
            return held;
        }

        internal void Observe(ConductorAdvanceOutcome.Held held, int minutes) =>
            ConductorBatchLoop.TrackGoalOutcome(_kernel, _driver, Goal, held, Start.AddMinutes(minutes),
                TimeSpan.FromMinutes(10), _changed, Lines, _conduct);

        internal List<JsonElement> Escalations() =>
            ReadEvents(_timeline.EventFilePath(Goal.Id), "eventType", "GoalEscalated");
        internal List<JsonElement> ConductEscalations() =>
            ReadEvents(_conduct.CurrentPath, "eventKind", "goal-escalation");
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
