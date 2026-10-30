using Mcg.AgentOrchestrator.App.Orchestration;

// Parallel-safe: pure evaluations with no shared state.
public sealed class ProjectConductorIdleCheckTests
{
    [Xunit.Theory]
    [Xunit.InlineData(1, 0, false, false, false, "running-workers:1")]
    [Xunit.InlineData(0, 1, false, false, false, "gate-holders:1")]
    [Xunit.InlineData(0, 0, true, false, false, "landing-in-progress")]
    [Xunit.InlineData(0, 0, false, true, false, "pending-operator-intent")]
    [Xunit.InlineData(0, 0, false, false, true, "applying-operator-intent")]
    public void EachBusyConditionNamesItsBlocker(int workers, int gates, bool landing, bool pending, bool applying, string expected)
    {
        Assert.Equal(expected, Assert.Single(ProjectConductorIdleCheck.Blockers(workers, gates, landing, pending, applying)));
    }

    [Xunit.Fact]
    public void EveryBlockerMustClearBeforeIdle()
    {
        Assert.Equal(5, ProjectConductorIdleCheck.Blockers(1, 1, true, true, true).Count);
        Assert.Equal(["gate-holders:1"], ProjectConductorIdleCheck.Blockers(0, 1, false, false, false));
        Assert.Empty(ProjectConductorIdleCheck.Blockers(0, 0, false, false, false));
    }
}
