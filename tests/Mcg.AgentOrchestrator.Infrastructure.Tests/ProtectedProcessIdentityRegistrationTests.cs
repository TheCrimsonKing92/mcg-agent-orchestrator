using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.Infrastructure.Tests;

public sealed class ProtectedProcessIdentityRegistrationTests
{
    [Fact]
    public void ReusedPidDoesNotRefuseRegistration()
    {
        var identity = new ProtectedProcessIdentity(4001, 100);
        long? LiveStart(int pid) => pid == 4001 ? 100 : null;
        bool Ancestry(int _, out WorkerProcessJobs.ProcessAncestryFacts facts)
        { facts = default; return false; }

        Assert.False(WorkerProcessJobs.IsProtectedRegistrationBoundary(4001, 200, identity, LiveStart, Ancestry));
    }

    [Fact]
    public void MatchingLiveIdentityRefusesRegistration()
    {
        var identity = new ProtectedProcessIdentity(4001, 100);
        long? LiveStart(int pid) => pid == 4001 ? 100 : null;
        bool Ancestry(int _, out WorkerProcessJobs.ProcessAncestryFacts facts)
        { facts = default; return false; }

        Assert.True(WorkerProcessJobs.IsProtectedRegistrationBoundary(4001, 100, identity, LiveStart, Ancestry));
    }
}
