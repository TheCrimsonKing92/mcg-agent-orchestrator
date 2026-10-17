using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: every fixture owns its metadata and lease; all process observations are injected.
public sealed class GroupedGateV1AdoptionEventTests
{
    [Theory]
    [InlineData("train")]
    [InlineData("cohort")]
    public void SuccessorAdoption_RepeatedStaleCalls_EmitsOncePerGeneration(string kind)
    {
        using var fixture = new Fixture();
        var original = fixture.Launch(kind);
        Assert.Equal(1, original.HolderContractVersion);
        Assert.Null(original.AdoptedByGenerationId);
        fixture.FirstAlive = false;
        var successor = fixture.Coordinator(Fixture.SecondGeneration);

        Assert.True(successor.ShouldAdopt(original));
        var adopted = successor.Adopt(original);

        Assert.Equal(Fixture.SecondGeneration, adopted.AdoptedByGenerationId);
        Assert.Equal(ExpectedEvent(original), Assert.Single(fixture.AdoptedEvents()));
        Assert.Equal(Fixture.SecondGeneration, fixture.Read(original).AdoptedByGenerationId);

        // Each call deliberately keeps the stale, unadopted input to exercise the stored marker.
        for (var repeat = 0; repeat < 2; repeat++)
        {
            successor.Adopt(original);
            Assert.Single(fixture.AdoptedEvents());
            Assert.Equal(Fixture.SecondGeneration, fixture.Read(original).AdoptedByGenerationId);
        }
        var restartedSuccessor = fixture.Coordinator(Fixture.SecondGeneration);
        restartedSuccessor.Adopt(original);
        Assert.Single(fixture.AdoptedEvents());
        Assert.Equal(Fixture.SecondGeneration, fixture.Read(original).AdoptedByGenerationId);
    }

    [Fact]
    public void OwnGeneration_AlreadyNamed_EmitsNothing()
    {
        using var fixture = new Fixture();
        var attempt = fixture.Launch("train") with { AdoptedByGenerationId = Fixture.FirstGeneration };
        ConductorGroupedGateAttemptCoordinator.Save(attempt);
        var first = fixture.Coordinator(Fixture.FirstGeneration);
        Assert.False(first.ShouldAdopt(attempt));

        var unchanged = first.Adopt(attempt);

        Assert.Equal(attempt, unchanged);
        Assert.Equal(Fixture.FirstGeneration, fixture.Read(attempt).AdoptedByGenerationId);
        Assert.Empty(fixture.Events);
    }

    [Fact]
    public void ForeignGenerationStillAlive_CannotAdopt_ReturnsInputWithoutEvent()
    {
        using var fixture = new Fixture();
        var attempt = fixture.Launch("train");
        var successor = fixture.Coordinator(Fixture.SecondGeneration);
        Assert.False(successor.ShouldAdopt(attempt));
        Assert.False(fixture.Lease.CanAdopt(AcceptanceHolderBinding.For(attempt),
            Fixture.Identity(Fixture.SecondGeneration)));

        Assert.Same(attempt, successor.Adopt(attempt));

        Assert.Null(fixture.Read(attempt).AdoptedByGenerationId);
        Assert.Empty(fixture.Events);
    }

    [Fact]
    public void HolderMismatch_AdoptionFault_RecordsOnlyHoldAndReturnsInput()
    {
        using var fixture = new Fixture();
        var attempt = fixture.Launch("cohort");
        fixture.FirstAlive = false;
        var binding = AcceptanceHolderBinding.For(attempt);
        var record = fixture.Lease.Read(binding);
        File.WriteAllText(binding.LeasePath, JsonSerializer.Serialize(record with
        {
            Holder = Fixture.Identity(333)
        }));
        var metadataBefore = File.ReadAllBytes(attempt.MetadataPath);
        var leaseBefore = File.ReadAllBytes(binding.LeasePath);

        Assert.Same(attempt, fixture.Coordinator(Fixture.SecondGeneration).Adopt(attempt));

        Assert.Empty(fixture.AdoptedEvents());
        var hold = Assert.Single(fixture.Events);
        Assert.StartsWith("ACCEPTANCE_HOLDER_HOLD ", hold);
        Assert.Contains("reason=lease-binding-mismatch", hold);
        Assert.Equal(metadataBefore, File.ReadAllBytes(attempt.MetadataPath));
        Assert.Equal(leaseBefore, File.ReadAllBytes(binding.LeasePath));
    }

    [Theory]
    [InlineData("train")]
    [InlineData("cohort")]
    public void LegacyAdoption_SameAttemptFields_EmitsIdenticalText(string kind)
    {
        using var fixture = new Fixture();
        var attempt = fixture.Launch(kind);
        fixture.FirstAlive = false;
        fixture.Coordinator(Fixture.SecondGeneration).Adopt(attempt);
        var v1Text = Assert.Single(fixture.AdoptedEvents());
        Assert.Equal(ExpectedEvent(attempt), v1Text);
        var legacyRoot = Path.Combine(fixture.Root, "legacy");
        var legacyAttempt = attempt with
        {
            HolderContractVersion = 0,
            MetadataPath = Path.Combine(legacyRoot, "legacy.attempt.json"),
            AdoptedByGenerationId = null
        };
        ConductorGroupedGateAttemptCoordinator.Save(legacyAttempt);
        var legacyEvents = new List<string>();
        var legacy = new ConductorGroupedGateAttemptCoordinator(legacyRoot,
            generationId: Fixture.SecondGeneration, isProcessAlive: _ => true,
            generationIdentity: Fixture.Identity(Fixture.SecondGeneration), eventSink: legacyEvents.Add);

        var adopted = legacy.Adopt(legacyAttempt);
        legacy.Adopt(legacyAttempt);

        Assert.Equal(v1Text, Assert.Single(legacyEvents));
        Assert.Equal(Fixture.SecondGeneration, adopted.AdoptedByGenerationId);
        Assert.Equal(Fixture.SecondGeneration, fixture.Read(legacyAttempt).AdoptedByGenerationId);
    }

    private static string ExpectedEvent(ConductorGroupedGateAttempt attempt) =>
        $"ACCEPTANCE_COHORT_ADOPTED kind={attempt.Kind} members=goal-one+goal-two " +
        $"identity=adoption-identity owner=222 launchingGeneration=111";

    private sealed class Fixture : IDisposable
    {
        internal const int FirstGeneration = 111;
        internal const int SecondGeneration = 112;
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), $"grouped-adoption-{Guid.NewGuid():N}");
        internal List<string> Events { get; } = [];
        internal bool FirstAlive { get; set; } = true;
        internal AcceptanceHolderLease Lease { get; }
        private static SpawnProcessIdentity Holder => Identity(222);

        internal Fixture()
        {
            Lease = new(utcNow: () => DateTimeOffset.UnixEpoch,
                classify: identity => identity == Holder || identity == Identity(SecondGeneration) ||
                    (identity == Identity(FirstGeneration) && FirstAlive)
                    ? SpawnTrackedProcessStatus.LiveMatch : SpawnTrackedProcessStatus.DeadOrRecycled,
                attach: _ => new EmptyContainment(),
                job: (_, _, status, cancel) => new(status == SpawnTrackedProcessStatus.LiveMatch,
                    cancel, "injected-job"),
                stopUnclaimed: _ => throw new InvalidOperationException("Unexpected unclaimed holder"));
        }

        internal static SpawnProcessIdentity Identity(int pid) => new(pid, DateTimeOffset.UnixEpoch, "C:\\dotnet.exe");

        internal ConductorGroupedGateAttemptCoordinator Coordinator(int generation) => new(Root,
            generationId: generation, utcNow: () => DateTimeOffset.UnixEpoch,
            isProcessAlive: pid => pid == Holder.ProcessId,
            holderLease: Lease, generationIdentity: Identity(generation), eventSink: Events.Add,
            launch: attempt =>
            {
                using var claim = Lease.Claim(AcceptanceHolderBinding.For(attempt), Holder);
                return new(Holder.ProcessId, Holder.StartedAt, Holder.ImagePath);
            });

        internal ConductorGroupedGateAttempt Launch(string kind)
        {
            var first = Coordinator(FirstGeneration);
            var attempt = first.Create(kind, [], "main", "tree", "manifest", "adoption-identity", Root,
                ConductorAutonomyPolicy.Permissive) with
            {
                Members = new[] { "goal-one", "goal-two" }.Select(id => new ConductorGroupedGateMember(
                    id, "branch", "candidate", [], [], ChangeRiskTier.Behavior)).ToArray()
            };
            first.Launch(attempt);
            return Read(attempt);
        }

        internal ConductorGroupedGateAttempt Read(ConductorGroupedGateAttempt attempt) =>
            ConductorGroupedGateAttemptCoordinator.Read(attempt.MetadataPath);

        internal string[] AdoptedEvents() => Events.Where(line =>
            line.StartsWith("ACCEPTANCE_COHORT_ADOPTED ", StringComparison.Ordinal)).ToArray();

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }

    private sealed class EmptyContainment : IDisposable
    {
        public void Dispose() { }
    }
}
