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

    [Xunit.Theory(Timeout = 30000)]
    [Xunit.Trait("Category", "CrossTick")]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void ClearedWaitReplaysWhileWidthOneRemainsOccupied(bool fallback)
    {
        using var scenario = CreateScenario();
        using var census = GateLoadContextProbe.PushLiveGateOccupantProbe(() => []);
        var occupant = scenario.StartOccupant();
        var original = scenario.Receipt;

        scenario.Advance(fallback);
        Assert.Equal(0, scenario.LandingRuns);
        Assert.Equal(0, scenario.VerifierRuns);
        Assert.Equal(original.ReconciledAt, scenario.Receipt.ReconciledAt);
        var obligation = Assert.Single(scenario.Goal.GetOutstandingCriterionEvidenceObligations(CandidateSha));
        scenario.Kernel.RecordCriterionEvidence(scenario.Goal.Id, obligation.Id,
            CriterionEvidenceOwner.Operator, CandidateSha, "cleared-wait", OperatorScope, true, "confirmed");

        scenario.Advance(fallback);
        scenario.Advance(fallback);

        Assert.Equal(1, scenario.LandingRuns);
        Assert.Equal(0, scenario.VerifierRuns);
        Assert.Equal(1, scenario.AttemptCount);
        Assert.Equal(original.AttemptId, scenario.Receipt.AttemptId);
        Assert.Equal(original.ReconciledAt, scenario.Receipt.ReconciledAt);
        Assert.NotNull(scenario.Receipt.ReplayLandedAt);
        Assert.Equal(occupant.AttemptId,
            Assert.Single(scenario.Coordinator.GetCapacityReservingAttempts([occupant.GoalId])).AttemptId);
    }

    [Xunit.Theory]
    [Xunit.InlineData("spec")]
    [Xunit.InlineData("task")]
    [Xunit.InlineData("missing-result")]
    [Xunit.InlineData("corrupt-result")]
    [Xunit.InlineData("missing-freshness")]
    public void InvalidReplayProofRequiresFreshValidationAtOccupiedWidth(string change)
    {
        using var scenario = CreateScenario();
        scenario.SatisfyOperatorEvidence();
        using var census = GateLoadContextProbe.PushLiveGateOccupantProbe(() => []);
        var occupant = scenario.StartOccupant();
        var original = scenario.Receipt;
        switch (change)
        {
            case "spec":
                scenario.Kernel.RecordGoalRefinement(scenario.Goal.Id, scenario.Goal.RefinedSpec! with
                    { BehavioralContract = "changed contract" });
                break;
            case "task":
                scenario.Kernel.RecordTaskVerification(scenario.Goal.Id, scenario.Goal.Tasks.Single().Id,
                    ManualVerificationRecorder.Create(true, "new verification", "fixture", DateTimeOffset.UtcNow));
                break;
            case "missing-result": File.Delete(original.ResultPath); break;
            case "corrupt-result": File.AppendAllText(original.ResultPath, " "); break;
            case "missing-freshness":
                scenario.ReplaceReceipt(original with { ReplayContextFingerprint = null });
                break;
        }

        Assert.Null(scenario.Driver.TryReplayPassedAcceptance(scenario.Goal, ConductorAutonomyPolicy.Conservative));
        scenario.Tick();

        Assert.Equal(0, scenario.LandingRuns);
        Assert.Equal(0, scenario.VerifierRuns);
        Assert.Equal(1, scenario.AttemptCount);
        Assert.Null(scenario.Receipt.ReplayLandedAt);
        Assert.Equal(original.ReconciledAt, scenario.Receipt.ReconciledAt);
        Assert.Equal(occupant.AttemptId,
            Assert.Single(scenario.Coordinator.GetCapacityReservingAttempts([occupant.GoalId])).AttemptId);
    }

    [Xunit.Theory]
    [Xunit.InlineData("human")]
    [Xunit.InlineData("lease")]
    [Xunit.InlineData("task")]
    public void CurrentHoldPreventsReceiptApplication(string hold)
    {
        using var scenario = CreateScenario();
        scenario.SatisfyOperatorEvidence();
        if (hold == "human") scenario.HumanHold = true;
        if (hold == "lease") scenario.MutationLeaseAvailable = false;
        if (hold == "task")
            scenario.Kernel.RecordTaskVerification(scenario.Goal.Id, scenario.Goal.Tasks.Single().Id,
                ManualVerificationRecorder.Create(false, "failed now", "fixture", DateTimeOffset.UtcNow));

        var result = scenario.Driver.TryReplayPassedAcceptance(scenario.Goal, ConductorAutonomyPolicy.Conservative);

        Assert.True(result is null || result.IsHeld);
        Assert.Equal(0, scenario.LandingRuns);
        Assert.Null(scenario.Receipt.ReplayLandedAt);
        Assert.Equal(0, scenario.VerifierRuns);
    }

    [Xunit.Fact(Timeout = 30000)]
    public async Task ConcurrentReplayKeepsMutationCustodyThroughExactlyOneLanding()
    {
        using var scenario = CreateScenario();
        scenario.SatisfyOperatorEvidence();
        scenario.Driver.BeginTick(scenario.Kernel, 0);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        scenario.BeforeLanding = () =>
        {
            entered.TrySetResult();
            release.Task.GetAwaiter().GetResult();
        };
        var first = Task.Run(() => scenario.Driver.TryReplayPassedAcceptance(scenario.Goal, ConductorAutonomyPolicy.Conservative));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            var second = await Task.Run(() => scenario.Driver.TryReplayPassedAcceptance(scenario.Goal, ConductorAutonomyPolicy.Conservative));
            Assert.NotNull(second);
            Assert.True(second.IsHeld);
            Assert.Equal(0, scenario.LandingRuns);
            Assert.Equal(1, scenario.ActiveMutationLeases);
        }
        finally { release.TrySetResult(); }
        Assert.True((await first.WaitAsync(TimeSpan.FromSeconds(30)))!.WasExecuted);
        scenario.Driver.TryReplayPassedAcceptance(scenario.Goal, ConductorAutonomyPolicy.Conservative);
        Assert.Equal(1, scenario.LandingRuns);
        Assert.Equal(0, scenario.ActiveMutationLeases);
        Assert.Equal(1, scenario.AttemptCount);
        Assert.Equal(0, scenario.VerifierRuns);
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

    [Xunit.Fact]
    public void Replay_TrippedCircuit_RecordsAdmissionAndDecisionWithoutLanding()
    {
        using var scenario = CreateScenario();
        scenario.SatisfyOperatorEvidence();
        var receipt = scenario.Receipt;
        var health = new AcceptanceEngineHealthSnapshot(AcceptanceEngineHealth.Unhealthy,
            MainSha, "main suspect", "canary-receipt", DateTimeOffset.UnixEpoch);
        var results = new Dictionary<string, ParallelLandingOutcome>();
        var changed = new HashSet<GoalId>();
        var progress = new List<string>();

        Assert.True(PassedAcceptanceReceiptReplay.TryBeforeAdmission(0, scenario.Goal,
            scenario.Driver, ConductorAutonomyPolicy.Conservative, () => health, results,
            changed, 7, progress, (line, lines) => lines.Add(line)));

        Assert.Equal($"ADMISSION tick=7 result=held reason=acceptance-engine-circuit goal={scenario.Goal.Id.Value[..8]} health=Unhealthy",
            Assert.Single(progress));
        var hold = Assert.IsType<ConductorAdvanceOutcome.Held>(results[scenario.Goal.Id.Value].Result.Outcome);
        var decision = Assert.IsType<PolicyDecisionRecord>(hold.Decision);
        Assert.Equal("acceptance-engine-circuit", decision.DiscriminatingEvidence);
        Assert.Contains(new PolicyDecisionFact("health", "Unhealthy"), decision.Facts);
        Assert.Contains(new PolicyDecisionFact("receiptReference", "canary-receipt"), decision.Facts);
        Assert.Equal(0, scenario.LandingRuns);
        Assert.Equal(0, scenario.VerifierRuns);
        Assert.Empty(changed);
        Assert.Equal(receipt.AttemptId, scenario.Receipt.AttemptId);
        Assert.Equal(receipt.ReconciledAt, scenario.Receipt.ReconciledAt);
        Assert.Equal(receipt.ReplayLandedAt, scenario.Receipt.ReplayLandedAt);
        Assert.Equal(receipt.ReplayContextFingerprint, scenario.Receipt.ReplayContextFingerprint);
    }

    [Xunit.Fact]
    public void Replay_AlreadyLanded_RecordsReceiptDecisionWithoutApplyingAgain()
    {
        using var scenario = CreateScenario();
        scenario.SatisfyOperatorEvidence();
        var receipt = scenario.Receipt with { ReplayLandedAt = DateTimeOffset.UnixEpoch };
        scenario.ReplaceReceipt(receipt);
        var candidate = ConductorParallelAcceptanceCandidate.Create(scenario.Goal, 0,
            receipt.ScopePaths!, CandidateSha, MainSha);

        var result = scenario.Coordinator.ReplayPassedReconciledReceipt(candidate,
            _ => throw new InvalidOperationException("Already landed receipt must not be applied."));

        var hold = Assert.IsType<ConductorAdvanceOutcome.Held>(result!.Outcome);
        var decision = Assert.IsType<PolicyDecisionRecord>(hold.Decision);
        Assert.Equal("receipt-already-landed", decision.DiscriminatingEvidence);
        Assert.Contains(new PolicyDecisionFact("attemptId", receipt.AttemptId), decision.Facts);
        Assert.Contains(new PolicyDecisionFact("replayLandedAt", receipt.ReplayLandedAt.Value.ToString("O")), decision.Facts);
        Assert.Equal(receipt.AttemptId, scenario.Receipt.AttemptId);
        Assert.Equal(receipt.ReconciledAt, scenario.Receipt.ReconciledAt);
        Assert.Equal(receipt.ReplayLandedAt, scenario.Receipt.ReplayLandedAt);
        Assert.Equal(0, scenario.LandingRuns);
        Assert.Equal(0, scenario.VerifierRuns);
    }

    [Xunit.Fact]
    public void Complete_StaleReconciledPass_RecordsFreshValidationDecision()
    {
        using var scenario = CreateScenario();
        scenario.SatisfyOperatorEvidence();
        var receipt = scenario.Receipt;
        var candidate = ConductorParallelAcceptanceCandidate.Create(scenario.Goal, 0,
            receipt.ScopePaths!, CandidateSha, MainSha);
        var run = ConductorParallelAcceptanceRunResult.Accepted(candidate,
            new AcceptanceVerificationSummary(true, [], BranchHeadSha: CandidateSha, MainHeadSha: MainSha));
        scenario.ReplaceReceipt(receipt with { ReplayContextFingerprint = null });

        var result = PassedAcceptanceReceiptReplay.Complete(scenario.Driver, run,
            ConductorAutonomyPolicy.Conservative, receipt, out var mutationLeaseHeld);

        var hold = Assert.IsType<ConductorAdvanceOutcome.Held>(result.Outcome);
        var decision = Assert.IsType<PolicyDecisionRecord>(hold.Decision);
        Assert.Equal("passed-receipt-no-longer-replayable", decision.DiscriminatingEvidence);
        Assert.Contains(new PolicyDecisionFact("attemptId", receipt.AttemptId), decision.Facts);
        Assert.Contains(new PolicyDecisionFact("replayEligible", "false"), decision.Facts);
        Assert.Contains("fresh validation required", hold.Reason, StringComparison.Ordinal);
        Assert.False(mutationLeaseHeld);
        Assert.Equal(receipt.ReconciledAt, scenario.Receipt.ReconciledAt);
        Assert.Null(scenario.Receipt.ReplayLandedAt);
        Assert.Equal(0, scenario.LandingRuns);
        Assert.Equal(0, scenario.VerifierRuns);
    }

    [Xunit.Theory]
    [Xunit.InlineData("Unhealthy", false)]
    [Xunit.InlineData("Pending", false)]
    [Xunit.InlineData("Unavailable", false)]
    [Xunit.InlineData("Unhealthy", true)]
    [Xunit.InlineData("Pending", true)]
    [Xunit.InlineData("Unavailable", true)]
    public void AdvanceOnce_BlockedEngine_HoldsReconciledPass(string health, bool tick)
    {
        using var scenario = CreateScenario();
        scenario.SatisfyOperatorEvidence();
        var receipt = scenario.Receipt;
        scenario.EngineHealth = new AcceptanceEngineHealthSnapshot(
            Enum.Parse<AcceptanceEngineHealth>(health), MainSha, "main suspect",
            "canary-receipt", DateTimeOffset.UnixEpoch);
        if (tick)
            scenario.Driver.BeginTick(scenario.Kernel, 0);

        var result = scenario.Driver.AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Conservative);

        var hold = Assert.IsType<ConductorAdvanceOutcome.Held>(result.Outcome);
        Assert.Equal(GoalLifecycleState.Verified, hold.State);
        Assert.Equal(ConductorBatchLoop.BuildAcceptanceEngineHoldReason(scenario.EngineHealth), hold.Reason);
        var decision = Assert.IsType<PolicyDecisionRecord>(hold.Decision);
        Assert.Equal("acceptance-engine-circuit", decision.DiscriminatingEvidence);
        Assert.Contains(new PolicyDecisionFact("health", health), decision.Facts);
        Assert.Contains(new PolicyDecisionFact("landingSha", MainSha), decision.Facts);
        Assert.Contains(new PolicyDecisionFact("failureReason", "main suspect"), decision.Facts);
        Assert.Contains(new PolicyDecisionFact("receiptReference", "canary-receipt"), decision.Facts);
        Assert.True(scenario.HealthReads > 0);
        Assert.Equal(0, scenario.LandingRuns);
        Assert.Equal(0, scenario.VerifierRuns);
        Assert.Equal(1, scenario.AttemptCount);
        Assert.Equal(receipt.AttemptId, scenario.Receipt.AttemptId);
        Assert.Equal(receipt.ReconciledAt, scenario.Receipt.ReconciledAt);
        Assert.Equal(receipt.ReplayLandedAt, scenario.Receipt.ReplayLandedAt);
        Assert.Equal(receipt.ReplayContextFingerprint, scenario.Receipt.ReplayContextFingerprint);
        Assert.Equal(0, scenario.ActiveMutationLeases);
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void AdvanceOnce_HealthyEngine_ReplaysBeforeOccupiedWidth(bool tick)
    {
        using var scenario = CreateScenario();
        using var census = GateLoadContextProbe.PushLiveGateOccupantProbe(() => []);
        scenario.SatisfyOperatorEvidence();
        var occupant = scenario.StartOccupant();
        var receipt = scenario.Receipt;
        scenario.EngineHealth = AcceptanceEngineHealthSnapshot.Healthy(DateTimeOffset.UnixEpoch);
        if (tick)
            scenario.Driver.BeginTick(scenario.Kernel, 0);

        var result = scenario.Driver.AdvanceOnce(scenario.Goal,
            ConductorAutonomyPolicy.Conservative with { AcceptanceWidth = 1 });

        var executed = Assert.IsType<ConductorAdvanceOutcome.Executed>(result.Outcome);
        Assert.StartsWith("Landed:", executed.Description);
        Assert.True(scenario.HealthReads > 0);
        Assert.Equal(1, scenario.LandingRuns);
        Assert.Equal(0, scenario.VerifierRuns);
        Assert.Equal(1, scenario.AttemptCount);
        Assert.Equal(receipt.AttemptId, scenario.Receipt.AttemptId);
        Assert.Equal(receipt.ReconciledAt, scenario.Receipt.ReconciledAt);
        Assert.NotNull(scenario.Receipt.ReplayLandedAt);
        Assert.Equal(occupant.AttemptId,
            Assert.Single(scenario.Coordinator.GetCapacityReservingAttempts([occupant.GoalId])).AttemptId);
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
            var summary = new ConductorBatchLoop().Run(
                Kernel, Driver, ConductorAutonomyPolicy.Conservative with { AcceptanceWidth = 1 }, NoStopPath(),
                maxIterations: 1, onlyGoalId: Goal.Id.Value, onTick: value => tick = value);
            return (summary, Assert.IsType<BatchTickSummary>(tick));
        }

        public void Dispose() => TryDeleteDirectory(root);

        private sealed class MutationLease(Action release) : IDisposable
        {
            public void Dispose() => release();
        }
    }
}
