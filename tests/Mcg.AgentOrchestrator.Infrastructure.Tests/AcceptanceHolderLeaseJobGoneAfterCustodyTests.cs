using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: every test writes under its own temp root; holder liveness and job observation are injected.
// 2026-10-16 20:12-21:10Z: an activation revert killed a gate holder's whole tree, custodian included, before the
// custodian published a settlement receipt. The named kill-on-close job then no longer existed (job-open-error:2)
// and the dead attempt held width-1 admission for 50 minutes.
public sealed class AcceptanceHolderLeaseJobGoneAfterCustodyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"holder-job-gone-{Guid.NewGuid():N}");
    private readonly SpawnProcessIdentity _holder = new(4242, DateTimeOffset.UnixEpoch.AddDays(1), "dotnet.exe");
    private bool _holderAlive = true;
    private bool _jobExists = true;

    public AcceptanceHolderLeaseJobGoneAfterCustodyTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void ClaimedHolderDeadAndJobGone_ReleasesCapacity()
    {
        var (lease, binding) = ClaimedLease();
        _holderAlive = false;
        _jobExists = false;

        var observation = lease.Observe(binding);

        Assert.False(observation.ReservesCapacity, observation.Reason);
        Assert.Equal("job-gone-after-custody", observation.Reason);
        Assert.Equal("Released", lease.Read(binding).Phase);
    }

    [Fact]
    public void ClaimedHolderAliveAndJobGone_KeepsTheFence()
    {
        var (lease, binding) = ClaimedLease();
        _jobExists = false;

        var observation = lease.Observe(binding);

        Assert.True(observation.ReservesCapacity, observation.Reason);
        Assert.Equal("Claimed", lease.Read(binding).Phase);
    }

    [Fact]
    public void ReservedButNeverClaimed_KeepsTheFence()
    {
        var lease = Lease();
        var binding = Binding();
        lease.Reserve(binding, AcceptanceHolderLease.CurrentGeneration());
        _holderAlive = false;
        _jobExists = false;

        var observation = lease.Observe(binding);

        Assert.True(observation.ReservesCapacity, observation.Reason);
        Assert.Equal("holder-claim-pending", observation.Reason);
    }

    private (AcceptanceHolderLease Lease, AcceptanceHolderBinding Binding) ClaimedLease()
    {
        var lease = Lease();
        var binding = Binding();
        lease.Reserve(binding, AcceptanceHolderLease.CurrentGeneration());
        using (lease.Claim(binding, _holder)) { }
        Assert.Equal("Claimed", lease.Read(binding).Phase);
        return (lease, binding);
    }

    private AcceptanceHolderLease Lease() => new(
        classify: identity => identity == _holder && _holderAlive
            ? SpawnTrackedProcessStatus.LiveMatch
            : SpawnTrackedProcessStatus.DeadOrRecycled,
        job: (_, _, status, _) => _jobExists
            ? new AcceptanceJobObservation(status == SpawnTrackedProcessStatus.LiveMatch, false, "job-active:1")
            : new AcceptanceJobObservation(false, false, "job-open-error:2"),
        attach: _ => new EmptyDisposable(),
        stopUnclaimed: _ => false);

    private AcceptanceHolderBinding Binding() =>
        new("attempt", Path.Combine(_root, "attempt.attempt.json"), "goal:branch:main", "slot-0");

    private sealed class EmptyDisposable : IDisposable
    {
        public void Dispose() { }
    }
}
