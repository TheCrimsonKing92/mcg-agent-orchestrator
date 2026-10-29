using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

// Parallel-safe: all state is owned by the individual test kernel.
public sealed class ConductorLandingBlockerRecorderTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.UnixEpoch;

    [Fact]
    public void Observe_EscalatedPostPassLanding_PreservesFullReasonAndSinceUntilExecution()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("post-pass landing blocker");
        HashSet<GoalId> changed = [];
        const string reason = "acceptance passed at 62f41de0; landing held\r\ngate test obligation: selector-not-found";
        var outcome = new ConductorAdvanceOutcome.Escalated(GoalLifecycleState.Verified, reason)
        {
            Decision = new PolicyDecisionRecord("landing", "escalate", 2, "acceptance-not-accepted", reason, [])
        };

        ConductorLandingBlockerRecorder.Observe(kernel, goal, outcome, Start, changed);

        Assert.Equal(new GoalLandingBlocker("landing/landing-held-after-pass", reason,
            "landing/landing-held-after-pass:62f41de0", Start, "unmapped", "unmapped"), goal.LandingBlocker);
        Assert.Equal(goal.Id, Assert.Single(changed));
        changed.Clear();
        ConductorLandingBlockerRecorder.Observe(kernel, goal, outcome, Start.AddMinutes(10), changed);
        Assert.Empty(changed);
        Assert.Equal(Start, goal.LandingBlocker!.Since);

        ConductorLandingBlockerRecorder.Observe(kernel, goal,
            new ConductorAdvanceOutcome.Executed(GoalLifecycleState.Verified, "landed"), Start.AddMinutes(20), changed);
        Assert.Null(goal.LandingBlocker);
        Assert.Equal(goal.Id, Assert.Single(changed));
    }

    [Theory]
    [InlineData("no passed acceptance outcome for candidate 62f41de0", "landing", "acceptance-not-accepted", GoalLifecycleState.Verified)]
    [InlineData("acceptance passed at 62f41de0", "other", "acceptance-not-accepted", GoalLifecycleState.Verified)]
    [InlineData("acceptance passed at 62f41de0", "landing", "other", GoalLifecycleState.Verified)]
    [InlineData("acceptance passed at 62f41de0", "landing", "acceptance-not-accepted", GoalLifecycleState.WorkspaceReady)]
    public void Observe_UnrelatedEscalation_ClearsBlocker(string reason, string stage, string evidence, GoalLifecycleState state)
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("unrelated escalation");
        ConductorLandingBlockerRecorder.Observe(kernel, goal, TypedHold("criterion-evidence-hold"), Start, []);
        Assert.NotNull(goal.LandingBlocker);
        HashSet<GoalId> changed = [];

        ConductorLandingBlockerRecorder.Observe(kernel, goal,
            new ConductorAdvanceOutcome.Escalated(state, reason)
            {
                Decision = new PolicyDecisionRecord(stage, "escalate", 2, evidence, reason, [])
            }, Start.AddMinutes(1), changed);

        Assert.Null(goal.LandingBlocker);
        Assert.Equal(goal.Id, Assert.Single(changed));
    }

    [Fact]
    public void Observe_TypedHold_PreservesSinceWithoutRepeatedWritesAndClearsOnExecution()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("record landing blocker");
        HashSet<GoalId> changed = [];
        var held = TypedHold("evidence-mutation-lease-hold") with
        {
            Reason = "lease\r\nunavailable", StableIdentity = "lease-1"
        };

        ConductorLandingBlockerRecorder.Observe(kernel, goal, held, Start, changed);

        Assert.Equal(new GoalLandingBlocker("verified-admission/evidence-mutation-lease-hold",
            "lease  unavailable", "verified-admission/evidence-mutation-lease-hold:lease-1",
            Start, "conductor", "next-tick"), goal.LandingBlocker);
        Assert.Equal(goal.Id, Assert.Single(changed));
        changed.Clear();

        ConductorLandingBlockerRecorder.Observe(kernel, goal, held, Start.AddMinutes(10), changed);
        Assert.Equal(Start, goal.LandingBlocker!.Since);
        Assert.Empty(changed);

        ConductorLandingBlockerRecorder.Observe(kernel, goal,
            new ConductorAdvanceOutcome.Executed(GoalLifecycleState.Verified, "landed"),
            Start.AddMinutes(11), changed);
        Assert.Null(goal.LandingBlocker);
        Assert.Equal(goal.Id, Assert.Single(changed));
    }

    [Fact]
    public void Observe_UntypedQueueHold_RecordsUnmappedNextAction()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("queue blocker");
        HashSet<GoalId> changed = [];
        ConductorLandingBlockerRecorder.Observe(kernel, goal,
            new ConductorAdvanceOutcome.Held(GoalLifecycleState.Verified, "queue")
                { Owner = ConductorHoldOwner.AcceptanceQueue }, Start, changed);

        Assert.Equal(new GoalLandingBlocker("untyped/AcceptanceQueue", "queue",
            "untyped/AcceptanceQueue:", Start, "unmapped", "unmapped"), goal.LandingBlocker);
        Assert.Equal(goal.Id, Assert.Single(changed));
    }

    [Fact]
    public void Observe_IneligibleOrMissingGoal_LeavesRecordAndChangedSetEmpty()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("negative controls");
        Assert.NotEqual(GoalStatus.Verified, goal.Status);
        HashSet<GoalId> changed = [];
        ConductorLandingBlockerRecorder.Observe(kernel, goal,
            new ConductorAdvanceOutcome.Executed(GoalLifecycleState.WorkspaceReady, "advanced"), Start, changed);
        Assert.Null(goal.LandingBlocker);
        Assert.Empty(changed);
        ConductorLandingBlockerRecorder.Observe(kernel, goal,
            new ConductorAdvanceOutcome.Held(GoalLifecycleState.WorkspaceReady, "unrelated"), Start, changed);
        Assert.Null(goal.LandingBlocker);
        Assert.Empty(changed);

        var absent = new AgentOrchestratorKernel().CreateGoal("absent");
        Assert.Null(Record.Exception(() => ConductorLandingBlockerRecorder.Observe(
            kernel, absent, TypedHold("criterion-evidence-hold"), Start, changed)));
        Assert.Null(absent.LandingBlocker);
        Assert.Empty(changed);
    }

    [Theory]
    [InlineData("criterion-evidence-hold", "evidence-owner", "criterion-evidence-record")]
    [InlineData("acceptance-apparatus-hold", "operator", "acceptance-retry")]
    [InlineData("environmental-apparatus-hold", "operator", "acceptance-retry")]
    [InlineData("replacement-evidence-mutation-lease", "conductor", "next-tick")]
    [InlineData("acceptance-width-admission", "conductor", "next-tick")]
    [InlineData("gate-start-infrastructure-deferred", "conductor", "next-tick")]
    [InlineData("gate-start-build-slots-busy", "conductor", "next-tick")]
    [InlineData("gate-start-build-lock-blocked", "conductor", "next-tick")]
    [InlineData("attempt-running-in-background", "conductor", "gate-reconciliation")]
    [InlineData("gate-start-attempt-cancelled", "unmapped", "unmapped")]
    [InlineData("Criterion-evidence-hold", "unmapped", "unmapped")]
    public void Observe_DecisionEvidence_MapsOnlyExactListedActions(string evidence, string actor, string verb)
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("mapping");
        ConductorLandingBlockerRecorder.Observe(kernel, goal, TypedHold(evidence), Start, []);
        Assert.Equal(actor, goal.LandingBlocker!.NextActor);
        Assert.Equal(verb, goal.LandingBlocker.NextVerb);
    }

    [Fact]
    public void Observe_StaleGoalInstance_MutatesAndReadsStatusFromKernelGoal()
    {
        var original = new AgentOrchestratorKernel();
        var stale = original.CreateGoal("authoritative status");
        var snapshot = original.ExportSnapshot();
        var kernel = AgentOrchestratorKernel.FromSnapshot(snapshot with
        {
            Goals = [snapshot.Goals.Single() with { Status = GoalStatus.Verified }]
        });
        HashSet<GoalId> changed = [];
        ConductorLandingBlockerRecorder.Observe(kernel, stale,
            new ConductorAdvanceOutcome.Held(GoalLifecycleState.WorkspaceReady, "held"), Start, changed);
        Assert.NotNull(kernel.GetGoal(stale.Id).LandingBlocker);
        Assert.Null(stale.LandingBlocker);
        Assert.Equal(stale.Id, Assert.Single(changed));
    }

    [Fact]
    public void ObserveLandingBlocker_DetailOrIdentityChanges_UsesContinuityContract()
    {
        var goal = new AgentOrchestratorKernel().CreateGoal("continuity");
        var first = new GoalLandingBlocker("kind", "detail", "identity", Start, "actor", "verb");
        Assert.True(goal.ObserveLandingBlocker(first));
        Assert.True(goal.ObserveLandingBlocker(first with { Detail = "new detail", Since = Start.AddMinutes(1) }));
        Assert.Equal(Start, goal.LandingBlocker!.Since);
        Assert.False(goal.ObserveLandingBlocker(goal.LandingBlocker with
            { NextActor = "new actor", NextVerb = "new verb", Since = Start.AddMinutes(2) }));
        Assert.Equal("new actor", goal.LandingBlocker.NextActor);
        Assert.Equal("new verb", goal.LandingBlocker.NextVerb);
        Assert.Equal(Start, goal.LandingBlocker.Since);
        Assert.True(goal.ObserveLandingBlocker(first with { Identity = "Identity", Since = Start.AddMinutes(3) }));
        Assert.Equal(Start.AddMinutes(3), goal.LandingBlocker.Since);
        Assert.True(goal.ObserveLandingBlocker(first with { Kind = "Kind", Since = Start.AddMinutes(4) }));
        Assert.Equal(Start.AddMinutes(4), goal.LandingBlocker.Since);
        Assert.True(goal.ClearLandingBlocker());
        Assert.False(goal.ClearLandingBlocker());
    }

    private static ConductorAdvanceOutcome.Held TypedHold(string evidence) =>
        new(GoalLifecycleState.Verified, "held")
        {
            Decision = new PolicyDecisionRecord("verified-admission", "hold", 4, evidence, "held", [])
        };
}
