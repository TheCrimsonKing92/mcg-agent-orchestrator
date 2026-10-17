using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each case owns its files and injects every process and job observation.
public sealed class ConductorGroupedGateAttemptRelaunchDrainCountTests : ConductorBatchLoopTests
{
    public ConductorGroupedGateAttemptRelaunchDrainCountTests(ITestOutputHelper output) : base(output) { }

    [Theory]
    [InlineData("cohort")]
    [InlineData("train")]
    [InlineData("follower")]
    public void LeasedV1_CountSkipsWithoutWriting(string kind)
    {
        var root = CreateTempDirectory("drain-v1-count");
        try
        {
            using var fixture = new DurableHolderFixture(root, kind);
            var before = File.ReadAllBytes(fixture.Binding.LeasePath);
            fixture.Events.Clear();

            Assert.Equal(0, ConductorGroupedGateAttemptRelaunchDrainCount.Count(fixture.Coordinator));

            Assert.Equal(before, File.ReadAllBytes(fixture.Binding.LeasePath));
            Assert.DoesNotContain(fixture.Events, line => line.StartsWith("ACCEPTANCE_HOLDER_", StringComparison.Ordinal));
            Assert.Equal(1, ConductorLiveGroupedGateCount.Count(fixture.Coordinator));
        }
        finally { TryDeleteDirectory(root); }
    }

    [Fact]
    public void DetachedV1_RepeatedDrainCountsDoNotReadopt()
    {
        var root = CreateTempDirectory("drain-detached-count");
        try
        {
            using var fixture = new DurableHolderFixture(root, "cohort");
            fixture.Coordinator.PrepareHolderDetach();
            Assert.Equal("Detached", fixture.Lease.Read(fixture.Binding).Phase);
            var before = File.ReadAllBytes(fixture.Binding.LeasePath);
            fixture.Events.Clear();

            for (var poll = 0; poll < 3; poll++)
                Assert.Equal(0, ConductorGroupedGateAttemptRelaunchDrainCount.Count(fixture.Coordinator));

            Assert.Equal("Detached", fixture.Lease.Read(fixture.Binding).Phase);
            Assert.Equal(before, File.ReadAllBytes(fixture.Binding.LeasePath));
            Assert.DoesNotContain(fixture.Events, line => line.StartsWith("ACCEPTANCE_HOLDER_ADOPT", StringComparison.Ordinal));
        }
        finally { TryDeleteDirectory(root); }
    }

    [Theory]
    [InlineData("cohort")]
    [InlineData("train")]
    [InlineData("follower")]
    public void LiveLegacyOwner_StillBlocks(string kind)
    {
        var root = CreateTempDirectory("drain-v0-count");
        try
        {
            var coordinator = new ConductorGroupedGateAttemptCoordinator(root,
                generationId: 111, isProcessAlive: pid => pid == 222);
            var attempt = ConductorLiveGroupedGateCountTests.SaveAttempt(coordinator, root, kind);
            Assert.Equal(0, attempt.HolderContractVersion);
            Assert.Equal(1, ConductorGroupedGateAttemptRelaunchDrainCount.Count(coordinator));
        }
        finally { TryDeleteDirectory(root); }
    }

    [Fact]
    public void V1WithoutLease_StillBlocks()
    {
        var root = CreateTempDirectory("drain-missing-lease");
        try
        {
            var coordinator = new ConductorGroupedGateAttemptCoordinator(root,
                generationId: 111, isProcessAlive: pid => pid == 222,
                classifyHolder: _ => SpawnTrackedProcessStatus.LiveMatch,
                holderLease: new AcceptanceHolderLease());
            var attempt = ConductorLiveGroupedGateCountTests.SaveAttempt(coordinator, root, "cohort");
            Assert.Equal(1, attempt.HolderContractVersion);
            Assert.False(File.Exists(AcceptanceHolderBinding.For(attempt).LeasePath));
            Assert.Equal(1, ConductorGroupedGateAttemptRelaunchDrainCount.Count(coordinator));
        }
        finally { TryDeleteDirectory(root); }
    }

    [Fact]
    public void MixedContracts_CountsOnlyLegacyHolder()
    {
        var root = CreateTempDirectory("drain-mixed-count");
        try
        {
            using var fixture = new DurableHolderFixture(root, "cohort");
            var legacy = ConductorLiveGroupedGateCountTests.SaveAttempt(fixture.Coordinator, root, "train")
                with { HolderContractVersion = 0 };
            ConductorGroupedGateAttemptCoordinator.Save(legacy);
            Assert.Equal(2, fixture.Coordinator.ReadAll().Count());
            Assert.Equal(1, ConductorGroupedGateAttemptRelaunchDrainCount.Count(fixture.Coordinator));
        }
        finally { TryDeleteDirectory(root); }
    }

    [Theory]
    [InlineData("reconciled")]
    [InlineData("owner-dead")]
    [InlineData("published")]
    public void SettledLegacyAttempt_DoesNotBlock(string condition)
    {
        var root = CreateTempDirectory("drain-settled-count");
        try
        {
            var exits = new AcceptanceHolderExitFixture();
            var coordinator = new ConductorGroupedGateAttemptCoordinator(root,
                generationId: 111, isProcessAlive: pid => condition != "owner-dead" && pid == 222,
                classifyHolder: condition == "published" ? exits.Classify : null);
            var attempt = ConductorLiveGroupedGateCountTests.SaveAttempt(coordinator, root, "cohort");
            Assert.Equal(0, attempt.HolderContractVersion);
            if (condition == "reconciled")
                ConductorGroupedGateAttemptCoordinator.Save(attempt with { ReconciledAt = DateTimeOffset.UnixEpoch });
            if (condition == "published")
            {
                File.WriteAllText(attempt.ResultPath, "{\"Status\":\"completed\"}");
                File.WriteAllText(attempt.ExitCodePath, "0");
                exits.RecordExit(attempt);
            }
            Assert.Equal(0, ConductorGroupedGateAttemptRelaunchDrainCount.Count(coordinator));
        }
        finally { TryDeleteDirectory(root); }
    }

    [Fact]
    public void NullCoordinator_CountsZero() =>
        Assert.Equal(0, ConductorGroupedGateAttemptRelaunchDrainCount.Count(null));

    internal sealed class DurableHolderFixture : IDisposable
    {
        private readonly IDisposable _claim;
        internal List<string> Events { get; } = [];
        internal AcceptanceHolderLease Lease { get; }
        internal ConductorGroupedGateAttemptCoordinator Coordinator { get; }
        internal ConductorGroupedGateAttempt Attempt { get; }
        internal AcceptanceHolderBinding Binding => AcceptanceHolderBinding.For(Attempt);

        internal DurableHolderFixture(string root, string kind)
        {
            var holder = new SpawnProcessIdentity(222, DateTimeOffset.UnixEpoch, "C:\\dotnet.exe");
            var generation = new SpawnProcessIdentity(111, DateTimeOffset.UnixEpoch, "C:\\dotnet.exe");
            Lease = new AcceptanceHolderLease(utcNow: () => DateTimeOffset.UnixEpoch,
                classify: _ => SpawnTrackedProcessStatus.LiveMatch,
                attach: _ => new EmptyContainment(),
                job: (_, identity, status, _) =>
                {
                    Assert.Equal(holder, identity);
                    return new(status == SpawnTrackedProcessStatus.LiveMatch,
                        status == SpawnTrackedProcessStatus.DeadOrRecycled, "test-holder");
                }, eventSink: Events.Add);
            Coordinator = new ConductorGroupedGateAttemptCoordinator(root,
                generationId: 111, isProcessAlive: pid => pid == 222,
                classifyHolder: _ => SpawnTrackedProcessStatus.LiveMatch,
                holderLease: Lease, generationIdentity: generation, eventSink: Events.Add,
                utcNow: () => DateTimeOffset.UnixEpoch);
            Attempt = ConductorLiveGroupedGateCountTests.SaveAttempt(Coordinator, root, kind);
            Assert.Equal(1, Attempt.HolderContractVersion);
            Lease.Reserve(Binding, generation);
            _claim = Lease.Claim(Binding, holder);
            Assert.Equal(holder, Lease.Read(Binding).Holder);
        }

        public void Dispose() => _claim.Dispose();
    }

    private sealed class EmptyContainment : IDisposable
    {
        public void Dispose() { }
    }
}
