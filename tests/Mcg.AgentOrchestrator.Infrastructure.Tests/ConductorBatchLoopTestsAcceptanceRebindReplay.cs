using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.DotnetBuildSlots)]
public sealed class ConductorBatchLoopTestsAcceptanceRebindReplay : ConductorBatchLoopTests
{
    private const string PairA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string MainA = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string Prior = "cccccccccccccccccccccccccccccccccccccccc";
    private const string Provenance = "operator binding";

    public ConductorBatchLoopTestsAcceptanceRebindReplay(ITestOutputHelper output) : base(output) { }

    [Xunit.Theory(Timeout = 30000)]
    [Xunit.Trait("Category", "CrossTick")]
    [Xunit.InlineData(false, false)]
    [Xunit.InlineData(false, true)]
    [Xunit.InlineData(true, false)]
    [Xunit.InlineData(true, true)]
    public void ReboundPendingEvidenceReplaysBeforeCapacity(bool occupied, bool fallback)
    {
        using var scene = new Scene();
        using var census = GateLoadContextProbe.PushLiveGateOccupantProbe(() => []);
        var occupant = occupied ? scene.StartOccupant() : null;
        var receipt = scene.Receipt;
        scene.AssertPending();
        Assert.Equal(receipt.ReplayContextFingerprint,
            ConductorParallelAcceptanceAttemptCoordinator.BuildReplayContextFingerprint(scene.Goal));
        scene.Reload(); // Rebinding and proof survive a process restart.
        scene.AssertPending();
        Assert.Equal(receipt.ReplayContextFingerprint,
            ConductorParallelAcceptanceAttemptCoordinator.BuildReplayContextFingerprint(scene.Goal));

        scene.Advance(fallback);

        scene.AssertSatisfied();
        Assert.Equal(1, scene.Landings);
        Assert.Equal(0, scene.GateLaunches);
        Assert.Equal(0, scene.WorkerLaunches);
        Assert.Equal(1, scene.AttemptCount);
        Assert.Equal(receipt.AttemptId, scene.Receipt.AttemptId);
        Assert.Equal(receipt.ReconciledAt, scene.Receipt.ReconciledAt);
        Assert.NotNull(scene.Receipt.ReplayLandedAt);
        var evidence = JsonSerializer.Serialize(scene.Goal.CriterionEvidenceObligations);
        scene.Reload();
        scene.Advance(fallback);
        scene.Advance(fallback);
        Assert.Equal(evidence, JsonSerializer.Serialize(scene.Goal.CriterionEvidenceObligations));
        Assert.Equal(1, scene.Landings);
        Assert.Equal(0, scene.GateLaunches);
        Assert.Equal(0, scene.WorkerLaunches);
        if (occupant is not null) scene.AssertOccupant(occupant);
    }

    [Xunit.Theory(Timeout = 30000)]
    [Xunit.Trait("Category", "CrossTick")]
    [Xunit.InlineData("missing-proof")]
    [Xunit.InlineData("corrupt-result")]
    [Xunit.InlineData("task")]
    public void DeclinedReplayAtFreeCapacityRunsFreshGateAndLandsOnce(string change)
    {
        using var scene = new Scene(native: true);
        using var census = GateLoadContextProbe.PushLiveGateOccupantProbe(() => []);
        switch (change)
        {
            case "missing-proof":
                scene.ReplaceReceipt(scene.Receipt with { ReplayContextFingerprint = null });
                break;
            case "corrupt-result": File.AppendAllText(scene.Receipt.ResultPath, " "); break;
            case "task":
                scene.Kernel.RecordTaskVerification(scene.Goal.Id, scene.Goal.Tasks.Single().Id,
                    ManualVerificationRecorder.Create(true, "new verification", "fixture", DateTimeOffset.UtcNow));
                break;
        }
        var original = scene.Receipt;
        var originalMetadata = File.ReadAllText(original.MetadataPath);
        var mainBefore = LandingExecutorTests.ReadGit(scene.Root, "rev-parse", "main");
        Assert.Null(scene.Driver.TryReplayPassedAcceptance(scene.Goal, ConductorAutonomyPolicy.Conservative));
        scene.AssertPending();
        var gateEntered = false;
        scene.BeforeGate = () =>
        {
            gateEntered = true;
            scene.AssertPending();
            Assert.Equal(0, scene.Landings);
            Assert.Equal(mainBefore, LandingExecutorTests.ReadGit(scene.Root, "rev-parse", "main"));
        };

        scene.Advance(fallback: false);

        Assert.True(gateEntered, "Declined replay must reach the ordinary fresh gate.");
        Assert.Equal(1, scene.GateLaunches);
        Assert.Equal(0, scene.WorkerLaunches);
        Assert.Equal(1, scene.Landings);
        Assert.Equal(2, scene.AttemptCount);
        scene.AssertSatisfied(replayed: false);
        var fresh = Assert.Single(Directory.GetFiles(scene.AttemptDirectory, "*.attempt.json")
            .Where(path => path != original.MetadataPath)
            .Select(path => JsonSerializer.Deserialize<ConductorParallelAcceptanceAttempt>(File.ReadAllText(path),
                new JsonSerializerOptions(JsonSerializerDefaults.Web))!));
        Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.Passed, fresh.Outcome);
        Assert.NotNull(fresh.ReconciledAt);
        Assert.Equal(scene.Candidate, fresh.BranchHeadSha);
        Assert.Equal(scene.Main, fresh.MainHeadSha);
        Assert.Equal(originalMetadata, File.ReadAllText(original.MetadataPath));
        var mainAfter = LandingExecutorTests.ReadGit(scene.Root, "rev-parse", "main");
        Assert.NotEqual(mainBefore, mainAfter);
        Assert.Equal("goal work", LandingExecutorTests.ReadGit(scene.Root, "show", "main:src/rebound.txt"));
        Assert.Single(GoalOperationJournal.Read(scene.Root, scene.Goal.Id).Entries.Where(entry =>
            entry.Operation == "conductor:land" && entry.Status == GoalOperationStatus.Completed));
        scene.Reload();
        scene.Advance(fallback: false);
        scene.Advance(fallback: false);
        scene.AssertSatisfied(replayed: false);
        Assert.Equal(mainAfter, LandingExecutorTests.ReadGit(scene.Root, "rev-parse", "main"));
        Assert.Equal(1, scene.GateLaunches);
        Assert.Equal(1, scene.Landings);
        Assert.Equal(0, scene.WorkerLaunches);
        Assert.Equal(2, scene.AttemptCount);
    }

    [Xunit.Theory(Timeout = 30000)]
    [Xunit.Trait("Category", "CrossTick")]
    [Xunit.InlineData(false, null)]
    [Xunit.InlineData(true, null)]
    [Xunit.InlineData(false, "NotExecuted")]
    [Xunit.InlineData(true, "NotExecuted")]
    [Xunit.InlineData(false, "Skipped")]
    [Xunit.InlineData(true, "Skipped")]
    public void ReboundReceiptPublishesNativeMainExactlyOnce(bool occupied, string? skippedOutcome)
    {
        using var scene = new Scene(native: true, skippedOutcome: skippedOutcome);
        using var census = GateLoadContextProbe.PushLiveGateOccupantProbe(() => []);
        var occupant = occupied ? scene.StartOccupant() : null;
        var wait = scene.Kernel.RequestHumanInputDeduplicated(scene.Goal.Id, null,
            "Required gate evidence", kind: HumanWaitKind.ProspectiveAcceptanceEvidence,
            evidenceOwner: "acceptance-gate", testNames: ["ReplayFixture.Check"]).Request;
        scene.Kernel.SubmitHumanInput(wait.Id, "Passing retained gate rows inspected");
        var mainBefore = LandingExecutorTests.ReadGit(scene.Root, "rev-parse", "main");
        scene.AssertPending();

        scene.Advance(fallback: false);

        scene.AssertSatisfied();
        Assert.Equal(1, scene.Landings);
        Assert.Equal(0, scene.GateLaunches);
        Assert.Equal(0, scene.WorkerLaunches);
        Assert.True(wait.IsCompleted);
        Assert.Empty(scene.Kernel.GetPendingHumanInput(scene.Goal.Id));
        var mainAfter = LandingExecutorTests.ReadGit(scene.Root, "rev-parse", "main");
        Assert.NotEqual(mainBefore, mainAfter);
        Assert.Equal(0, GitCli.Run(scene.Root, "merge-base", "--is-ancestor", scene.Candidate, "main").ExitCode);
        Assert.Equal("goal work", LandingExecutorTests.ReadGit(scene.Root, "show", "main:src/rebound.txt"));
        Assert.Single(GoalOperationJournal.Read(scene.Root, scene.Goal.Id).Entries.Where(entry =>
            entry.Operation == "conductor:land" && entry.Status == GoalOperationStatus.Completed));
        scene.Reload();
        scene.Advance(fallback: false);
        Assert.Equal(mainAfter, LandingExecutorTests.ReadGit(scene.Root, "rev-parse", "main"));
        Assert.Equal(1, scene.Landings);
        Assert.Equal(1, scene.AttemptCount);
        Assert.Equal(0, scene.GateLaunches);
        if (occupant is not null) scene.AssertOccupant(occupant);
    }

    [Xunit.Theory(Timeout = 30000)]
    [Xunit.Trait("Category", "CrossTick")]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void ReboundReceiptWithAdvisoryFailureReplaysBeforeCapacity(bool occupied)
    {
        using var scene = new Scene(advisoryFailure: true);
        using var census = GateLoadContextProbe.PushLiveGateOccupantProbe(() => []);
        var occupant = occupied ? scene.StartOccupant() : null;
        scene.AssertPending();
        scene.Reload();

        scene.Advance(fallback: false);

        scene.AssertSatisfied();
        Assert.Equal(1, scene.Landings);
        Assert.Equal(0, scene.GateLaunches);
        Assert.Equal(0, scene.WorkerLaunches);
        Assert.NotNull(scene.Receipt.ReplayLandedAt);
        scene.Reload();
        scene.Advance(fallback: false);
        Assert.Equal(1, scene.Landings);
        Assert.Equal(0, scene.GateLaunches);
        if (occupant is not null) scene.AssertOccupant(occupant);
    }

    [Xunit.Theory]
    [Xunit.InlineData("criterion-text")]
    [Xunit.InlineData("owner")]
    [Xunit.InlineData("scope")]
    [Xunit.InlineData("provenance")]
    [Xunit.InlineData("spec")]
    [Xunit.InlineData("task")]
    [Xunit.InlineData("candidate")]
    [Xunit.InlineData("main")]
    [Xunit.InlineData("landing-scope")]
    [Xunit.InlineData("missing-result")]
    [Xunit.InlineData("corrupt-result")]
    [Xunit.InlineData("missing-proof")]
    [Xunit.InlineData("missing-result-proof")]
    [Xunit.InlineData("missing-trx")]
    [Xunit.InlineData("unreadable-trx")]
    [Xunit.InlineData("locked-trx")]
    [Xunit.InlineData("failed-trx")]
    [Xunit.InlineData("empty-trx")]
    [Xunit.InlineData("incomplete-trx")]
    [Xunit.InlineData("advisory-missing-trx")]
    [Xunit.InlineData("advisory-unreadable-trx")]
    [Xunit.InlineData("advisory-incomplete-trx")]
    [Xunit.InlineData("wrong-binding")]
    [Xunit.InlineData("unbound")]
    public void InvalidProofCannotConsumePendingEvidence(string change)
    {
        using var scene = new Scene(advisoryFailure: change.StartsWith("advisory-", StringComparison.Ordinal));
        using var census = GateLoadContextProbe.PushLiveGateOccupantProbe(() => []);
        var occupant = scene.StartOccupant();
        var receipt = scene.Receipt;
        switch (change)
        {
            case "criterion-text":
                scene.Kernel.RecordGoalRefinement(scene.Goal.Id, scene.Goal.RefinedSpec! with
                    { AcceptanceCriteria = ["Changed required behavior", "Second behavior", "Operator proof"] });
                break;
            case "spec":
                scene.Kernel.RecordGoalRefinement(scene.Goal.Id, scene.Goal.RefinedSpec! with
                    { BehavioralContract = "Changed behavior" });
                break;
            case "task":
                scene.Kernel.RecordTaskVerification(scene.Goal.Id, scene.Goal.Tasks.Single().Id,
                    ManualVerificationRecorder.Create(true, "different verification", "fixture", DateTimeOffset.UtcNow));
                break;
            case "owner": scene.Map(1, CriterionEvidenceOwner.Operator); break;
            // These are persisted-contract controls. The supported mapping API
            // rejects invalid scopes/bindings and ignores an actor-only remap.
            case "scope": scene.ReplaceObligation(1, item => item with { RequiredScope = "different scope" }); break;
            case "provenance": scene.ReplaceObligation(1, item => item with { Provenance = "changed provenance" }); break;
            case "wrong-binding": scene.Map(1, candidate: Prior); break;
            case "unbound": scene.ReplaceObligation(1, item => item with { ExpectedCandidateSha = null }); break;
            case "candidate": scene.CurrentCandidate = Prior; break;
            case "main": scene.CurrentMain = Prior; break;
            case "landing-scope": scene.Scopes = ["src/other.cs"]; break;
            case "missing-result": File.Delete(receipt.ResultPath); break;
            case "corrupt-result": File.AppendAllText(receipt.ResultPath, " "); break;
            case "missing-proof": scene.ReplaceReceipt(receipt with { ReplayContextFingerprint = null }); break;
            case "missing-result-proof": scene.ReplaceReceipt(receipt with { ReplayResultFingerprint = null }); break;
            case "missing-trx": File.Delete(scene.TrxPaths[1]); break;
            case "unreadable-trx": File.WriteAllText(scene.TrxPaths[1], "<broken"); break;
            case "failed-trx": scene.WriteTrx(scene.TrxPaths[1], "Failed"); break;
            case "empty-trx": File.WriteAllText(scene.TrxPaths[1], "<TestRun />"); break;
            case "incomplete-trx": scene.WriteTrx(scene.TrxPaths[1], "NotExecuted", total: 2); break;
            case "advisory-missing-trx": File.Delete(scene.TrxPaths[2]); break;
            case "advisory-unreadable-trx": File.WriteAllText(scene.TrxPaths[2], "<broken"); break;
            case "advisory-incomplete-trx": scene.WriteTrx(scene.TrxPaths[2], "Failed", total: 2); break;
        }
        var evidence = JsonSerializer.Serialize(scene.Goal.CriterionEvidenceObligations);
        using var lockedTrx = change == "locked-trx" ? new FileStream(scene.TrxPaths[1], FileMode.Open, FileAccess.ReadWrite, FileShare.None) : null;

        Assert.Null(scene.Driver.TryReplayPassedAcceptance(scene.Goal, ConductorAutonomyPolicy.Conservative));

        Assert.Equal(evidence, JsonSerializer.Serialize(scene.Goal.CriterionEvidenceObligations));
        Assert.Equal(0, scene.Landings);
        Assert.Equal(0, scene.GateLaunches);
        Assert.Equal(receipt.ReconciledAt, scene.Receipt.ReconciledAt);
        Assert.Null(scene.Receipt.ReplayLandedAt);
        scene.AssertOccupant(occupant);
    }

    [Xunit.Theory]
    [Xunit.InlineData("scope")]
    [Xunit.InlineData("unbound")]
    public void InvalidMappingIsRejectedBeforeReplay(string change)
    {
        using var scene = new Scene();
        var evidence = JsonSerializer.Serialize(scene.Goal.CriterionEvidenceObligations);
        Assert.Throws<ArgumentException>(() => change == "scope"
            ? scene.Map(1, scope: "different scope") : scene.Map(1, candidate: null));
        Assert.Equal(evidence, JsonSerializer.Serialize(scene.Goal.CriterionEvidenceObligations));
        scene.AssertPending();
        Assert.Equal(0, scene.Landings);
    }

    [Xunit.Theory]
    [Xunit.InlineData("human")]
    [Xunit.InlineData("mutation")]
    [Xunit.InlineData("circuit")]
    [Xunit.InlineData("owner")]
    public void ActiveHoldCannotConsumePendingEvidence(string hold)
    {
        using var scene = new Scene();
        if (hold == "human") scene.HumanHold = true;
        if (hold == "mutation") scene.MutationAvailable = false;
        if (hold == "circuit") scene.Health = new AcceptanceEngineHealthSnapshot(
            AcceptanceEngineHealth.Unhealthy, MainA, "canary failed", "canary", DateTimeOffset.UnixEpoch);
        if (hold == "owner")
        {
            scene.Kernel.RecordAcceptanceFailure(scene.Goal.Id, ["owner-protected configuration"], scene.Candidate, scene.Main);
            GoalOperationJournal.OwnerReviewHoldEntered(scene.Root, scene.Goal, scene.Candidate, scene.Main,
                new OwnerReviewHoldReceipt("review", scene.Goal.LatestAcceptanceFailure!.OccurredAt, "Owner approval required"),
                ["owner-protected configuration"]);
        }

        var result = scene.Driver.TryReplayPassedAcceptance(scene.Goal, ConductorAutonomyPolicy.Conservative);

        Assert.True(result is null || result.IsHeld);
        scene.AssertPending();
        Assert.Equal(0, scene.Landings);
        Assert.Equal(0, scene.GateLaunches);
        Assert.Null(scene.Receipt.ReplayLandedAt);

        scene.HumanHold = false;
        scene.MutationAvailable = true;
        scene.Health = null;
        scene.Kernel.ClearAcceptanceFailure(scene.Goal.Id);
        Assert.True(scene.Driver.TryReplayPassedAcceptance(scene.Goal, ConductorAutonomyPolicy.Conservative)!.WasExecuted);
        scene.AssertSatisfied();
        Assert.Equal(1, scene.Landings);
        Assert.Equal(0, scene.GateLaunches);
    }

    [Xunit.Fact(Timeout = 30000)]
    public async Task BusyWriterCustodyLeavesEvidencePending()
    {
        using var scene = new Scene();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writer = Task.Run(() =>
        {
            using var lease = StorageRetentionMaintenance.TryAcquireAttemptWriterLease(scene.AttemptDirectory);
            Assert.NotNull(lease);
            entered.TrySetResult();
            Assert.True(release.Task.Wait(TimeSpan.FromSeconds(30)), "Writer release event did not arrive.");
        });
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Throws<AcceptanceArtifactWriterLeaseBusyException>(() =>
                scene.Driver.TryReplayPassedAcceptance(scene.Goal, ConductorAutonomyPolicy.Conservative));
            scene.AssertPending();
            Assert.Equal(0, scene.Landings);
        }
        finally { release.TrySetResult(); await writer.WaitAsync(TimeSpan.FromSeconds(30)); }
    }

    [Xunit.Fact(Timeout = 30000)]
    public async Task ReconciliationKeepsCustodyThroughOneLanding()
    {
        using var scene = new Scene();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        scene.BeforeLanding = () =>
        {
            entered.TrySetResult();
            Assert.True(release.Task.Wait(TimeSpan.FromSeconds(30)), "Landing release event did not arrive.");
        };
        var first = Task.Run(() => scene.Driver.TryReplayPassedAcceptance(scene.Goal, ConductorAutonomyPolicy.Conservative));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            scene.AssertSatisfied();
            var second = scene.Driver.TryReplayPassedAcceptance(scene.Goal, ConductorAutonomyPolicy.Conservative);
            Assert.True(second!.IsHeld);
            Assert.Equal(1, scene.ActiveMutationLeases);
            Assert.Equal(0, scene.Landings);
        }
        finally { release.TrySetResult(); }
        Assert.True((await first.WaitAsync(TimeSpan.FromSeconds(30)))!.WasExecuted);
        Assert.Equal(1, scene.Landings);
        Assert.Equal(0, scene.ActiveMutationLeases);
        Assert.Equal(0, scene.GateLaunches);
    }

    [Xunit.Theory]
    [Xunit.InlineData("main")]
    [Xunit.InlineData("human")]
    [Xunit.InlineData("circuit")]
    public void HoldAtEvidenceApplicationLeavesPendingEvidence(string hold)
    {
        using var scene = new Scene();
        var reads = 0;
        scene.BeforeHeads = () =>
        {
            if (++reads != 3) return;
            if (hold == "main") scene.CurrentMain = Prior;
            if (hold == "human") scene.HumanHold = true;
            if (hold == "circuit") scene.Health = new AcceptanceEngineHealthSnapshot(
                AcceptanceEngineHealth.Unhealthy, MainA, "canary failed", "canary", DateTimeOffset.UnixEpoch);
        };

        Assert.Null(scene.Driver.TryReplayPassedAcceptance(scene.Goal, ConductorAutonomyPolicy.Conservative));

        scene.AssertPending();
        Assert.Equal(0, scene.Landings);
        Assert.Null(scene.Receipt.ReplayLandedAt);
    }

    [Xunit.Fact]
    public void ForeignGoalCannotConsumeReceipt()
    {
        using var scene = new Scene();
        var other = CreateVerifiedSimpleGoal(scene.Kernel, "other goal");
        // Put the foreign identity on the registered path that replay actually reads.
        // Canonical custody must reject it before either evidence or landing callbacks.
        scene.ReplaceReceipt(scene.Receipt with { GoalId = other.Id.Value });
        var receipt = scene.Receipt;
        var candidate = ConductorParallelAcceptanceCandidate.Create(scene.Goal, 0, scene.Scopes, scene.Candidate, scene.Main);
        var error = Assert.Throws<InvalidDataException>(() => scene.Coordinator.ReplayPassedReconciledReceipt(candidate,
            _ => throw new InvalidOperationException("Foreign receipt must not land."),
            _ => throw new InvalidOperationException("Foreign receipt must not apply evidence.")));
        Assert.Contains($"belongs to goal '{other.Id.Value}', not canonical directory goal '{scene.Goal.Id.Value}'", error.Message);
        scene.AssertPending();
        Assert.Equal(0, scene.Landings);
        Assert.Equal(0, scene.GateLaunches);
        Assert.Equal(JsonSerializer.Serialize(receipt), JsonSerializer.Serialize(scene.Receipt));
    }

    private sealed class Scene : IDisposable
    {
        public string Root { get; }
        public AgentOrchestratorKernel Kernel { get; private set; }
        public Goal Goal { get; private set; }
        public string Candidate { get; }
        public string Main { get; }
        public string CurrentCandidate { get; set; }
        public string CurrentMain { get; set; }
        public IReadOnlyList<string> Scopes { get; set; } = ["src/rebound.txt"];
        public string[] TrxPaths { get; }
        public ConductorParallelAcceptanceAttemptCoordinator Coordinator { get; private set; } = null!;
        public ConductorDriver Driver { get; private set; } = null!;
        public int GateLaunches { get; private set; }
        public int WorkerLaunches { get; private set; }
        public int Landings { get; private set; }
        public bool HumanHold { get; set; }
        public bool MutationAvailable { get; set; } = true;
        public AcceptanceEngineHealthSnapshot? Health { get; set; }
        public Action? BeforeLanding { get; set; }
        public Action? BeforeHeads { get; set; }
        public Action? BeforeGate { get; set; }
        private int _leases;
        private string _receiptPath = null!;
        private readonly bool _native;
        public int ActiveMutationLeases => Volatile.Read(ref _leases);
        public string AttemptDirectory => Path.Combine(Root, ".orchestrator", "acceptance-gate-attempts", Goal.Id.Value);
        public int AttemptCount => Directory.GetFiles(AttemptDirectory, "*.attempt.json").Length;
        public ConductorParallelAcceptanceAttempt Receipt => JsonSerializer.Deserialize<ConductorParallelAcceptanceAttempt>(
            File.ReadAllText(_receiptPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

        public Scene(bool native = false, string? skippedOutcome = null, bool advisoryFailure = false)
        {
            _native = native;
            Root = native ? LandingExecutorTests.CreateGitRepository() : CreateTempDirectory("mcg-rebound-replay");
            if (native)
            {
                var seeded = LandingExecutorTests.CreateAcceptedCandidate(Root, "src/rebound.txt");
                Kernel = seeded.Kernel;
                Goal = seeded.Goal;
                Candidate = LandingExecutorTests.ReadGit(Root, "rev-parse", GoalWorktrees.BranchName(Goal.Id));
                Main = LandingExecutorTests.ReadGit(Root, "rev-parse", "main");
            }
            else
            {
                Kernel = new AgentOrchestratorKernel();
                Goal = CreateVerifiedSimpleGoal(Kernel, "Replay rebound exact candidate acceptance");
                Candidate = PairA;
                Main = MainA;
            }
            CurrentCandidate = Candidate;
            CurrentMain = Main;
            Kernel.RecordGoalRefinement(Goal.Id, new RefinedSpec(Goal.Objective,
                ["First behavior", "Second behavior", "Operator proof"], VerificationClass.TestVerifiable, [], []));
            for (var index = 0; index < 2; index++)
            {
                var obligation = Map(index, candidate: Prior);
                Kernel.RecordCriterionEvidence(Goal.Id, obligation.Id, obligation.Owner, Prior,
                    $"prior-{index}", obligation.RequiredScope, true, "prior gate");
            }
            var operatorProof = Kernel.MapCriterionEvidenceOwner(Goal.Id, 2, 1, CriterionEvidenceOwner.Operator,
                Provenance, "operator observation", expectedCandidateSha: Candidate);
            Kernel.RecordCriterionEvidence(Goal.Id, operatorProof.Id, operatorProof.Owner, Candidate,
                "operator-proof", operatorProof.RequiredScope, true, "operator observed");
            var artifacts = Path.Combine(Root, ".orchestrator", "replay-fixture");
            Directory.CreateDirectory(artifacts);
            TrxPaths = [Path.Combine(artifacts, "first.trx"), Path.Combine(artifacts, "second.trx")];
            foreach (var path in TrxPaths) WriteTrx(path, "Passed");
            if (skippedOutcome is not null) WriteTrx(TrxPaths[1], skippedOutcome, includePassed: true);
            AcceptanceCheckResult[] unmetCriteria = [];
            if (advisoryFailure)
            {
                var advisoryPath = Path.Combine(artifacts, "advisory.trx");
                WriteTrx(advisoryPath, "Failed");
                TrxPaths = [.. TrxPaths, advisoryPath];
                unmetCriteria = [new AcceptanceCheckResult("Advisory fixture", false, 1, null,
                    Advisory: true, TestResultPaths: [advisoryPath])];
            }
            Configure();
            var seed = Coordinator.Evaluate(ConductorParallelAcceptanceCandidate.Create(Goal, 0, Scopes, Candidate, Main),
                ConductorAutonomyPolicy.Conservative, (candidate, _, _, _, _) =>
                    ConductorParallelAcceptanceRunResult.Accepted(candidate, new AcceptanceVerificationSummary(true, unmetCriteria,
                        BranchHeadSha: Candidate, MainHeadSha: Main, TestResultPaths: TrxPaths)));
            Assert.Equal(ConductorParallelAcceptanceAttemptDecisionKind.Completed, seed.Kind);
            _receiptPath = seed.Attempt.MetadataPath;
            Coordinator.MarkReconciled(seed.Attempt);
            for (var index = 0; index < 2; index++) Map(index);
        }

        public CriterionEvidenceObligation Map(int index, CriterionEvidenceOwner owner = CriterionEvidenceOwner.Acceptance,
            string scope = CriterionEvidenceScopes.FullAcceptanceGate, string? candidate = "current", string provenance = Provenance) =>
            Kernel.MapCriterionEvidenceOwner(Goal.Id, index, 1, owner, provenance, scope,
                expectedCandidateSha: candidate == "current" ? Candidate : candidate);

        private void Configure()
        {
            Coordinator = new ConductorParallelAcceptanceAttemptCoordinator(Path.GetDirectoryName(AttemptDirectory)!, TrunkBranchName.Default,
                runInline: true, isProcessAlive: _ => true);
            Driver = MakeDriver(
                getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true, HasOpenClarification: HumanHold,
                    IsMerged: _native && GoalOperationJournal.Read(Root, Goal.Id).Entries.Any(entry =>
                        entry.Operation == "conductor:land" && entry.Status == GoalOperationStatus.Completed)),
                dispatchAndStart: _ => { WorkerLaunches++; return DispatchStartOutcome.Started(); },
                runAcceptanceWithSlot: (_, _) =>
                {
                    GateLaunches++;
                    BeforeGate?.Invoke();
                    return new AcceptanceVerificationSummary(true, [], BranchHeadSha: CurrentCandidate, MainHeadSha: CurrentMain,
                        TestResultPaths: TrxPaths);
                },
                land: goal =>
                {
                    BeforeLanding?.Invoke();
                    Landings++;
                    if (!_native)
                        return new LandingResult(goal.Id.Value, goal.Id.Value[..8], new LandingDecision.Promote(), "integration", true, "Landed");
                    // Match the production driver's journal wrapper around the
                    // native executor; the executor owns ref publication.
                    GoalOperationJournal.Begin(Root, goal, "conductor:land", "Landing goal via integration branch.");
                    var result = LandingExecutor.Execute(Kernel, goal, OrchestratorWorkspace.ForDirectory(Root));
                    if (result.MainAdvanced)
                        GoalOperationJournal.Completed(Root, goal, "conductor:land", result.Message);
                    else
                        GoalOperationJournal.Failed(Root, goal, "conductor:land", result.Message);
                    return result;
                },
                getLandingFileScopes: _ => Scopes,
                parallelAcceptanceAttemptCoordinator: Coordinator,
                resolveAcceptanceHeads: _ => { BeforeHeads?.Invoke(); return (CurrentCandidate, CurrentMain); },
                tryAcquireEvidenceMutationLease: (_, _) =>
                {
                    if (!MutationAvailable || Interlocked.CompareExchange(ref _leases, 1, 0) != 0) return null;
                    return new Lease(() => Interlocked.Exchange(ref _leases, 0));
                },
                executionDirectory: Root,
                readAcceptanceEngineHealth: () => Health);
            Driver.OverrideOwnerReviewHoldForTests(
                CollaborationItemStore.ForDirectory(Path.Combine(Root, ".orchestrator")),
                (_, _) => { }, (_, _) => "review", Kernel);
            Driver.BeginTick(Kernel, 0);
        }

        public void Reload()
        {
            var id = Goal.Id;
            Kernel = AgentOrchestratorKernel.FromSnapshot(JsonSerializer.Deserialize<OrchestratorSnapshot>(
                JsonSerializer.Serialize(Kernel.ExportSnapshot()))!);
            Goal = Kernel.GetGoal(id);
            Configure();
        }

        public void ReplaceObligation(int index, Func<CriterionEvidenceObligation, CriterionEvidenceObligation> replace)
        {
            var snapshot = Kernel.ExportSnapshot();
            var stored = snapshot.Goals.Single(item => item.Id == Goal.Id.Value);
            var original = stored.CriterionEvidenceObligations!.Single(item => item.CriterionIndex == index);
            var replacement = replace(original);
            Assert.NotEqual(original, replacement);
            Kernel = AgentOrchestratorKernel.FromSnapshot(snapshot with
            {
                Goals = snapshot.Goals.Select(item => item.Id != stored.Id ? item : item with
                {
                    CriterionEvidenceObligations = item.CriterionEvidenceObligations!.Select(obligation =>
                        obligation.Id == original.Id ? replacement : obligation).ToArray()
                }).ToArray()
            });
            Goal = Kernel.GetGoal(new GoalId(stored.Id));
            var restored = Goal.CriterionEvidenceObligations.Single(item => item.Id == replacement.Id);
            if (replacement.Owner == CriterionEvidenceOwner.Acceptance &&
                replacement.RequiredScope != CriterionEvidenceScopes.FullAcceptanceGate)
            {
                // Restore rejects an impossible Acceptance scope into an explicit
                // ownership obligation, which must still prevent receipt reuse.
                Assert.Equal(CriterionEvidenceOwner.Unknown, restored.Owner);
                Assert.Equal(CriterionEvidenceState.Pending, restored.State);
                Assert.Equal("ownership mapping required", restored.RequiredScope);
            }
            else
                Assert.Equal(replacement, restored);
            Configure();
        }

        public void Advance(bool fallback)
        {
            Driver.BeginTick(Kernel, 0);
            var policy = ConductorAutonomyPolicy.Conservative with { AcceptanceWidth = 1 };
            if (fallback) Driver.AdvanceOnce(Goal, policy);
            else new ConductorBatchLoop().Run(Kernel, Driver, policy, NoStopPath(), maxIterations: 1, onlyGoalId: Goal.Id.Value);
        }

        public void AssertPending()
        {
            var obligations = Goal.CriterionEvidenceObligations.Where(item => item.Owner == CriterionEvidenceOwner.Acceptance).ToArray();
            Assert.Equal(2, obligations.Length);
            Assert.All(obligations, item =>
            {
                Assert.Equal(CriterionEvidenceState.Pending, item.State);
                Assert.Equal(Candidate, item.ExpectedCandidateSha);
                Assert.Null(item.CandidateSha);
                Assert.Equal(Prior, Assert.Single(item.PriorReceipts!).CandidateSha);
            });
        }

        public void AssertSatisfied(bool replayed = true)
        {
            var obligations = Goal.CriterionEvidenceObligations.Where(item => item.Owner == CriterionEvidenceOwner.Acceptance).ToArray();
            Assert.Equal(2, obligations.Length);
            Assert.All(obligations, item =>
            {
                Assert.Equal(CriterionEvidenceState.Satisfied, item.State);
                Assert.Equal(Candidate, item.CandidateSha);
                Assert.Equal($"full-acceptance:{Candidate}", item.ReceiptId);
                Assert.Equal(Prior, Assert.Single(item.PriorReceipts!).CandidateSha);
                if (replayed) Assert.Contains(Receipt.AttemptId, item.Detail, StringComparison.Ordinal);
            });
            Assert.Equal("operator-proof", Assert.Single(Goal.CriterionEvidenceObligations.Where(item =>
                item.Owner == CriterionEvidenceOwner.Operator)).ReceiptId);
        }

        public void WriteTrx(string path, string outcome, bool includePassed = false, int? total = null)
        {
            var skipped = outcome is "NotExecuted" or "Skipped" ? 1 : 0;
            var passed = (outcome == "Passed" ? 1 : 0) + (includePassed ? 1 : 0);
            var failed = outcome == "Failed" ? 1 : 0;
            File.WriteAllText(path,
                $"<TestRun xmlns=\"http://microsoft.com/schemas/VisualStudio/TeamTest/2010\"><Results>" +
                (includePassed ? "<UnitTestResult testName=\"ReplayFixture.Passed\" outcome=\"Passed\" />" : "") +
                $"<UnitTestResult testName=\"ReplayFixture.Check\" outcome=\"{outcome}\" /></Results>" +
                $"<ResultSummary outcome=\"Completed\"><Counters total=\"{total ?? passed + failed + skipped}\" " +
                $"executed=\"{passed + failed}\" passed=\"{passed}\" failed=\"{failed}\" notExecuted=\"{skipped}\" /></ResultSummary></TestRun>");
        }

        public void ReplaceReceipt(ConductorParallelAcceptanceAttempt receipt) =>
            File.WriteAllText(receipt.MetadataPath, JsonSerializer.Serialize(receipt));

        public ConductorParallelAcceptanceAttempt StartOccupant()
        {
            var otherGoal = CreateVerifiedSimpleGoal(Kernel, "Unrelated live gate");
            var other = new ConductorParallelAcceptanceAttemptCoordinator(Path.GetDirectoryName(AttemptDirectory)!, TrunkBranchName.Default,
                isProcessAlive: _ => true,
                launchOwnedProcess: _ => new ConductorParallelAcceptanceOwnedProcessLaunchResult(9876));
            var decision = other.Evaluate(ConductorParallelAcceptanceCandidate.Create(otherGoal, 0,
                ["src/unrelated.cs"], Candidate, Main), ConductorAutonomyPolicy.Conservative,
                (_, _, _, _, _) => throw new InvalidOperationException("Unrelated gate must remain live."));
            Assert.Equal(ConductorParallelAcceptanceAttemptDecisionKind.Started, decision.Kind);
            return decision.Attempt;
        }

        public void AssertOccupant(ConductorParallelAcceptanceAttempt occupant)
        {
            var current = Assert.Single(Coordinator.GetCapacityReservingAttempts([occupant.GoalId]));
            Assert.Equal(occupant.AttemptId, current.AttemptId);
            Assert.Equal(occupant.Outcome, current.Outcome);
            Assert.Equal(occupant.OwnerProcessId, current.OwnerProcessId);
            Assert.Null(current.ReconciledAt);
            Assert.Null(current.ReplayLandedAt);
        }

        public void Dispose() => TryDeleteDirectory(Root);
        private sealed class Lease(Action release) : IDisposable { public void Dispose() => release(); }
    }
}
