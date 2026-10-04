using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.DotnetBuildSlots)]
public sealed class ConductorBatchLoopTestsVerifyingStageDecision : ConductorBatchLoopTests
{
    public ConductorBatchLoopTestsVerifyingStageDecision(ITestOutputHelper output) : base(output)
    {
    }

    [Xunit.Fact]
    public void Verifying_OutsideTick_AttachesConductLoopDecisionAndPayload()
    {
        using var fixture = new AdmissionFixture();
        var goal = fixture.CreateVerifyingGoal();

        var result = fixture.Driver.AdvanceOnce(goal, WidthOne);

        var held = Assert.IsType<ConductorAdvanceOutcome.Held>(result.Outcome);
        Assert.Equal(GoalLifecycleState.Verifying, held.State);
        Assert.Equal(ConductorHoldOwner.None, held.Owner);
        Assert.Equal("Acceptance gate is owned by the conduct loop; reconciliation will handle terminal artifact", held.Reason);
        Assert.Equal(GoalStatus.Verifying, goal.Status);
        Assert.Empty(fixture.Attempts(goal));
        Assert.Equal(0, fixture.LaunchCount);
        var decision = AssertDecision(held, 1, "conduct-loop-owned");
        AssertFacts(decision, "false", "", "", "", "", "");
        AssertPayload(held, decision);
    }

    [Xunit.Fact]
    public void Verifying_WidthRefused_AttachesAdmissionDecisionAndPayload()
    {
        using var fixture = new AdmissionFixture();
        var occupant = fixture.StartOccupant();
        var goal = fixture.CreateVerifyingGoal();
        fixture.Driver.BeginTick(fixture.Kernel, 1);
        var reason = $"acceptance width 1 reached; live acceptance occupants goal:{occupant.Id.Value[..8]}; retry on next conduct tick";

        var result = fixture.Driver.AdvanceOnce(goal, WidthOne);

        var held = Assert.IsType<ConductorAdvanceOutcome.Held>(result.Outcome);
        Assert.Equal(GoalLifecycleState.Verifying, held.State);
        Assert.Equal(ConductorHoldOwner.None, held.Owner);
        Assert.Equal(reason, held.Reason);
        Assert.Equal(GoalStatus.Verifying, goal.Status);
        Assert.Empty(fixture.Attempts(goal));
        Assert.Equal(1, fixture.LaunchCount);
        var decision = AssertDecision(held, 6, "acceptance-width-admission");
        AssertFacts(decision, "true", "true", "true", "true", "false", reason);
        AssertPayload(held, decision);
    }

    private static ConductorAutonomyPolicy WidthOne =>
        ConductorAutonomyPolicy.Conservative with { AcceptanceWidth = 1 };

    private static PolicyDecisionRecord AssertDecision(ConductorAdvanceOutcome.Held held, int rung, string evidence)
    {
        var decision = Assert.IsType<PolicyDecisionRecord>(held.Decision);
        Assert.Equal("verifying", decision.Stage);
        Assert.Equal("Hold", decision.Action);
        Assert.Equal(rung, decision.Rung);
        Assert.Equal(evidence, decision.DiscriminatingEvidence);
        Assert.Equal(held.Reason, decision.Reason);
        return decision;
    }

    private static void AssertFacts(PolicyDecisionRecord decision, string tick, string parallel,
        string candidate, string lease, string admission, string admissionReason)
    {
        Assert.Collection(decision.Facts,
            f => AssertFact(f, "callerState", "Verifying"),
            f => AssertFact(f, "conductorTick", tick),
            f => AssertFact(f, "parallelAcceptance", parallel),
            f => AssertFact(f, "candidate", candidate),
            f => AssertFact(f, "replacementLease", lease),
            f => AssertFact(f, "admission", admission),
            f => AssertFact(f, "artifactWriter", ""),
            f => AssertFact(f, "goalStatus", "Verifying"),
            f => AssertFact(f, "replacementLeaseReason", ""),
            f => AssertFact(f, "admissionReason", admissionReason),
            f => AssertFact(f, "artifactWriterMessage", ""),
            f => AssertFact(f, "attemptDecision", ""),
            f => AssertFact(f, "attemptId", ""),
            f => AssertFact(f, "noTickWait", ""));
    }

    private static void AssertFact(PolicyDecisionFact fact, string name, string value)
    {
        Assert.Equal(name, fact.Name);
        Assert.Equal(value, fact.Value);
    }

    private static void AssertPayload(ConductorAdvanceOutcome.Held held, PolicyDecisionRecord expected)
    {
        var payload = VerifiedAcceptanceEscalationDecision.BuildTickOutcomePayload(held);
        Assert.Equal("Held", payload.OutcomeKind);
        Assert.Equal("Verifying", payload.LifecycleState);
        Assert.Null(payload.EscalationKind);
        var decision = Assert.IsType<PolicyDecisionRecord>(payload.Decision);
        Assert.Equal(expected.Stage, decision.Stage);
        Assert.Equal(expected.Action, decision.Action);
        Assert.Equal(expected.Rung, decision.Rung);
        Assert.Equal(expected.DiscriminatingEvidence, decision.DiscriminatingEvidence);
        Assert.Equal(expected.Reason, decision.Reason);
        Assert.Equal(expected.Facts.Count, decision.Facts.Count);
        for (var index = 0; index < expected.Facts.Count; index++)
            AssertFact(decision.Facts[index], expected.Facts[index].Name, expected.Facts[index].Value);
    }

    private sealed class AdmissionFixture : IDisposable
    {
        private readonly string _attemptRoot = CreateTempDirectory("mcg-verifying-stage-decision");
        private readonly IDisposable _probe = GateLoadContextProbe.PushLiveGateOccupantProbe(() => []);
        private int _nextProcessId = 9800;

        internal AgentOrchestratorKernel Kernel { get; } = new();
        internal ConductorParallelAcceptanceAttemptCoordinator Coordinator { get; }
        internal ConductorDriver Driver { get; }
        internal int LaunchCount { get; private set; }

        internal AdmissionFixture()
        {
            Coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
                _attemptRoot,
                isProcessAlive: _ => true,
                launchOwnedProcess: _ =>
                {
                    LaunchCount++;
                    return new ConductorParallelAcceptanceOwnedProcessLaunchResult(++_nextProcessId);
                });
            Driver = MakeDriver(
                getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                runAcceptanceWithSlot: (_, _) => AcceptanceVerificationSummary.PassedWithNoUnmetCriteria,
                getLandingFileScopes: _ => ["docs/test-audit/critical-lanes-2026-10.md"],
                parallelAcceptanceAttemptCoordinator: Coordinator);
        }

        internal Goal CreateVerifyingGoal()
        {
            var goal = CreateGoal();
            Kernel.BeginGoalAcceptanceVerification(goal.Id, "Previously tracked attempt is missing");
            return goal;
        }

        private Goal CreateGoal()
        {
            var goal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(
                Kernel, DefaultAgents(), "Update docs/test-audit/critical-lanes-2026-10.md");
            PassVerification(Kernel, goal, goal.Tasks.Single());
            return goal;
        }

        internal Goal StartOccupant()
        {
            var goal = CreateGoal();
            Driver.BeginTick(Kernel, 0);
            Assert.Equal(GoalLifecycleState.Verifying,
                Assert.IsType<ConductorAdvanceOutcome.Held>(Driver.AdvanceOnce(goal, WidthOne).Outcome).State);
            Assert.Single(Coordinator.GetCapacityReservingAttempts([goal.Id.Value]));
            return goal;
        }

        internal IReadOnlyList<ConductorParallelAcceptanceAttempt> Attempts(Goal goal) =>
            Coordinator.GetUnreconciledAttempts([goal.Id.Value]);

        public void Dispose()
        {
            _probe.Dispose();
            TryDeleteDirectory(_attemptRoot);
        }
    }
}
