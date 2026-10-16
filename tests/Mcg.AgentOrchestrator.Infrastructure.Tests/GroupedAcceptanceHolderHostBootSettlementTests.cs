using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each test owns its root, and all decision timestamps are injected constants.
public sealed class GroupedAcceptanceHolderHostBootSettlementTests : IDisposable
{
    private static readonly DateTimeOffset Boot = new(2026, 10, 14, 6, 32, 40, TimeSpan.Zero);
    private static readonly DateTimeOffset BeforeBoot = new(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset AfterBoot = Boot.AddHours(4);
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"grouped-holder-boot-{Guid.NewGuid():N}");

    [Fact]
    public void BeforeBoot_ReleasesWithReceiptAndRemainsReleasedAfterRestart()
    {
        var events = new List<string>();
        var coordinator = Coordinator(() => Boot, events.Add);
        var legacy = WriteLegacy(coordinator);

        Assert.False(coordinator.ReservesCapacity(legacy));
        Assert.Equal(0, ConductorLiveGroupedGateCount.Count(coordinator));
        Assert.True(LegacyAcceptanceHolderSettlement.HasTerminalReceipt(AcceptanceHolderBinding.For(legacy),
            legacy.OwnerProcessId, null, null, legacy.StartedAt,
            LegacyAcceptanceHolderSettlement.PredatesHostBootEvidence));
        var release = Assert.Single(events);
        Assert.Contains("ACCEPTANCE_HOLDER_RELEASE", release, StringComparison.Ordinal);
        Assert.Contains("reason=legacy-holder-predates-host-boot", release, StringComparison.Ordinal);
        Assert.Equal("Reconciled", ConductorGroupedGateAttemptCoordinator.Read(legacy.MetadataPath).Outcome);
        Assert.False(Coordinator(() => Boot).ReservesCapacity(
            ConductorGroupedGateAttemptCoordinator.Read(legacy.MetadataPath)));
        Assert.Single(events);
    }

    [Theory]
    [InlineData("started-after")]
    [InlineData("started-equal")]
    [InlineData("exit-after")]
    [InlineData("exit-equal")]
    [InlineData("stdout-after")]
    [InlineData("stderr-after")]
    public void ActivityAtOrAfterBoot_KeepsCapacityAndHoldReason(string activity)
    {
        var events = new List<string>();
        var coordinator = Coordinator(() => Boot, events.Add);
        var legacy = WriteLegacy(coordinator);
        if (activity.StartsWith("started", StringComparison.Ordinal))
        {
            legacy = legacy with { StartedAt = activity == "started-equal" ? Boot : AfterBoot };
            ConductorGroupedGateAttemptCoordinator.Save(legacy);
        }
        else
        {
            var path = activity.StartsWith("exit", StringComparison.Ordinal) ? legacy.ExitCodePath
                : activity == "stdout-after" ? legacy.StdoutPath : legacy.StderrPath;
            File.SetLastWriteTimeUtc(path, activity == "exit-equal" ? Boot.UtcDateTime : AfterBoot.UtcDateTime);
        }

        Assert.True(coordinator.ReservesCapacity(legacy));
        Assert.Contains("reason=legacy-tree-settlement-unavailable", Assert.Single(events), StringComparison.Ordinal);
        Assert.False(File.Exists(LegacyAcceptanceHolderSettlement.TerminalReceiptPath(AcceptanceHolderBinding.For(legacy))));
    }

    [Fact]
    public void WithoutBootProvider_KeepsTheLegacyFence()
    {
        var events = new List<string>();
        var coordinator = Coordinator(null, events.Add);
        var legacy = WriteLegacy(coordinator);

        Assert.True(coordinator.ReservesCapacity(legacy));
        Assert.Contains("reason=legacy-tree-settlement-unavailable", Assert.Single(events), StringComparison.Ordinal);
    }

    [Fact]
    public void CoordinatorReconciliationAfterBoot_DoesNotCountAsHolderActivity()
    {
        var coordinator = Coordinator(null);
        var legacy = WriteLegacy(coordinator);
        coordinator.Reconcile(legacy, "coordinator-observation");
        var reconciled = ConductorGroupedGateAttemptCoordinator.Read(legacy.MetadataPath);
        // Keep the running outcome while retaining the timestamp actually written by Reconcile.
        var running = reconciled with { Outcome = "Running" };
        ConductorGroupedGateAttemptCoordinator.Save(running);

        Assert.Equal(AfterBoot, running.ReconciledAt);
        Assert.False(Coordinator(() => Boot).ReservesCapacity(running));
    }

    [Fact]
    public void DeadReconciliation_UsesBootProofWithUnknownHolderIdentity()
    {
        var coordinator = Coordinator(() => Boot);
        var legacy = WriteLegacy(coordinator);

        var reconciled = coordinator.TryReconcileDead(legacy, "boot-settled");

        Assert.Equal("Reconciled", reconciled.Outcome);
        Assert.Equal(AfterBoot, reconciled.ReconciledAt);
        Assert.False(coordinator.ReservesCapacity(reconciled));
    }

    [Fact]
    public void ProductionFactory_SettlesBeforeBootRecordWithLiveIncompletePid()
    {
        var workspace = OrchestratorWorkspace.ForDirectory(_root);
        var factory = OwnedChildAttemptCoordinatorFactory.CreateGroupedGate(workspace, null);
        var legacy = WriteLegacy(factory, Environment.ProcessId);
        var plain = new ConductorGroupedGateAttemptCoordinator(
            Path.Combine(workspace.OrchestratorDirectory, "grouped-gate-attempts"));

        Assert.True(plain.ReservesCapacity(legacy));
        Assert.False(OwnedChildAttemptCoordinatorFactory.CreateGroupedGate(workspace, null).ReservesCapacity(legacy));
    }

    [Fact]
    public void ResultAfterBoot_IsHolderActivityInTheGroupedProof()
    {
        var coordinator = Coordinator(() => Boot);
        var legacy = WriteLegacy(coordinator);
        File.WriteAllText(legacy.ResultPath, "{}");
        File.SetLastWriteTimeUtc(legacy.ResultPath, AfterBoot.UtcDateTime);

        Assert.False(LegacyAcceptanceHolderSettlement.PredatesHostBoot(legacy, Boot));
    }

    private ConductorGroupedGateAttemptCoordinator Coordinator(Func<DateTimeOffset?>? boot,
        Action<string>? events = null) => new(_root,
            classifyHolder: _ => SpawnTrackedProcessStatus.Unknown,
            holderLease: new AcceptanceHolderLease(), utcNow: () => AfterBoot,
            eventSink: events, hostBootTime: boot);

    private ConductorGroupedGateAttempt WriteLegacy(ConductorGroupedGateAttemptCoordinator coordinator,
        int processId = 24100)
    {
        var legacy = coordinator.Create("cohort", [], "main", "tree", "manifest", "identity", _root,
            ConductorAutonomyPolicy.Permissive) with
        {
            HolderContractVersion = 0, OwnerProcessId = processId, StartedAt = BeforeBoot
        };
        ConductorGroupedGateAttemptCoordinator.Save(legacy);
        foreach (var path in new[] { legacy.StdoutPath, legacy.StderrPath, legacy.ExitCodePath })
        {
            File.WriteAllText(path, "1");
            File.SetLastWriteTimeUtc(path, BeforeBoot.UtcDateTime);
        }
        return legacy;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
