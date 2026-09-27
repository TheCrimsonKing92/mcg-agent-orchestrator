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
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
