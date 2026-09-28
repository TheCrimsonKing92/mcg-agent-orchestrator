using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.DotnetBuildSlots)]
public sealed class ConductorBatchLoopTestsParallelAcceptanceNonGateEvidence : ConductorBatchLoopTests
{
    private const string CandidateSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string MainSha = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string OperatorScope = "operator observation";

    public ConductorBatchLoopTestsParallelAcceptanceNonGateEvidence(ITestOutputHelper output) : base(output) { }

    [Xunit.Fact]
    public void ReconciledPassWithOnlyOperatorEvidenceHoldsAcrossTwoTicksWithoutReservingASlot()
    {
        using var scenario = CreateScenario();
        string? holdIdentity = null;

        for (var tick = 0; tick < 2; tick++)
        {
            var (summary, tickSummary) = scenario.Tick();
            Assert.Equal(1, summary.Held);
            Assert.Equal(GoalStatus.Verified, scenario.Goal.Status);
            var recordedHold = Assert.IsType<GoalHoldState>(scenario.Goal.CurrentHold);
            Assert.Equal(GoalLifecycleState.Verified.ToString(), recordedHold.State);
            Assert.Equal(scenario.Hold.Reason, recordedHold.Blocker);
            var expectedIdentity = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes($"{recordedHold.State}\0{scenario.Hold.StableIdentity}")))
                .ToLowerInvariant();
            Assert.Equal(expectedIdentity, recordedHold.Identity);
            holdIdentity ??= recordedHold.Identity;
            Assert.Equal(holdIdentity, recordedHold.Identity);
            Assert.DoesNotContain(tickSummary.ProgressLines ?? [], line =>
                line.Contains("result=started", StringComparison.Ordinal) ||
                line.Contains("result=running", StringComparison.Ordinal));
            Assert.Empty(scenario.Coordinator.GetCapacityReservingAttempts([scenario.Goal.Id.Value]));
            Assert.False(scenario.Coordinator.HasLiveAttempt(scenario.Goal.Id.Value));
            Assert.Equal(0, scenario.VerifierRuns);
            Assert.Equal(1, scenario.AttemptCount);
        }
    }

    [Xunit.Fact]
    public void OperatorEvidenceLandsFromTheOriginalPassedAttempt()
    {
        using var scenario = CreateScenario();
        scenario.Tick();
        var obligation = Assert.Single(scenario.Goal.GetOutstandingCriterionEvidenceObligations(CandidateSha));
        scenario.Kernel.RecordCriterionEvidence(
            scenario.Goal.Id, obligation.Id, CriterionEvidenceOwner.Operator, CandidateSha,
            "operator-receipt", OperatorScope, passed: true, "operator confirmed behavior");

        var (summary, _) = scenario.Tick();

        Assert.Equal(1, summary.Advanced);
        Assert.Equal(1, scenario.LandingRuns);
        Assert.Equal(0, scenario.VerifierRuns);
        Assert.Equal(1, scenario.AttemptCount);
    }

    [Xunit.Theory]
    [Xunit.InlineData(true)]
    [Xunit.InlineData(false)]
    public void ChangedCandidateOrMainStartsANewAcceptanceAttempt(bool changeCandidate)
    {
        using var scenario = CreateScenario();
        scenario.Tick();
        if (changeCandidate)
            scenario.CurrentCandidateSha = "cccccccccccccccccccccccccccccccccccccccc";
        else
            scenario.CurrentMainSha = "dddddddddddddddddddddddddddddddddddddddd";

        scenario.Tick();

        Assert.Equal(1, scenario.VerifierRuns);
        Assert.Equal(2, scenario.AttemptCount);
    }

    [Xunit.Fact]
    public void NewlyAcceptanceOwnedObligationStartsANewAcceptanceAttempt()
    {
        using var scenario = CreateScenario();
        scenario.Tick();
        scenario.Kernel.MapCriterionEvidenceOwner(
            scenario.Goal.Id, 0, 1, CriterionEvidenceOwner.Acceptance,
            "test owner change", CriterionEvidenceScopes.FullAcceptanceGate,
            expectedCandidateSha: CandidateSha);

        scenario.Tick();

        Assert.Equal(1, scenario.VerifierRuns);
        Assert.Equal(2, scenario.AttemptCount);
    }

    private Scenario CreateScenario()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = CreateVerifiedSimpleGoal(kernel, "Hold passed acceptance for operator evidence");
        kernel.RecordGoalRefinement(goal.Id, new RefinedSpec(
            goal.Objective, ["Operator confirms the behavior"],
            VerificationClass.RealWorldDependent, [], []));
        kernel.MapCriterionEvidenceOwner(
            goal.Id, 0, 1, CriterionEvidenceOwner.Operator,
            "test operator", OperatorScope, expectedCandidateSha: CandidateSha);

        var root = CreateTempDirectory("mcg-passed-nongate-evidence");
        try
        {
            var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(root, runInline: true);
            var candidate = ConductorParallelAcceptanceCandidate.Create(
                goal, 0, ["src/Mcg.AgentOrchestrator.App/Orchestration/ConductorBatchLoop.cs"],
                CandidateSha, MainSha);
            var seed = coordinator.Evaluate(
                candidate, ConductorAutonomyPolicy.Conservative,
                (current, _, _, _, _) => ConductorParallelAcceptanceRunResult.Accepted(
                    current, new AcceptanceVerificationSummary(
                        true, [], BranchHeadSha: CandidateSha, MainHeadSha: MainSha)));
            Assert.Equal(ConductorParallelAcceptanceAttemptDecisionKind.Completed, seed.Kind);
            coordinator.MarkReconciled(seed.Attempt);
            var hold = Assert.IsType<ConductorAdvanceOutcome.Held>(
                AcceptanceCriterionEvidence.RecordAndCreateHold(goal, CandidateSha, kernel));

            var scenario = new Scenario(root, kernel, goal, coordinator, hold);
            scenario.Driver = MakeDriver(
                getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                runAcceptanceWithSlot: (_, _) =>
                {
                    scenario.VerifierRuns++;
                    return new AcceptanceVerificationSummary(
                        true, [], BranchHeadSha: scenario.CurrentCandidateSha,
                        MainHeadSha: scenario.CurrentMainSha);
                },
                land: landedGoal =>
                {
                    scenario.LandingRuns++;
                    return new LandingResult(
                        landedGoal.Id.Value, landedGoal.Id.Value[..8],
                        new LandingDecision.Promote(), "integration", true, "Landed");
                },
                getLandingFileScopes: _ => candidate.ScopePaths,
                parallelAcceptanceAttemptCoordinator: coordinator,
                resolveAcceptanceHeads: _ => (scenario.CurrentCandidateSha, scenario.CurrentMainSha));
            return scenario;
        }
        catch
        {
            TryDeleteDirectory(root);
            throw;
        }
    }

    private sealed class Scenario(
        string root,
        AgentOrchestratorKernel kernel,
        Goal goal,
        ConductorParallelAcceptanceAttemptCoordinator coordinator,
        ConductorAdvanceOutcome.Held hold) : IDisposable
    {
        public AgentOrchestratorKernel Kernel { get; } = kernel;
        public Goal Goal { get; } = goal;
        public ConductorParallelAcceptanceAttemptCoordinator Coordinator { get; } = coordinator;
        public ConductorAdvanceOutcome.Held Hold { get; } = hold;
        public ConductorDriver Driver { get; set; } = null!;
        public string CurrentCandidateSha { get; set; } = CandidateSha;
        public string CurrentMainSha { get; set; } = MainSha;
        public int VerifierRuns { get; set; }
        public int LandingRuns { get; set; }
        public int AttemptCount => Directory.EnumerateFiles(
            Path.Combine(root, Goal.Id.Value), "*.attempt.json").Count();

        public (BatchLoopSummary Summary, BatchTickSummary Tick) Tick()
        {
            BatchTickSummary? tick = null;
            var summary = new ConductorBatchLoop().Run(
                Kernel, Driver, ConductorAutonomyPolicy.Conservative, NoStopPath(),
                maxIterations: 1, onTick: value => tick = value);
            return (summary, Assert.IsType<BatchTickSummary>(tick));
        }

        public void Dispose() => TryDeleteDirectory(root);
    }
}
