using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

[Collection(TestCollections.DotnetBuildSlots)]
public sealed class ConductorBatchLoopTestsGatePermitQueueScenario : ConductorBatchLoopTests
{
    public ConductorBatchLoopTestsGatePermitQueueScenario(ITestOutputHelper output) : base(output) { }

    [Fact]
    public void YieldingGateKeepsPlaceAcrossAttemptsAndWorkerRetries()
    {
        var (_, goal) = SimpleGoal("Update src/QueueScenario.cs");
        using var scenario = new Scenario(goal);
        using var worker = scenario.Hold(0, "worker-build:incumbent", BuildPermitClaimantKind.WorkerBuild);
        using var focused = scenario.Hold(1, "pre-review-evidence:incumbent", BuildPermitClaimantKind.FocusedEvidence);
        var releasedSlot = worker.Environment.BuildPermitIndex!.Value;
        var yielded = scenario.Gate();
        Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.BlockedBuildSlot, yielded.Outcome);
        Assert.Equal(0, scenario.RunCount);
        var busyLine = Assert.Single(yielded.Output.Split('\n').Where(line => line.StartsWith("SLOTS_BUSY ", StringComparison.Ordinal)));
        Assert.Contains("holder-worker-build:incumbent", busyLine);
        Assert.Contains("holder-pre-review-evidence:incumbent", busyLine);
        Assert.DoesNotContain("holder-unknown", busyLine);
        Assert.True(File.Exists(scenario.Queue.TicketPath(scenario.GateKey)));

        scenario.Clock.Advance(TimeSpan.FromSeconds(30));
        var w2 = scenario.Worker("w2");
        Assert.IsType<DotnetBuildLeaseAcquisition.SlotsBusy>(scenario.Acquire(w2));
        worker.Dispose();
        DotnetBuildLeaseAcquisition? second = null;
        var output = AsyncLocalConsoleRouter.Capture(() => second = scenario.Acquire(w2));
        Assert.IsType<DotnetBuildLeaseAcquisition.SlotsBusy>(second);
        Assert.Contains("PERMIT_QUEUE_DEFER", output);
        Assert.Contains("head=" + scenario.GateKey, output);
        Assert.True(scenario.IsFree(releasedSlot));

        scenario.Clock.Advance(TimeSpan.FromSeconds(60));
        var passed = scenario.Gate();
        Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.Passed, passed.Outcome);
        Assert.Equal(1, scenario.RunCount);
        Assert.Equal(releasedSlot, scenario.RunPermit);
        Assert.False(File.Exists(scenario.Queue.TicketPath(scenario.GateKey)));
    }

    [Fact]
    public void FiveMinuteGateGoesAheadOfOlderRefreshedWorker()
    {
        var (_, goal) = SimpleGoal("Update src/PrecedenceScenario.cs");
        using var scenario = new Scenario(goal);
        using var first = scenario.Hold(0, "first", BuildPermitClaimantKind.WorkerBuild);
        using var second = scenario.Hold(1, "second", BuildPermitClaimantKind.FocusedEvidence);
        var w1 = scenario.Worker("w1");
        var original = scenario.Queue.Register(w1);
        foreach (var minute in new[] { 1, 3, 5 })
        {
            scenario.AtMinute(minute);
            Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.BlockedBuildSlot, scenario.Gate().Outcome);
            scenario.AtMinute(minute + 1);
            Assert.Equal(original.FirstWaitedAt, scenario.Queue.Register(w1).FirstWaitedAt);
        }
        Assert.Equal(0, scenario.RunCount);
        first.Dispose();
        Assert.IsType<DotnetBuildLeaseAcquisition.SlotsBusy>(scenario.Acquire(w1));
        Assert.True(scenario.IsFree(0));
        Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.Passed, scenario.Gate().Outcome);
        Assert.Equal(1, scenario.RunCount);
        Assert.Equal(first.Environment.BuildPermitIndex, scenario.RunPermit);
    }

    [Fact]
    public void YoungerGateLeavesReleasedPermitForEarlierWorker()
    {
        var (_, goal) = SimpleGoal("Update src/YoungerScenario.cs");
        using var scenario = new Scenario(goal);
        using var first = scenario.Hold(0, "first", BuildPermitClaimantKind.WorkerBuild);
        using var second = scenario.Hold(1, "second", BuildPermitClaimantKind.FocusedEvidence);
        var w1 = scenario.Worker("w1");
        scenario.Queue.Register(w1);
        scenario.AtMinute(1);
        Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.BlockedBuildSlot, scenario.Gate().Outcome);
        scenario.AtMinute(2);
        first.Dispose();
        var gate = scenario.Gate();
        Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.BlockedBuildSlot, gate.Outcome);
        Assert.Contains("PERMIT_QUEUE_DEFER", gate.Output);
        Assert.True(scenario.IsFree(0));
        Assert.Equal(0, scenario.RunCount);
        using var worker = Assert.IsType<DotnetBuildLeaseAcquisition.Acquired>(scenario.Acquire(w1)).Lease;
        Assert.Equal(first.Environment.BuildPermitIndex, worker.Environment.BuildPermitIndex);
    }

    [Fact]
    public void AbandonedGateExpiresAndWorkerAcquiresReleasedPermit()
    {
        var (_, goal) = SimpleGoal("Update src/StaleScenario.cs");
        using var scenario = new Scenario(goal);
        using var first = scenario.Hold(0, "first", BuildPermitClaimantKind.WorkerBuild);
        using var second = scenario.Hold(1, "second", BuildPermitClaimantKind.FocusedEvidence);
        Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.BlockedBuildSlot, scenario.Gate().Outcome);
        Assert.True(File.Exists(scenario.Queue.TicketPath(scenario.GateKey)));
        scenario.Clock.Advance(BuildPermitWaitQueue.TicketStaleAfter);
        first.Dispose();
        using var worker = Assert.IsType<DotnetBuildLeaseAcquisition.Acquired>(scenario.Acquire(scenario.Worker("w2"))).Lease;
        Assert.Equal(first.Environment.BuildPermitIndex, worker.Environment.BuildPermitIndex);
        Assert.False(File.Exists(scenario.Queue.TicketPath(scenario.GateKey)));
        Assert.Equal(0, scenario.RunCount);
    }

    [Fact]
    public void AdmittedGateReclaimsDeadHolderThroughExistingLeasePath()
    {
        var (_, goal) = SimpleGoal("Update src/ReclaimScenario.cs");
        using var scenario = new Scenario(goal);
        var environment = DotnetBuildEnvironmentManager.CreateAttempt(goal.Id, "dead-holder", storageRoot: scenario.Storage);
        var permit = environment.BuildPermitIndex!.Value;
        using var live = scenario.Hold(1 - permit, "live", BuildPermitClaimantKind.WorkerBuild);
        Directory.CreateDirectory(Path.GetDirectoryName(environment.ExecutionLockPath)!);
        File.WriteAllText(environment.ExecutionLockPath, "999999");
        scenario.Queue.Register(new BuildPermitClaimant(scenario.GateKey, BuildPermitClaimantKind.Gate, 999999, false));
        var gate = scenario.Gate();
        Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.Passed, gate.Outcome);
        Assert.Contains("LEASE_RECLAIM", gate.Output);
        Assert.Contains("reclaimedPid=999999", gate.Output);
        Assert.Equal(1, scenario.RunCount);
        Assert.Equal(permit, scenario.RunPermit);
    }

    private sealed class Scenario : IDisposable
    {
        private readonly string _root = CreateTempDirectory("mcg-gate-queue");
        private readonly Func<ProcessCommandLineSnapshot>? _priorSnapshot;
        private readonly ConductorParallelAcceptanceAttemptCoordinator _coordinator;
        private readonly ConductorParallelAcceptanceCandidate _candidate;
        private readonly DateTimeOffset _start;
        public RecordingTimeProvider Clock { get; } = new();
        public DotnetBuildStorageRoot Storage { get; }
        public BuildPermitWaitQueue Queue { get; }
        public string GateKey { get; }
        public int RunCount { get; private set; }
        public int? RunPermit { get; private set; }

        public Scenario(Goal goal)
        {
            Storage = new DotnetBuildStorageRoot(Path.Combine(_root, "storage"));
            Queue = new BuildPermitWaitQueue(Storage, Clock);
            GateKey = "gate:goal-" + goal.Id.Value;
            _start = Clock.GetUtcNow();
            _candidate = ConductorParallelAcceptanceCandidate.Create(goal, 0, ["src/QueueScenario.cs"], "branch", "main");
            _coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
                Path.Combine(_root, "attempts"), Mcg.AgentOrchestrator.Infrastructure.TrunkBranchName.Default,
                runInline: true, timeProvider: Clock, buildPermitBusyTimeout: TimeSpan.FromSeconds(20),
                buildPermitSleep: Clock.Advance, buildStorageRoot: Storage);
            _priorSnapshot = DotnetBuildEnvironmentManager.ProcessCommandLineSnapshotForTests;
            DotnetBuildEnvironmentManager.ProcessCommandLineSnapshotForTests = () => new(new Dictionary<int, string>());
        }

        public void AtMinute(int minute) => Clock.Advance(_start.AddMinutes(minute) - Clock.GetUtcNow());
        public BuildPermitClaimant Worker(string key) => new("worker-build:" + key,
            BuildPermitClaimantKind.WorkerBuild, Environment.ProcessId, true);
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

        public DotnetBuildLeaseAcquisition Acquire(BuildPermitClaimant claimant) =>
            DotnetBuildEnvironmentManager.TryAcquireFirstAvailableBuildPermit(
                DotnetBuildEnvironmentManager.CreateAttempt(GoalId.New(), "worker", storageRoot: Storage),
                TimeSpan.Zero, timeProvider: Clock, sleep: Clock.Advance, claimant: claimant);

        public (ConductorParallelAcceptanceAttemptOutcome Outcome, string Output) Gate()
        {
            ConductorParallelAcceptanceAttemptDecision? decision = null;
            var output = AsyncLocalConsoleRouter.Capture(() => decision = _coordinator.Evaluate(
                _candidate, ConductorAutonomyPolicy.Conservative, (candidate, policy, lease, _, _) =>
                {
                    Assert.NotNull(lease);
                    RunCount++;
                    RunPermit = lease.Environment.BuildPermitIndex;
                    return PassingRun(candidate, policy);
                }));
            Assert.NotNull(decision);
            _coordinator.MarkReconciled(decision.Attempt);
            return (decision.Attempt.Outcome, output);
        }

        public void Dispose()
        {
            DotnetBuildEnvironmentManager.ProcessCommandLineSnapshotForTests = _priorSnapshot;
            SharedTestSupport.RemoveTempDirectory(_root);
        }
    }
}
