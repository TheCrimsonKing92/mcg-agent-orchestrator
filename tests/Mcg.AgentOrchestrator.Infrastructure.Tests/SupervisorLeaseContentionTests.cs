using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;

namespace Mcg.AgentOrchestrator.Infrastructure.Tests;

public sealed class SupervisorLeaseContentionTests
{
    [Xunit.Fact]
    public void TransferRetriesOnceWhenLeaseHandleIsReleased()
    {
        using var fixture = new Fixture();
        FileStream? held = null;
        var calls = new List<int>();
        var seam = new SystemConductorSupervisorHandoffSeam(fixture.Root, attempt =>
        {
            calls.Add(attempt);
            held!.Dispose();
        });
        fixture.WriteLease(seam.Self);
        using (held = File.Open(fixture.LeasePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            seam.Transfer(seam.Self, fixture.Successor, fixture.Build);
            Assert.True(seam.IsOwner(fixture.Successor));
            Assert.Equal([1], calls);
        }
    }

    [Xunit.Fact]
    public void TransferAndOwnerCheckStopAfterFortyAttempts()
    {
        using var fixture = new Fixture();
        var calls = new List<int>();
        var seam = new SystemConductorSupervisorHandoffSeam(fixture.Root, calls.Add);
        fixture.WriteLease(seam.Self);
        using var held = File.Open(fixture.LeasePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var error = Assert.Throws<IOException>(() => seam.Transfer(seam.Self, fixture.Successor, fixture.Build));
        Assert.True((error.HResult & 0xFFFF) is 32 or 33);
        Assert.Equal(Enumerable.Range(1, 39), calls);
        calls.Clear();
        Assert.False(seam.IsOwner(seam.Self));
        Assert.Equal(Enumerable.Range(1, 39), calls);
    }

    [Xunit.Fact]
    public void NonSharingOpenFailureIsNotRetried()
    {
        using var fixture = new Fixture();
        var parentFile = Path.Combine(fixture.Root, "parent-file");
        File.WriteAllText(parentFile, "file");
        var calls = new List<int>();
        var seam = new SystemConductorSupervisorHandoffSeam(Path.Combine(parentFile, "continuity"), calls.Add);

        var error = Assert.ThrowsAny<IOException>(() => seam.Transfer(seam.Self, fixture.Successor, fixture.Build));
        Assert.False((error.HResult & 0xFFFF) is 32 or 33);
        Assert.Empty(calls);
        Assert.False(seam.IsOwner(seam.Self));
        Assert.Empty(calls);
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), $"r-{Guid.NewGuid():N}");
        internal string LeasePath => Path.Combine(Root, "conduct-supervisor.lock");
        internal ConductorSupervisorBuildIdentity Build { get; } = new("commit-b", "C:\\run-b");
        internal ConductorSupervisorProcessIdentity Successor { get; } =
            new(900, new DateTimeOffset(2026, 10, 25, 0, 0, 0, TimeSpan.Zero));

        internal Fixture() => Directory.CreateDirectory(Root);
        internal void WriteLease(ConductorSupervisorProcessIdentity owner) =>
            File.WriteAllText(LeasePath, JsonSerializer.Serialize(new { Process = owner, Build }));
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
