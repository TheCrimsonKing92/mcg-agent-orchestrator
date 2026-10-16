using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: every test writes under its own temp root; holder liveness and job observation are injected.
// 2026-10-16 21:17-21:45Z: six focused-evidence attempts started by the running conductor finished and published
// settlement receipts, but the owning generation re-adopted each one every tick (Adopt returned true for its own
// generation without looking at the holder), so the goals read PRE_REVIEW_FOCUSED_EVIDENCE_RUNNING until a bounce.
public sealed class AcceptanceHolderLeaseSameGenerationAdoptTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"holder-same-gen-{Guid.NewGuid():N}");
    private readonly SpawnProcessIdentity _holder = new(4242, DateTimeOffset.UnixEpoch.AddDays(1), "dotnet.exe");
    private bool _holderAlive = true;
    private bool _jobSettled;

    public AcceptanceHolderLeaseSameGenerationAdoptTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void OwningGeneration_DoesNotAdoptAnExitedSettledHolder()
    {
        var (lease, binding, generation) = ClaimedLease();
        _holderAlive = false;
        _jobSettled = true;

        Assert.False(lease.Adopt(binding, generation));
        Assert.False(lease.Observe(binding).ReservesCapacity);
    }

    [Fact]
    public void OwningGeneration_DoesNotAdoptADeadHolderWhoseJobIsGone()
    {
        var (lease, binding, generation) = ClaimedLease();
        _holderAlive = false;

        Assert.False(lease.Adopt(binding, generation));
    }

    [Fact]
    public void OwningGeneration_KeepsALiveHolderWithoutATransition()
    {
        var (lease, binding, generation) = ClaimedLease();

        Assert.True(lease.Adopt(binding, generation));
        Assert.Equal("Claimed", lease.Read(binding).Phase);
    }

    private (AcceptanceHolderLease Lease, AcceptanceHolderBinding Binding, SpawnProcessIdentity Generation) ClaimedLease()
    {
        var lease = new AcceptanceHolderLease(
            classify: identity => identity == _holder && _holderAlive
                ? SpawnTrackedProcessStatus.LiveMatch
                : SpawnTrackedProcessStatus.DeadOrRecycled,
            job: (_, _, status, _) => _jobSettled
                ? new AcceptanceJobObservation(false, true, "job-settlement-receipt")
                : status == SpawnTrackedProcessStatus.LiveMatch
                    ? new AcceptanceJobObservation(true, false, "job-active:1")
                    : new AcceptanceJobObservation(false, false, "job-open-error:2"),
            attach: _ => new EmptyDisposable(),
            stopUnclaimed: _ => false);
        var binding = new AcceptanceHolderBinding(
            "attempt", Path.Combine(_root, "attempt.attempt.json"), "goal:branch:main", "slot-0");
        var generation = AcceptanceHolderLease.CurrentGeneration();
        lease.Reserve(binding, generation);
        using (lease.Claim(binding, _holder)) { }
        Assert.Equal("Claimed", lease.Read(binding).Phase);
        return (lease, binding, generation);
    }

    private sealed class EmptyDisposable : IDisposable
    {
        public void Dispose() { }
    }
}
