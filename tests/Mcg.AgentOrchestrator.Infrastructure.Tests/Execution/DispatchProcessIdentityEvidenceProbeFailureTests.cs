using System.ComponentModel;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class DispatchProcessIdentityEvidenceProbeFailureTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ThrowingLivenessProbeExcludesOnlyThatCandidate(bool win32)
    {
        var recorded = new[] { Identity(101), Identity(202) };

        var live = DispatchProcessIdentityEvidence.GetLiveRecordedOwnerProcessIds(
            [101, 202],
            recorded,
            pid => pid == 101
                ? throw (win32 ? (Exception)new Win32Exception(5) : new InvalidOperationException("unavailable"))
                : true,
            pid => Identity(pid));

        Assert.Equal([202], live);
    }

    [Fact]
    public void ThrowingIdentityProbeExcludesOnlyThatCandidate()
    {
        var recorded = new[] { Identity(101), Identity(202) };

        var live = DispatchProcessIdentityEvidence.GetLiveRecordedOwnerProcessIds(
            [101, 202],
            recorded,
            _ => true,
            pid => pid == 101 ? throw new Win32Exception(5) : Identity(pid));

        Assert.Equal([202], live);
    }

    private static SpawnProcessIdentity Identity(int pid) =>
        new(pid, new DateTimeOffset(2026, 9, 29, 0, 0, 0, TimeSpan.Zero), $@"C:\workers\{pid}.exe");
}
