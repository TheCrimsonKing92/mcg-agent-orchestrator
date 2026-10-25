using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

[Collection(TestCollections.DotnetBuildSlots)]
public sealed class ConductorBatchLoopTestsEvidencePermitQueueScenario : ConductorBatchLoopTests
{
    public ConductorBatchLoopTestsEvidencePermitQueueScenario(ITestOutputHelper output) : base(output) { }

    [Fact]
    public void RefusedEvidenceKeepsConductorOwnedPlaceAheadOfLaterGate()
    {
        var (_, evidenceGoal) = SimpleGoal("Update src/EvidenceQueue.cs");
        var (_, gateGoal) = SimpleGoal("Update src/GateQueue.cs");
        using var scenario = new Scenario(evidenceGoal, gateGoal);
        using var gateA = scenario.Hold(0, "gate:A", BuildPermitClaimantKind.Gate);
        using var worker = scenario.Hold(1, "worker-build:incumbent", BuildPermitClaimantKind.WorkerBuild);
        var first = scenario.Evidence();
        Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.BlockedBuildSlot, first.Decision.Attempt.Outcome);
        var original = Assert.Single(scenario.Queue.Snapshot());
        Assert.Equal(scenario.EvidenceKey, original.Key);
        Assert.Equal(scenario.Start, original.FirstWaitedAt);
        Assert.Equal(Environment.ProcessId, original.OwnerPid);
        Assert.True(original.OwnerBound);
        scenario.ReconcileEvidence(first.Decision);
        Assert.True(File.Exists(scenario.Queue.TicketPath(scenario.EvidenceKey)));

        foreach (var minute in new[] { 1, 3, 5 })
        {
            scenario.AtMinute(minute);
            Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.BlockedBuildSlot, scenario.Gate().Outcome);
        }
        scenario.AtMinute(6.5);
        Assert.Equal(original.FirstWaitedAt,
            Assert.Single(scenario.Queue.Snapshot().Where(t => t.Key == scenario.EvidenceKey)).FirstWaitedAt);
        var refusedAgain = scenario.Evidence();
        Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.BlockedBuildSlot, refusedAgain.Decision.Attempt.Outcome);
        var retained = Assert.Single(scenario.Queue.Snapshot().Where(t => t.Key == scenario.EvidenceKey));
        Assert.Equal(original.FirstWaitedAt, retained.FirstWaitedAt);
        Assert.Equal(Environment.ProcessId, retained.OwnerPid);
        Assert.True(retained.OwnerBound);
        scenario.ReconcileEvidence(refusedAgain.Decision);
        scenario.AtMinute(7);
        Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.BlockedBuildSlot, scenario.Gate().Outcome);

        scenario.AtMinute(8);
        gateA.Dispose();
        Assert.True(scenario.IsFree(0));
        var deferredGate = scenario.Gate();
        Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.BlockedBuildSlot, deferredGate.Outcome);
        var deferrals = deferredGate.Output.Split('\n').Where(line => line.StartsWith("PERMIT_QUEUE_DEFER ", StringComparison.Ordinal)).ToArray();
        Assert.NotEmpty(deferrals);
        Assert.All(deferrals, line => Assert.Contains("head=" + scenario.EvidenceKey, line));
        Assert.Equal(0, scenario.GateRunCount);
        Assert.True(scenario.IsFree(0));
        Assert.Equal(0, scenario.EvidenceRunCount);

        scenario.AtMinute(8.5);
        var passed = scenario.Evidence();
        Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.Passed, passed.Decision.Attempt.Outcome);
        Assert.Equal(1, scenario.EvidenceRunCount);
        Assert.Equal(0, scenario.EvidenceRunPermit);
        Assert.False(File.Exists(scenario.Queue.TicketPath(scenario.EvidenceKey)));
        Assert.True(scenario.IsFree(0));

        // Acquisition removed the waiting interval; reconciliation also cleans a newly present ticket.
        scenario.Queue.Register(new BuildPermitClaimant(scenario.EvidenceKey,
            BuildPermitClaimantKind.FocusedEvidence, Environment.ProcessId, OwnerBound: true));
        Assert.True(File.Exists(scenario.Queue.TicketPath(scenario.EvidenceKey)));
        scenario.ReconcileEvidence(passed.Decision);
        Assert.False(File.Exists(scenario.Queue.TicketPath(scenario.EvidenceKey)));
        Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.Passed, scenario.Gate().Outcome);
        Assert.Equal(1, scenario.GateRunCount);
        Assert.Equal(0, scenario.GateRunPermit);
    }

    [Fact]
    public void InjectedExternalLauncherDoesNotCreateEvidenceTicket()
    {
        var (_, goal) = SimpleGoal("Update src/InjectedQueue.cs");
        using var scenario = new Scenario(goal, goal);
        ConductorParallelAcceptanceAttempt? launched = null;
        var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
            Path.Combine(scenario.Root, "injected"), TrunkBranchName.Default,
            isProcessAlive: _ => true,
            launchOwnedProcess: launch =>
            {
                launched = launch.Attempt;
                return new ConductorParallelAcceptanceOwnedProcessLaunchResult(8700);
            },
            buildStorageRoot: scenario.Storage, timeProvider: scenario.Clock);
        var candidate = ConductorParallelAcceptanceCandidate.Create(goal, 0, ["src/InjectedQueue.cs"], "branch", "main");
        var decision = coordinator.EvaluateFocusedEvidence(candidate, ConductorAutonomyPolicy.Conservative,
            "injected evidence", (_, _, _, _) => throw new InvalidOperationException("The launcher must not run evidence."));
        Assert.NotNull(launched);
        Assert.Equal(launched.AttemptId, decision.Attempt.AttemptId);
        Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.Running, decision.Attempt.Outcome);
        var directory = Path.Combine(scenario.Storage.RootPath, "build-slots", "wait-tickets");
        Assert.Empty(Directory.Exists(directory) ? Directory.GetFiles(directory, "*.json") : []);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void QueueIoFaultIsLoggedByTicketHelper(bool adopting)
    {
        var (_, goal) = SimpleGoal("Update src/QueueFault.cs");
        using var scenario = new Scenario(goal, goal);
        Directory.CreateDirectory(scenario.Storage.RootPath);
        File.WriteAllText(Path.Combine(scenario.Storage.RootPath, "build-slots"), "blocks queue directory creation");
        var output = AsyncLocalConsoleRouter.Capture(() =>
        {
            if (adopting)
                FocusedEvidencePermitTicket.Adopt(scenario.Storage, scenario.Clock, goal.Id, Environment.ProcessId);
            else
                FocusedEvidencePermitTicket.Remove(scenario.Storage, scenario.Clock, goal.Id);
        });
        var line = Assert.Single(output.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.Equal($"BUILD_PERMIT_QUEUE_UNAVAILABLE claimant={scenario.EvidenceKey} reason=IOException", line.TrimEnd('\r'));
    }

    private sealed class Scenario : IDisposable
    {
        public string Root { get; } = CreateTempDirectory("mcg-evidence-queue");
        private readonly Func<ProcessCommandLineSnapshot>? _priorSnapshot;
        private readonly ConductorParallelAcceptanceAttemptCoordinator _gateCoordinator;
        private readonly ConductorParallelAcceptanceAttemptCoordinator _evidenceCoordinator;
        private readonly ConductorParallelAcceptanceCandidate _gateCandidate;
        private readonly ConductorParallelAcceptanceCandidate _evidenceCandidate;
        public RecordingTimeProvider Clock { get; } = new();
        public DateTimeOffset Start { get; }
        public DotnetBuildStorageRoot Storage { get; }
        public BuildPermitWaitQueue Queue { get; }
        public string EvidenceKey { get; }
        public int GateRunCount { get; private set; }
        public int? GateRunPermit { get; private set; }
        public int EvidenceRunCount { get; private set; }
        public int? EvidenceRunPermit { get; private set; }

        public Scenario(Goal evidenceGoal, Goal gateGoal)
        {
            Storage = new DotnetBuildStorageRoot(Path.Combine(Root, "storage"));
            Queue = new BuildPermitWaitQueue(Storage, Clock);
            EvidenceKey = FocusedEvidencePermitTicket.Key(evidenceGoal.Id);
            Start = Clock.GetUtcNow();
            _gateCandidate = ConductorParallelAcceptanceCandidate.Create(gateGoal, 0, ["src/GateQueue.cs"], "branch", "main");
            _evidenceCandidate = ConductorParallelAcceptanceCandidate.Create(evidenceGoal, 0, ["src/EvidenceQueue.cs"], "branch", "main");
            _gateCoordinator = Coordinator("gate-attempts");
            _evidenceCoordinator = Coordinator("evidence-attempts");
            _priorSnapshot = DotnetBuildEnvironmentManager.ProcessCommandLineSnapshotForTests;
            DotnetBuildEnvironmentManager.ProcessCommandLineSnapshotForTests = () => new(new Dictionary<int, string>());
        }

        private ConductorParallelAcceptanceAttemptCoordinator Coordinator(string name) => new(
            Path.Combine(Root, name), TrunkBranchName.Default,
            runInline: true, timeProvider: Clock, buildPermitBusyTimeout: TimeSpan.FromSeconds(20),
            buildPermitSleep: Clock.Advance, buildStorageRoot: Storage);

        public void AtMinute(double minute) => Clock.Advance(Start.AddMinutes(minute) - Clock.GetUtcNow());
        public bool IsFree(int slot) => DotnetBuildEnvironmentManager.IsStableSlotExecutionLeaseAvailable(slot, Storage);

        public DotnetBuildEnvironmentLease Hold(int slot, string label, BuildPermitClaimantKind kind)
        {
            var environment = DotnetBuildEnvironmentManager.CreateStableSlotAttempt(slot, storageRoot: Storage) with { HolderLabel = label };
            var acquired = DotnetBuildEnvironmentManager.TryAcquireFirstAvailableBuildPermit(environment, TimeSpan.Zero,
                timeProvider: Clock, claimant: new BuildPermitClaimant(label, kind, Environment.ProcessId, kind == BuildPermitClaimantKind.WorkerBuild));
            var lease = Assert.IsType<DotnetBuildLeaseAcquisition.Acquired>(acquired).Lease;
            Assert.Equal(slot, lease.Environment.BuildPermitIndex);
            return lease;
        }

        public (ConductorParallelAcceptanceAttemptDecision Decision, string Output) Evidence()
        {
            ConductorParallelAcceptanceAttemptDecision? decision = null;
            var output = AsyncLocalConsoleRouter.Capture(() => decision = _evidenceCoordinator.EvaluateFocusedEvidence(
                _evidenceCandidate, ConductorAutonomyPolicy.Conservative, "queue evidence", (_, request, lease, _) =>
                {
                    Assert.NotNull(lease);
                    EvidenceRunCount++;
                    EvidenceRunPermit = lease.Environment.BuildPermitIndex;
                    return ConductorDriverTests.PassingPreReviewEvidence(request);
                }));
            Assert.NotNull(decision);
            return (decision, output);
        }

        public void ReconcileEvidence(ConductorParallelAcceptanceAttemptDecision decision) =>
            _evidenceCoordinator.MarkReconciled(decision.Attempt);

        public (ConductorParallelAcceptanceAttemptOutcome Outcome, string Output) Gate()
        {
            ConductorParallelAcceptanceAttemptDecision? decision = null;
            var output = AsyncLocalConsoleRouter.Capture(() => decision = _gateCoordinator.Evaluate(
                _gateCandidate, ConductorAutonomyPolicy.Conservative, (candidate, policy, lease, _, _) =>
                {
                    Assert.NotNull(lease);
                    GateRunCount++;
                    GateRunPermit = lease.Environment.BuildPermitIndex;
                    return PassingRun(candidate, policy);
                }));
            Assert.NotNull(decision);
            _gateCoordinator.MarkReconciled(decision.Attempt);
            return (decision.Attempt.Outcome, output);
        }

        public void Dispose()
        {
            DotnetBuildEnvironmentManager.ProcessCommandLineSnapshotForTests = _priorSnapshot;
            SharedTestSupport.RemoveTempDirectory(Root);
        }
    }
}
