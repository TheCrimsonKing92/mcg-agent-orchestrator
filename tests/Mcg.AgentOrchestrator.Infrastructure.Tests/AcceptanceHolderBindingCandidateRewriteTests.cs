using Mcg.AgentOrchestrator.App.Orchestration;

// Parallel-safe: every test writes under its own temp root; no process or clock is shared.
// 2026-10-16 13:58Z: a solo gate integrated main before acceptance, terminal persistence rewrote the
// attempt heads to the integrated candidate, and the durable lease bound to the launch candidate no
// longer matched AcceptanceHolderBinding.For(attempt). Read threw for every census, so every Verified
// goal held with "acceptance capacity state unavailable" until the binding used the launch key.
public sealed class AcceptanceHolderBindingCandidateRewriteTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"holder-binding-{Guid.NewGuid():N}");

    public AcceptanceHolderBindingCandidateRewriteTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void LeaseBoundAtLaunch_StaysReadableAfterTerminalHeadRewrite()
    {
        var launched = Attempt("launch-branch", "launch-main");
        launched = launched with { HolderContractVersion = 1, HolderCandidateKey = launched.CandidateKey };
        var lease = new AcceptanceHolderLease();
        lease.Reserve(AcceptanceHolderBinding.For(launched), AcceptanceHolderLease.CurrentGeneration());

        var terminal = launched with { BranchHeadSha = "integrated-branch", MainHeadSha = "integrated-main" };

        Assert.NotEqual(launched.CandidateKey, terminal.CandidateKey);
        Assert.Equal(AcceptanceHolderBinding.For(launched), AcceptanceHolderBinding.For(terminal));
        Assert.Equal("Reserved", lease.Read(AcceptanceHolderBinding.For(terminal)).Phase);
    }

    [Fact]
    public void RecordWrittenBeforeLaunchKey_RecoversTheLaunchCandidateFromItsOwnLease()
    {
        var launched = Attempt("launch-branch", "launch-main") with { HolderContractVersion = 1 };
        var lease = new AcceptanceHolderLease();
        lease.Reserve(AcceptanceHolderBinding.For(launched), AcceptanceHolderLease.CurrentGeneration());

        var terminal = launched with { BranchHeadSha = "integrated-branch" };

        Assert.Equal(launched.CandidateKey, AcceptanceHolderBinding.For(terminal).Candidate);
        Assert.Equal("Reserved", lease.Read(AcceptanceHolderBinding.For(terminal)).Phase);
    }

    [Fact]
    public void ALeaseForAnotherAttempt_IsNotAdoptedAndTheMismatchStillFailsClosed()
    {
        var launched = Attempt("launch-branch", "launch-main") with { HolderContractVersion = 1 };
        var lease = new AcceptanceHolderLease();
        // Same canonical metadata path, so the same lease file, but bound to a different attempt id.
        var foreign = AcceptanceHolderBinding.For(launched) with { AttemptId = "other-attempt" };
        lease.Reserve(foreign, AcceptanceHolderLease.CurrentGeneration());

        var terminal = launched with { BranchHeadSha = "integrated-branch" };

        Assert.Equal(terminal.CandidateKey, AcceptanceHolderBinding.For(terminal).Candidate);
        var error = Assert.Throws<InvalidDataException>(() => lease.Read(AcceptanceHolderBinding.For(terminal)));
        Assert.Equal("Acceptance holder lease binding or phase changed.", error.Message);
    }

    private ConductorParallelAcceptanceAttempt Attempt(string branch, string main)
    {
        var directory = Path.Combine(_root, "goal");
        Directory.CreateDirectory(directory);
        return new ConductorParallelAcceptanceAttempt("attempt", "goal", "goal", 0, branch, main,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 4321, ConductorParallelAcceptanceAttemptOutcome.Running,
            Path.Combine(directory, "out"), Path.Combine(directory, "err"), Path.Combine(directory, "exit"),
            Path.Combine(directory, "heartbeat"), Path.Combine(directory, "result"),
            Path.Combine(directory, "attempt.attempt.json"));
    }
}
