using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.Infrastructure.Tests;

public sealed class ProtectedProcessIdentityKillGuardTests
{
    private static readonly ProtectedProcessIdentity Identity = new(1001, 100);

    [Fact]
    public void LiveIdentityProtectsItselfAncestorsAndDescendants()
    {
        foreach (var sweep in new[] { false, true })
        {
            Assert.Equal(WorkerProcessJobs.KillProtection.Protected, Evaluate(1001, 100, sweep: sweep));
            Assert.Equal(WorkerProcessJobs.KillProtection.Protected, Evaluate(901, 100, sweep: sweep));
            Assert.Equal(WorkerProcessJobs.KillProtection.Protected, Evaluate(1101, 100, sweep: sweep));
        }
        Assert.Equal(WorkerProcessJobs.KillProtection.NotProtected, Evaluate(1101, 100, allowDescendant: true));
    }

    [Fact]
    public void ReusedPidAndItsDescendantsAreNotProtected()
    {
        foreach (var sweep in new[] { false, true })
        {
            Assert.Equal(WorkerProcessJobs.KillProtection.NotProtected, Evaluate(1001, 200, sweep: sweep));
            Assert.Equal(WorkerProcessJobs.KillProtection.NotProtected, Evaluate(1101, 200, sweep: sweep));
        }
    }

    [Fact]
    public void UnreadableMatchingPidFailsClosedForKillAndSweep()
    {
        Assert.Equal(WorkerProcessJobs.KillProtection.Unverifiable, Evaluate(1001, null, sweep: true));
        Assert.Equal(WorkerProcessJobs.KillProtection.Unverifiable,
            Evaluate(1001, null, allowDescendant: true));
    }

    private static WorkerProcessJobs.KillProtection Evaluate(int candidate, long? protectedTicks,
        bool allowDescendant = false, bool sweep = false)
    {
        long? ReadTicks(int pid) => pid == 1001 ? protectedTicks : pid == 901 ? 50 : 150;
        bool ReadAncestry(int pid, out WorkerProcessJobs.ProcessAncestryFacts facts)
        {
            facts = pid switch
            {
                1001 => new WorkerProcessJobs.ProcessAncestryFacts(901, new DateTime(100, DateTimeKind.Utc)),
                901 => new WorkerProcessJobs.ProcessAncestryFacts(0, new DateTime(50, DateTimeKind.Utc)),
                1101 => new WorkerProcessJobs.ProcessAncestryFacts(1001, new DateTime(150, DateTimeKind.Utc)),
                _ => default
            };
            return pid is 1001 or 901 or 1101;
        }
        return sweep
            ? WorkerProcessJobs.EvaluateSweepProtection(candidate, Identity, 1001, ReadTicks, ReadAncestry)
            : WorkerProcessJobs.EvaluateCanKillProtection(candidate, allowDescendant, Identity, 1001,
                ReadTicks, ReadAncestry);
    }
}
