using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using static ConductorDriverTests;

// Every test owns its journal and uses injected effects; no processes or git are invoked.
public sealed class ConductorDriverTestsVerifiedAdmissionDecision
{
    private const string Candidate = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Fingerprint = "v1:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";

    [Fact]
    public void ActiveOwnerReviewHoldCarriesAdmissionDecisionWithoutRegating()
    {
        using var fixture = new OwnerFixture();
        var initial = Assert.IsType<ConductorAdvanceOutcome.Held>(fixture.Advance().Outcome);
        Assert.Null(initial.Decision); // The post-gate entry stays outside verified admission.
        fixture.Driver = fixture.CreateDriver();
        var held = Assert.IsType<ConductorAdvanceOutcome.Held>(fixture.Advance().Outcome);
        Assert.Equal(1, fixture.Gates);
        Assert.Equal(GoalStatus.Verified, fixture.Goal.Status);
        Assert.Equal(Assert.Single(fixture.Escalations), held.Reason);
        AssertHeld(held, initial.Reason, $"owner-review-hold:{Candidate}:{Fingerprint}");
        var decision = AssertDecisionAndPayload(held, 1, "owner-review-hold");
        AssertFact(decision, "goalStatus", "Verified");
        AssertFact(decision, "ownerReviewHold", "true");
        AssertFact(decision, "ownerReviewSha", Candidate);
        AssertFact(decision, "ownerReviewReason", initial.Reason);
        AssertFact(decision, "ownerReviewFingerprint", Fingerprint);
        AssertFact(decision, "cohortAttribution", "");
    }

    [Fact]
    public void VerifiedGoalWithMissingTaskVerificationCarriesPrecheckDecision()
    {
        var (kernel, original) = SimpleGoal();
        PassVerification(kernel, original, original.Tasks.Single());
        var snapshot = kernel.ExportSnapshot();
        var goalSnapshot = Assert.Single(snapshot.Goals);
        // Restore a persisted Verified row whose completed task has no verification receipt.
        var restored = AgentOrchestratorKernel.FromSnapshot(snapshot with
        {
            Goals = [goalSnapshot with
            {
                Tasks = goalSnapshot.Tasks.Select(task => task with { LastVerification = null, VerificationHistory = [] }).ToArray()
            }]
        });
        var goal = restored.GetGoal(original.Id);
        Assert.Equal(GoalStatus.Verified, goal.Status);
        Assert.Equal(WorkTaskStatus.Completed, Assert.Single(goal.Tasks).Status);
        Assert.False(AcceptancePrecheck.HasCompletedPassedVerificationForAllTasks(goal));
        var gateRuns = 0;
        var driver = MakeDriver(runAcceptanceSummary: _ =>
        {
            gateRuns++;
            return AcceptanceVerificationSummary.PassedWithNoUnmetCriteria;
        });
        var held = Assert.IsType<ConductorAdvanceOutcome.Held>(driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative).Outcome);
        AssertHeld(held, "Goal is not ready for acceptance: complete every task with a passed verification before accepting this gate.", null);
        Assert.Equal(0, gateRuns);
        var decision = AssertDecisionAndPayload(held, 5, "task-verification-precheck");
        AssertFact(decision, "ownerReviewHold", "false");
        AssertFact(decision, "cohortAttribution", "false");
        AssertFact(decision, "apparatusHold", "false");
        AssertFact(decision, "evidenceLease", "true");
        AssertFact(decision, "allTasksPassed", "false");
    }

    [Fact]
    public void BusyBuildSlotsCarriesAdmissionDecisionAndOriginalHold()
    {
        var (kernel, goal) = SimpleGoal();
        PassVerification(kernel, goal, goal.Tasks.Single());
        var gateRuns = 0;
        var driver = MakeDriver(runAcceptanceSummary: _ =>
        {
            gateRuns++;
            throw new DotnetBuildSlotsBusyException(new DotnetBuildLeaseAcquisition.SlotsBusy(
                "goal-slots-busy", [new DotnetBuildStableSlotWait(0, 12345)]));
        });
        var held = Assert.IsType<ConductorAdvanceOutcome.Held>(driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative).Outcome);
        AssertHeld(held, "Stable dotnet build slots busy; retry on next conduct tick. wanted-by=goal-slots-busy; busy slots: slot-0 pid 12345", null);
        Assert.Equal(1, gateRuns);
        Assert.Equal(GoalStatus.Verified, goal.Status);
        var decision = AssertDecisionAndPayload(held, 7, "gate-start-build-slots-busy");
        AssertFact(decision, "ownerReviewHold", "false");
        AssertFact(decision, "cohortAttribution", "false");
        AssertFact(decision, "apparatusHold", "false");
        AssertFact(decision, "evidenceLease", "true");
        AssertFact(decision, "allTasksPassed", "true");
        AssertFact(decision, "gateStartDeferral", "build-slots-busy");
        AssertFact(decision, "buildSlotsBusyDetail", "wanted-by=goal-slots-busy; busy slots: slot-0 pid 12345");
    }

    private static void AssertHeld(ConductorAdvanceOutcome.Held held, string reason, string? identity)
    {
        Assert.Equal(reason, held.Reason);
        Assert.Equal(GoalLifecycleState.Verified, held.State);
        Assert.Equal(identity, held.StableIdentity);
        Assert.Equal(ConductorHoldOwner.None, held.Owner);
    }

    internal static PolicyDecisionRecord AssertDecisionAndPayload(ConductorAdvanceOutcome outcome, int rung, string evidence)
    {
        var decision = Assert.IsType<PolicyDecisionRecord>(outcome switch
        {
            ConductorAdvanceOutcome.Held held => held.Decision,
            ConductorAdvanceOutcome.Escalated escalated => escalated.Decision,
            _ => throw new InvalidOperationException("Expected a held or escalated outcome.")
        });
        Assert.Equal("verified-admission", decision.Stage);
        Assert.Equal(outcome is ConductorAdvanceOutcome.Held ? "Hold" : "Escalate", decision.Action);
        Assert.Equal(rung, decision.Rung);
        Assert.Equal(evidence, decision.DiscriminatingEvidence);
        var payload = VerifiedAcceptanceEscalationDecision.BuildTickOutcomePayload(outcome);
        Assert.Equal(outcome is ConductorAdvanceOutcome.Held ? "Held" : "Escalated", payload.OutcomeKind);
        Assert.Equal(outcome is ConductorAdvanceOutcome.Held ? "Verified" : "AcceptanceFailed", payload.LifecycleState);
        Assert.Equal(outcome is ConductorAdvanceOutcome.Held ? null : "AcceptanceVerificationFailed", payload.EscalationKind);
        var copied = Assert.IsType<PolicyDecisionRecord>(payload.Decision);
        Assert.Equal(decision.Stage, copied.Stage);
        Assert.Equal(decision.Action, copied.Action);
        Assert.Equal(decision.Rung, copied.Rung);
        Assert.Equal(decision.DiscriminatingEvidence, copied.DiscriminatingEvidence);
        var reason = outcome is ConductorAdvanceOutcome.Held h ? h.Reason : ((ConductorAdvanceOutcome.Escalated)outcome).Reason;
        Assert.Equal(reason, decision.Reason);
        Assert.Equal(decision.Reason, copied.Reason);
        Assert.Equal(20, decision.Facts.Count);
        Assert.Equal(decision.Facts.Count, copied.Facts.Count);
        for (var index = 0; index < decision.Facts.Count; index++)
        {
            Assert.Equal(decision.Facts[index].Name, copied.Facts[index].Name);
            Assert.Equal(decision.Facts[index].Value, copied.Facts[index].Value);
        }
        return decision;
    }

    internal static void AssertFact(PolicyDecisionRecord decision, string name, string value) =>
        Assert.Equal(value, Assert.Single(decision.Facts, fact => fact.Name == name).Value);

    private sealed class OwnerFixture : IDisposable
    {
        private readonly string _root = ConductorDriverTests.CreateTempDirectory();
        private readonly AgentOrchestratorKernel _kernel;
        private readonly ICollaborationItemStore _decisions;
        internal readonly Goal Goal;
        internal readonly List<string> Escalations = [];
        internal ConductorDriver Driver;
        internal int Gates;

        internal OwnerFixture()
        {
            (_kernel, Goal) = SimpleGoal();
            PassVerification(_kernel, Goal, Goal.Tasks.Single());
            _decisions = CollaborationItemStore.ForDirectory(Path.Combine(_root, ".orchestrator"));
            Driver = CreateDriver();
        }

        internal ConductorDriver CreateDriver()
        {
            var driver = MakeDriver(runAcceptanceSummary: _ =>
            {
                Gates++;
                return new AcceptanceVerificationSummary(false,
                    [new AcceptanceCheckResult("owner-protected configuration", false, 1,
                        "config/acceptance-manifest.json: changed field(s) engine.maxParallelShards; owner decision required for this candidate",
                        ResultSummary: "operator review required")],
                    FailedChecks: ["owner-protected configuration"], BranchHeadSha: Candidate, MainHeadSha: "main");
            }, recordAcceptanceFailure: (goal, checks, branch, main, attributions, baseline) =>
                _kernel.RecordAcceptanceFailure(goal.Id, checks, branch, main, attributions, baseline),
                resolveAcceptanceHeads: _ => (Candidate, "main"), executionDirectory: _root);
            driver.OverrideOwnerReviewHoldForTests(_decisions, (_, text) => Escalations.Add(text), (_, _) => Fingerprint, _kernel);
            return driver;
        }

        internal ConductorAdvanceResult Advance() => Driver.AdvanceOnce(Goal, ConductorAutonomyPolicy.Conservative);
        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
