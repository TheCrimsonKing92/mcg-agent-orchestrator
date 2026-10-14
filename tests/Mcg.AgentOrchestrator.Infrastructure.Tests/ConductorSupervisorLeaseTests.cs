using Mcg.AgentOrchestrator.App.Orchestration;

namespace Mcg.AgentOrchestrator.Infrastructure.Tests;

public sealed class ConductorSupervisorLeaseTests
{
    [Xunit.Fact]
    public void LeaseTransferRequiresCurrentPidAndStartTime()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"mcg-supervisor-lease-{Guid.NewGuid():N}");
        try
        {
            var seam = new SystemConductorSupervisorHandoffSeam(directory);
            var buildA = new ConductorSupervisorBuildIdentity("commit-a", "C:\\run-a");
            var buildB = new ConductorSupervisorBuildIdentity("commit-b", "C:\\run-b");
            var successor = new ConductorSupervisorProcessIdentity(900,
                seam.Self.StartedAt.AddMinutes(1));

            seam.Acquire(buildA);
            Assert.True(seam.IsOwner(seam.Self));
            Assert.Throws<InvalidOperationException>(() => seam.Transfer(
                seam.Self with { StartedAt = seam.Self.StartedAt.AddSeconds(1) },
                successor, buildB));
            Assert.True(seam.IsOwner(seam.Self));

            seam.Transfer(seam.Self, successor, buildB);
            Assert.False(seam.IsOwner(seam.Self));
            Assert.True(seam.IsOwner(successor));
            Assert.Throws<InvalidOperationException>(() => seam.Transfer(seam.Self, successor, buildB));
            Assert.True(seam.IsOwner(successor));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Xunit.Fact]
    public void HandoffRecordCanBeClaimedOnlyOnce()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"mcg-supervisor-record-{Guid.NewGuid():N}");
        try
        {
            var seam = new SystemConductorSupervisorHandoffSeam(directory);
            var build = new ConductorSupervisorBuildIdentity("commit-a", directory);
            seam.Acquire(build);
            var record = new ConductorSupervisorHandoffRecord(Guid.NewGuid().ToString("N"),
                seam.Self, build, new ConductorSupervisorBuildSnapshot(build.CommitSha, directory,
                    Path.Combine(directory, "Mcg.AgentOrchestrator.App.dll")),
                null, 0, null, 0, Path.Combine(directory, "ready.json"));
            var path = seam.WriteRecord(record);

            Assert.Equal(record, seam.ReadRecord(path));
            Assert.ThrowsAny<IOException>(() => seam.ReadRecord(path));
            // Even recreating the original name cannot overwrite the consumed claim.
            seam.WriteRecord(record);
            Assert.ThrowsAny<IOException>(() => seam.ReadRecord(path));
            Assert.True(seam.IsOwner(seam.Self));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
