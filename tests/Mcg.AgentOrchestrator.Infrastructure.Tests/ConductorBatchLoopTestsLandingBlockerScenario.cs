using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Uses an isolated attempt directory; the collection owns the live-gate probe seam.
[Xunit.Collection(TestCollections.DotnetBuildSlots)]
public sealed class ConductorBatchLoopTestsLandingBlockerScenario : ConductorBatchLoopTests
{
    private const string CandidateSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string MainSha = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string OperatorScope = "operator observation";
    private static readonly DateTimeOffset Start = DateTimeOffset.UnixEpoch;

    public ConductorBatchLoopTestsLandingBlockerScenario(ITestOutputHelper output) : base(output) { }

    [Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [Trait("Category", "CrossTick")]
    public void Run_ChangingLandingHolds_TracksKindsAndSinceThenClearsAfterLanding()
    {
        using var scenario = CreateScenario();
        using var census = GateLoadContextProbe.PushLiveGateOccupantProbe(() => []);
        var evidence = TickHeld(scenario, Start);
        var repeated = TickHeld(scenario, Start.AddMinutes(5));
        Assert.Equal(evidence.Kind, repeated.Kind);
        Assert.Equal(Start, repeated.Since);

        scenario.MutationLeaseAvailable = false;
        var lease = TickHeld(scenario, Start.AddMinutes(10));
        Assert.Equal(Start.AddMinutes(10), lease.Since);
        Assert.NotEqual(evidence.Kind, lease.Kind);

        scenario.MutationLeaseAvailable = true;
        var occupant = scenario.StartOccupant();
        scenario.ReplaceReceipt(scenario.Receipt with { ReplayContextFingerprint = null });
        var width = TickHeld(scenario, Start.AddMinutes(15));
        Assert.Equal(3, new[] { evidence.Kind, lease.Kind, width.Kind }.Distinct().Count());
        Assert.Contains(new[] { evidence.Kind, lease.Kind, width.Kind },
            kind => !kind.StartsWith("untyped/", StringComparison.Ordinal));
        Assert.Equal(0, scenario.LandingRuns);
        Assert.Equal(occupant.AttemptId,
            Assert.Single(scenario.Coordinator.GetCapacityReservingAttempts([occupant.GoalId])).AttemptId);

        // Remove only this fixture's synthetic occupant receipt, never a real process.
        Assert.True(File.Exists(occupant.MetadataPath));
        File.Delete(occupant.MetadataPath);
        Assert.Empty(scenario.Coordinator.GetCapacityReservingAttempts([occupant.GoalId]));
        scenario.SatisfyOperatorEvidence();
        for (var tick = 0; tick < 5 && scenario.LandingRuns == 0; tick++)
        {
            scenario.Now = Start.AddMinutes(20 + tick);
            scenario.Tick();
        }
        Assert.Equal(1, scenario.LandingRuns);
        Assert.Null(scenario.Kernel.GetGoal(scenario.Goal.Id).LandingBlocker);
        Assert.Null(AgentOrchestratorKernel.FromSnapshot(scenario.Kernel.ExportSnapshot())
            .GetGoal(scenario.Goal.Id).LandingBlocker);
    }

    [Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [Trait("Category", "CrossTick")]
    public void Run_LeaseHoldReleased_ReturningEvidenceKindStartsNewSince()
    {
        using var scenario = CreateScenario();
        using var census = GateLoadContextProbe.PushLiveGateOccupantProbe(() => []);
        var evidence = TickHeld(scenario, Start);
        scenario.MutationLeaseAvailable = false;
        var lease = TickHeld(scenario, Start.AddMinutes(10));
        Assert.NotEqual(evidence.Kind, lease.Kind);
        scenario.MutationLeaseAvailable = true;
        var returned = TickHeld(scenario, Start.AddMinutes(15));
        Assert.Equal(evidence.Kind, returned.Kind);
        Assert.Equal(Start.AddMinutes(15), returned.Since);
        Assert.Equal(0, scenario.LandingRuns);
    }

    private static GoalLandingBlocker TickHeld(Scenario scenario, DateTimeOffset now)
    {
        scenario.Now = now;
        var (summary, tick) = scenario.Tick();
        Assert.Equal(1, summary.Held);
        Assert.Single((tick.ProgressLines ?? []).Where(line =>
            line.StartsWith($"GOAL goal={scenario.Goal.Id.Value[..8]} result=held ", StringComparison.Ordinal)));
        return Assert.IsType<GoalLandingBlocker>(scenario.Kernel.GetGoal(scenario.Goal.Id).LandingBlocker);
    }

    private static void AssertProgressMatches(GoalLandingBlocker? recorded, string line)
    {
        var blocker = Assert.IsType<GoalLandingBlocker>(recorded);
        var reason = line.Split(" reason=", 2)[1].Split(' ', 2)[0];
        Assert.Equal(reason, ConductorBatchLoop.SanitizeReason(blocker.Detail));
        if (line.Contains(" decision=", StringComparison.Ordinal))
        {
            var decision = line.Split(" decision=", 2)[1].Split(' ', 2)[0].Split('/', 3);
            Assert.Equal(decision[0] + "/" + decision[2], blocker.Kind);
        }
        else
        {
            Assert.StartsWith("untyped/", blocker.Kind);
        }
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

        var root = CreateTempDirectory("mcg-landing-blocker-scenario");
        try
        {
            var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(root, Mcg.AgentOrchestrator.Infrastructure.TrunkBranchName.Default,
                runInline: true, isProcessAlive: _ => true);
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
                AcceptanceCriterionEvidence.RecordAndCreateHold(goal, CandidateSha, kernel, Mcg.AgentOrchestrator.Infrastructure.TrunkBranchName.Default));

            var scenario = new Scenario(root, kernel, goal, coordinator, hold);
            scenario.Driver = MakeDriver(
                getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true, HasOpenClarification: scenario.HumanHold),
                runAcceptanceWithSlot: (_, _) =>
                {
                    scenario.VerifierRuns++;
                    return new AcceptanceVerificationSummary(
                        true, [], BranchHeadSha: scenario.CurrentCandidateSha,
                        MainHeadSha: scenario.CurrentMainSha);
                },
                land: landedGoal =>
                {
                    scenario.BeforeLanding?.Invoke();
                    scenario.LandingRuns++;
                    return new LandingResult(
                        landedGoal.Id.Value, landedGoal.Id.Value[..8],
                        new LandingDecision.Promote(), "integration", true, "Landed");
                },
                getLandingFileScopes: _ => candidate.ScopePaths,
                tryAcquireEvidenceMutationLease: (_, _) => scenario.TryAcquireMutationLease(),
                parallelAcceptanceAttemptCoordinator: coordinator,
                resolveAcceptanceHeads: _ => (scenario.CurrentCandidateSha, scenario.CurrentMainSha),
                readAcceptanceEngineHealth: () =>
                {
                    scenario.HealthReads++;
                    return scenario.EngineHealth;
                });
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
        public AcceptanceEngineHealthSnapshot? EngineHealth { get; set; }
        public int HealthReads { get; set; }
        public DateTimeOffset Now { get; set; } = Start;
        public bool HumanHold { get; set; }
        public bool MutationLeaseAvailable { get; set; } = true;
        public Action? BeforeLanding { get; set; }
        private int _activeMutationLeases;
        public int ActiveMutationLeases => Volatile.Read(ref _activeMutationLeases);

        public IDisposable? TryAcquireMutationLease()
        {
            if (!MutationLeaseAvailable || Interlocked.CompareExchange(ref _activeMutationLeases, 1, 0) != 0)
                return null;
            return new MutationLease(() => Interlocked.Exchange(ref _activeMutationLeases, 0));
        }

        public void SatisfyOperatorEvidence()
        {
            var obligation = Assert.Single(Goal.GetOutstandingCriterionEvidenceObligations(CandidateSha));
            Kernel.RecordCriterionEvidence(Goal.Id, obligation.Id, CriterionEvidenceOwner.Operator,
                CandidateSha, "confirmed", OperatorScope, true, "operator confirmed");
        }
        public int AttemptCount => Directory.EnumerateFiles(
            Path.Combine(root, Goal.Id.Value), "*.attempt.json").Count();
        public ConductorParallelAcceptanceAttempt Receipt => System.Text.Json.JsonSerializer
            .Deserialize<ConductorParallelAcceptanceAttempt>(File.ReadAllText(
                Directory.GetFiles(Path.Combine(root, Goal.Id.Value), "*.attempt.json").Single()),
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

        public void ReplaceReceipt(ConductorParallelAcceptanceAttempt receipt) => File.WriteAllText(
            receipt.MetadataPath, System.Text.Json.JsonSerializer.Serialize(receipt));

        public ConductorParallelAcceptanceAttempt StartOccupant()
        {
            var otherGoal = CreateVerifiedSimpleGoal(Kernel, "unrelated live gate");
            var other = new ConductorParallelAcceptanceAttemptCoordinator(root,
                Mcg.AgentOrchestrator.Infrastructure.TrunkBranchName.Default, isProcessAlive: _ => true,
                launchOwnedProcess: _ => new ConductorParallelAcceptanceOwnedProcessLaunchResult(9876));
            var decision = other.Evaluate(ConductorParallelAcceptanceCandidate.Create(otherGoal, 0,
                    ["src/unrelated-gate.cs"], CandidateSha, MainSha),
                ConductorAutonomyPolicy.Conservative,
                (_, _, _, _, _) => throw new InvalidOperationException("The unrelated gate must remain live."));
            Assert.Equal(ConductorParallelAcceptanceAttemptDecisionKind.Started, decision.Kind);
            Assert.Single(Coordinator.GetCapacityReservingAttempts([otherGoal.Id.Value]));
            return decision.Attempt;
        }

        public void Advance(bool fallback)
        {
            if (!fallback) { Tick(); return; }
            Driver.BeginTick(Kernel, 0);
            Driver.AdvanceOnce(Goal, ConductorAutonomyPolicy.Conservative with { AcceptanceWidth = 1 });
        }

        public (BatchLoopSummary Summary, BatchTickSummary Tick) Tick()
        {
            BatchTickSummary? tick = null;
            Driver.BeginTick(Kernel, 0);
            var summary = new ConductorBatchLoop(utcNow: () => Now).Run(
                Kernel, Driver, ConductorAutonomyPolicy.Conservative with { AcceptanceWidth = 1 }, NoStopPath(),
                maxIterations: 1, onlyGoalId: Goal.Id.Value, onTick: value => tick = value);
            var observedTick = Assert.IsType<BatchTickSummary>(tick);
            var heldLines = (observedTick.ProgressLines ?? []).Where(line =>
                line.StartsWith($"GOAL goal={Goal.Id.Value[..8]} result=held ", StringComparison.Ordinal)).ToArray();
            foreach (var line in heldLines)
                AssertProgressMatches(Kernel.GetGoal(Goal.Id).LandingBlocker, line);
            return (summary, observedTick);
        }

        public void Dispose() => TryDeleteDirectory(root);

        private sealed class MutationLease(Action release) : IDisposable
        {
            public void Dispose() => release();
        }
    }
}
