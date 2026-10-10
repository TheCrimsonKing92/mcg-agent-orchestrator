using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each test owns its database and journal directory; process probes are fake.
public sealed class GoalEvidenceDeadOwnerReclaimTests : IDisposable
{
    private const string LegacyOwner = "goal-evidence:conductor:dispatch:32096:instance-a";
    private const string IntegrationOperation = "conductor:developer-branch-integration";
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(30);
    private static readonly DateTimeOffset ProcessStartedAt = new(2026, 10, 10, 7, 0, 0, TimeSpan.Zero);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mcg-dead-evidence-owner", Guid.NewGuid().ToString("N"));
    private readonly Goal _goal = new AgentOrchestratorKernel().CreateGoal("Recover dead evidence owner");
    private readonly ReconcileSweepRemediationStore _store;

    public GoalEvidenceDeadOwnerReclaimTests()
    {
        Directory.CreateDirectory(_root);
        _store = new ReconcileSweepRemediationStore(Path.Combine(_root, "state.db"));
    }

    [Fact]
    public void TryBeginReclaimsDeadLegacyOwnerAndJournalsRecovery()
    {
        SeedOwner(LegacyOwner);
        var probe = new LiveOwnerPidProbe(isRunning: false, isSameProcess: false);
        var coordinator = Coordinator(probe);
        var pending = coordinator.ReadFact(_goal);
        Assert.Equal(GoalEvidenceLeaseRecoveryStatus.TerminalReclaimPending, pending?.RecoveryStatus);
        Assert.Equal(LegacyOwner, pending?.Owner);
        Assert.Equal("conductor:dispatch", pending?.Operation);
        Assert.Equal("instance-a", pending?.OperationInstanceId);

        var started = coordinator.TryBegin(_goal, IntegrationOperation);

        using var scope = started.Scope;
        Assert.NotNull(scope);
        Assert.Equal(GoalEvidenceLeaseRecoveryStatus.Reclaimed, started.LeaseFact?.RecoveryStatus);
        Assert.Equal("goal-evidence:v1:conductor:developer-branch-integration:42:successor-instance",
            _store.TryGetAcceptanceLeaseOwner(_goal.Id.Value));
        var begin = Assert.Single(GoalOperationJournal.Read(_root, _goal.Id).Entries);
        Assert.Equal(GoalOperationStatus.Begin, begin.Status);
        Assert.Equal(IntegrationOperation, begin.Operation);
        Assert.Equal("successor-instance", begin.OperationInstanceId);
        Assert.Equal("reclaimed:instance-a", begin.LeaseRecovery);
        Assert.Equal(32096, probe.ProbedPid);
        Assert.Equal(0, probe.SameProcessCalls);

        scope.Dispose();

        Assert.Null(_store.TryGetAcceptanceLeaseOwner(_goal.Id.Value));
    }

    [Fact]
    public void TryBeginPreservesLiveLegacyOwner()
    {
        SeedOwner(LegacyOwner);
        var probe = new LiveOwnerPidProbe(isRunning: true, isSameProcess: false);

        var started = Coordinator(probe).TryBegin(_goal, IntegrationOperation);

        using var scope = started.Scope;
        Assert.Null(scope);
        Assert.Equal(GoalEvidenceLeaseRecoveryStatus.Live, started.LeaseFact?.RecoveryStatus);
        Assert.Equal(LegacyOwner, started.LeaseFact?.Owner);
        Assert.Equal("conductor:dispatch", started.LeaseFact?.Operation);
        Assert.Equal("instance-a", started.LeaseFact?.OperationInstanceId);
        Assert.Equal(LegacyOwner, _store.TryGetAcceptanceLeaseOwner(_goal.Id.Value));
        Assert.Equal(32096, probe.ProbedPid);
        Assert.Equal(0, probe.SameProcessCalls);
    }

    [Theory]
    [InlineData("goal-evidence:malformed")]
    [InlineData("goal-evidence:conductor:dispatch:no-pid:instance-a")]
    [InlineData("goal-evidence:conductor:dispatch:0:instance-a")]
    [InlineData("goal-evidence:conductor:dispatch:-1:instance-a")]
    [InlineData("goal-evidence:conductor:dispatch:2147483648:instance-a")]
    [InlineData("goal-evidence:conductor:dispatch::instance-a")]
    [InlineData("goal-evidence::32096:instance-a")]
    [InlineData("goal-evidence:conductor:dispatch:32096:")]
    [InlineData("goal-evidence:v1::32096:instance-a")]
    [InlineData("GOAL-EVIDENCE:conductor:dispatch:32096:instance-a")]
    public void TryBeginFailsClosedForUnusableOwner(string owner)
    {
        SeedOwner(owner);
        var probe = new LiveOwnerPidProbe(isRunning: false, isSameProcess: false);

        var started = Coordinator(probe).TryBegin(_goal, IntegrationOperation);

        using var scope = started.Scope;
        Assert.Null(scope);
        Assert.Equal(GoalEvidenceLeaseRecoveryStatus.StateUnavailable, started.LeaseFact?.RecoveryStatus);
        Assert.Equal(owner, _store.TryGetAcceptanceLeaseOwner(_goal.Id.Value));
        Assert.Null(probe.ProbedPid);
    }

    [Fact]
    public void TryBeginFailsClosedWithoutPidProbe()
    {
        SeedOwner(LegacyOwner);

        var started = Coordinator(null).TryBegin(_goal, IntegrationOperation);

        using var scope = started.Scope;
        Assert.Null(scope);
        Assert.Equal(GoalEvidenceLeaseRecoveryStatus.StateUnavailable, started.LeaseFact?.RecoveryStatus);
        Assert.Equal(LegacyOwner, _store.TryGetAcceptanceLeaseOwner(_goal.Id.Value));
    }

    [Fact]
    public void TryBeginFailsClosedWhenLegacyProbeThrows()
    {
        SeedOwner(LegacyOwner);
        var probe = new LiveOwnerPidProbe(isRunning: false, isSameProcess: false) { ThrowOnProbe = true };

        var started = Coordinator(probe).TryBegin(_goal, IntegrationOperation);

        using var scope = started.Scope;
        Assert.Null(scope);
        Assert.Equal(GoalEvidenceLeaseRecoveryStatus.StateUnavailable, started.LeaseFact?.RecoveryStatus);
        Assert.Equal(LegacyOwner, _store.TryGetAcceptanceLeaseOwner(_goal.Id.Value));
        Assert.Equal(32096, probe.ProbedPid);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TryBeginKeepsV1ProcessStartIdentityCheck(bool isSameProcess)
    {
        const string owner = "goal-evidence:v1:conductor:developer-branch-integration:32096:instance-a";
        SeedOwner(owner);
        GoalOperationJournal.Begin(_root, _goal, IntegrationOperation, "Seed abandoned Begin",
            operationInstanceId: "instance-a", ownerProcessStartedAtUtc: ProcessStartedAt);
        var originalBegin = Assert.Single(GoalOperationJournal.Read(_root, _goal.Id).Entries);
        Assert.Equal(ProcessStartedAt, originalBegin.OwnerProcessStartedAtUtc);
        var probe = new LiveOwnerPidProbe(isRunning: true, isSameProcess: isSameProcess);

        var started = Coordinator(probe).TryBegin(_goal, IntegrationOperation);

        using var scope = started.Scope;
        Assert.Equal(32096, probe.ProbedPid);
        Assert.Equal(1, probe.SameProcessCalls);
        Assert.Equal(ProcessStartedAt, probe.ProbedStartTime);
        if (isSameProcess)
        {
            Assert.Null(scope);
            Assert.Equal(GoalEvidenceLeaseRecoveryStatus.Live, started.LeaseFact?.RecoveryStatus);
            Assert.Equal(owner, _store.TryGetAcceptanceLeaseOwner(_goal.Id.Value));
        }
        else
        {
            Assert.NotNull(scope);
            Assert.Equal(GoalEvidenceLeaseRecoveryStatus.Reclaimed, started.LeaseFact?.RecoveryStatus);
            Assert.Equal("reclaimed:instance-a", started.LeaseFact?.LatestRecovery);
            Assert.Equal(started.LeaseFact?.Owner, _store.TryGetAcceptanceLeaseOwner(_goal.Id.Value));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TryRecoverTerminalReleasesOnlyDeadLegacyOwner(bool isRunning)
    {
        SeedOwner(LegacyOwner);

        var recovered = Coordinator(new LiveOwnerPidProbe(isRunning, isSameProcess: false))
            .TryRecoverTerminal(_goal);

        Assert.Equal(isRunning ? GoalEvidenceLeaseRecoveryStatus.Live : GoalEvidenceLeaseRecoveryStatus.Reclaimed,
            recovered?.RecoveryStatus);
        Assert.Equal(isRunning ? LegacyOwner : null, _store.TryGetAcceptanceLeaseOwner(_goal.Id.Value));
        Assert.Equal(isRunning ? null : "reclaimed:instance-a", recovered?.LatestRecovery);
    }

    private void SeedOwner(string owner) =>
        Assert.True(_store.TryClaimAcceptanceLease(_goal.Id.Value, owner, LeaseDuration));

    private GoalEvidenceOperationCoordinator Coordinator(IConductLockPidProbe? probe) =>
        new(_store, _root, LeaseDuration, processId: () => 42, instanceId: () => "successor-instance",
            processStartedAtUtc: () => ProcessStartedAt, pidProbe: probe);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private sealed class LiveOwnerPidProbe(bool isRunning, bool isSameProcess) : IConductLockPidProbe
    {
        public int? ProbedPid { get; private set; }
        public DateTimeOffset? ProbedStartTime { get; private set; }
        public int SameProcessCalls { get; private set; }
        public bool ThrowOnProbe { get; init; }

        public bool IsRunning(int processId)
        {
            ProbedPid = processId;
            if (ThrowOnProbe)
            {
                throw new InvalidOperationException("injected owner liveness failure");
            }

            return isRunning;
        }

        public bool IsSameProcess(int processId, DateTimeOffset processStartedAt)
        {
            SameProcessCalls++;
            ProbedStartTime = processStartedAt;
            return isSameProcess;
        }
    }
}
