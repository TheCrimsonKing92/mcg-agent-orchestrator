using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: in-memory kernels, inline gates, and an exclusively owned temporary attempt root.
public sealed class FaultRecordedAcceptanceFailureRouteTests : ConductorBatchLoopTests
{
    private const string CandidateSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    public FaultRecordedAcceptanceFailureRouteTests(ITestOutputHelper output) : base(output) { }

    [Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [Trait("Category", "CrossTick")]
    public void TimeoutRestoresVerifiedOncePerShaThenEscalates()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = CreateVerifiedSimpleGoal(kernel, "Update src/Probe.cs");
        RecordFault(kernel, goal, WorkspaceProbeFaultClassifierTests.F2);
        var driver = MakeDriver(getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true));
        driver.BeginTick(kernel, 1);

        var first = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(GoalLifecycleState.Verified, Assert.IsType<ConductorAdvanceOutcome.Held>(first.Outcome).State);
        Assert.Equal(GoalStatus.Verified, goal.Status);
        Assert.Null(goal.LatestAcceptanceFailure);
        Assert.Single(goal.Timeline.Where(evt => evt.Message.StartsWith(
            $"FAULT_RECORDED_REGATE candidate_sha={CandidateSha} ", StringComparison.Ordinal)));
        var marker = Assert.Single(RegateMarkers(goal));
        Assert.StartsWith($"FAULT_RECORDED_REGATE candidate_sha={CandidateSha} ordinal=1/1", marker.Message);

        RecordFault(kernel, goal, WorkspaceProbeFaultClassifierTests.F2);
        driver.BeginTick(kernel, 2);
        var second = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        var escalation = Assert.IsType<ConductorAdvanceOutcome.Escalated>(second.Outcome);
        Assert.Equal(ConductorEscalationKind.BackgroundAcceptanceFailed, escalation.Kind);
        Assert.StartsWith("background acceptance fault recorded at AcceptanceFailed:", escalation.Reason);
        Assert.DoesNotContain("Unhandled lifecycle state", escalation.Reason, StringComparison.Ordinal);
        Assert.Equal(GoalStatus.AcceptanceFailed, goal.Status);
        Assert.Single(RegateMarkers(goal));
    }

    [Fact]
    public void ConservativePolicyRetainsManualReviewPrecedence()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = CreateVerifiedSimpleGoal(kernel, "Update src/Probe.cs");
        RecordFault(kernel, goal, WorkspaceProbeFaultClassifierTests.F2);
        var driver = MakeDriver(getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true));
        driver.BeginTick(kernel, 1);

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        var escalation = Assert.IsType<ConductorAdvanceOutcome.Escalated>(result.Outcome);
        Assert.Equal("policy-manual-review", escalation.Decision!.DiscriminatingEvidence);
        Assert.Equal(GoalStatus.AcceptanceFailed, goal.Status);
        Assert.Empty(RegateMarkers(goal));
    }

    [Fact]
    public void DefinitiveProbeEscalatesWithoutRegating()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = CreateVerifiedSimpleGoal(kernel, "Update src/Probe.cs");
        RecordFault(kernel, goal, "background-acceptance-fault: " + WorkspaceProbeFaultClassifierTests.F3.Replace(' ', '_'));
        var driver = MakeDriver(getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true));
        driver.BeginTick(kernel, 1);

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        var escalation = Assert.IsType<ConductorAdvanceOutcome.Escalated>(result.Outcome);
        Assert.Equal(ConductorEscalationKind.BackgroundAcceptanceFailed, escalation.Kind);
        Assert.StartsWith("background acceptance fault recorded at AcceptanceFailed:", escalation.Reason);
        Assert.Equal(GoalStatus.AcceptanceFailed, goal.Status);
        Assert.Empty(RegateMarkers(goal));
    }

    [Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [Trait("Category", "CrossTick")]
    public void RecordedTimeoutRecoversAndNextTickRunsExactlyOneGate()
    {
        var root = CreateTempDirectory("mcg-recorded-probe-regate");
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var goal = CreateVerifiedSimpleGoal(kernel, "Update src/Probe.cs");
            RecordFault(kernel, goal, WorkspaceProbeFaultClassifierTests.F2);
            var calls = 0;
            var escalations = new List<string>();
            var driver = MakeDriver(
                getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                runAcceptanceWithSlot: (_, _) =>
                {
                    calls++;
                    return AcceptanceVerificationSummary.PassedWithNoUnmetCriteria;
                },
                getLandingFileScopes: _ => ["src/Probe.cs"],
                parallelAcceptanceAttemptCoordinator: new ConductorParallelAcceptanceAttemptCoordinator(
                    root, TrunkBranchName.Default, runInline: true),
                writeEscalation: (_, _, reason) => escalations.Add(reason));

            var first = new ConductorBatchLoop().Run(kernel, driver, ConductorAutonomyPolicy.Permissive,
                NoStopPath(), maxIterations: 1);
            Assert.Equal(1, first.Held);
            Assert.Equal(0, first.Escalated);
            Assert.Equal(GoalStatus.Verified, goal.Status);
            Assert.Equal(0, calls);
            var second = new ConductorBatchLoop().Run(kernel, driver, ConductorAutonomyPolicy.Permissive,
                NoStopPath(), maxIterations: 1);

            Assert.Equal(1, calls);
            Assert.Equal(0, second.Escalated);
            Assert.Empty(escalations);
            Assert.Single(RegateMarkers(goal));
        }
        finally { TryDeleteDirectory(root); }
    }

    [Theory]
    [InlineData("a", false)]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", true)]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaab", false)]
    public void PriorMarkerMatchesTheWholeCandidateToken(string priorSha, bool alreadyRegated)
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = CreateVerifiedSimpleGoal(kernel, "Update src/Probe.cs");
        kernel.RecordGoalPolicyDecision(goal.Id, $"FAULT_RECORDED_REGATE candidate_sha={priorSha} ordinal=1/1\r\nextra detail");
        RecordFault(kernel, goal, WorkspaceProbeFaultClassifierTests.F2);

        var decision = FaultRecordedAcceptanceFailureRoute.Decide(goal);

        Assert.NotNull(decision);
        Assert.Equal(!alreadyRegated, decision.IsRegate);
    }

    [Fact]
    public void MarkerForLongerShaDoesNotConsumeShortShaAllowance()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = CreateVerifiedSimpleGoal(kernel, "Update src/Probe.cs");
        kernel.RecordGoalPolicyDecision(goal.Id, $"FAULT_RECORDED_REGATE candidate_sha={CandidateSha} ordinal=1/1");
        RecordFault(kernel, goal, WorkspaceProbeFaultClassifierTests.F2, "aaaa");
        Assert.True(FaultRecordedAcceptanceFailureRoute.Decide(goal)!.IsRegate);
    }

    [Fact]
    public void MixedChecksAndAttributedChecksAreOutsideTheRoute()
    {
        var kernel = new AgentOrchestratorKernel();
        var mixed = CreateVerifiedSimpleGoal(kernel, "Update src/Mixed.cs");
        Assert.True(kernel.BeginGoalAcceptanceVerification(mixed.Id, "test gate begins"));
        Assert.True(kernel.ReconcileGoalAcceptanceFailed(mixed.Id,
            [WorkspaceProbeFaultClassifierTests.F2, "another failure"], "test mixed failure", CandidateSha));
        Assert.Null(FaultRecordedAcceptanceFailureRoute.Decide(mixed));
        var attributed = CreateVerifiedSimpleGoal(kernel, "Update src/Attributed.cs");
        Assert.True(kernel.BeginGoalAcceptanceVerification(attributed.Id, "test gate begins"));
        Assert.True(kernel.ReconcileGoalAcceptanceFailed(attributed.Id, [WorkspaceProbeFaultClassifierTests.F2],
            "test attributed failure", CandidateSha, checkAttributions:
            [new AcceptanceCheckAttribution(WorkspaceProbeFaultClassifierTests.F2, AcceptanceFailureOrigin.Introduced, "test evidence")]));
        Assert.Null(FaultRecordedAcceptanceFailureRoute.Decide(attributed));
    }

    private static void RecordFault(AgentOrchestratorKernel kernel, Goal goal, string check, string sha = CandidateSha)
    {
        Assert.True(kernel.BeginGoalAcceptanceVerification(goal.Id, "test gate begins"));
        Assert.True(kernel.ReconcileGoalAcceptanceFailed(goal.Id, [check], "test fault recorded", sha, "main-a"));
        Assert.Equal(GoalStatus.AcceptanceFailed, goal.Status);
        Assert.Equal(check, Assert.Single(goal.LatestAcceptanceFailure!.FailedChecks));
    }

    private static IEnumerable<ProgressEvent> RegateMarkers(Goal goal) => goal.Timeline.Where(evt =>
        evt.Kind == ProgressKind.GoalPolicyDecision && evt.Message.StartsWith("FAULT_RECORDED_REGATE ", StringComparison.Ordinal));
}
