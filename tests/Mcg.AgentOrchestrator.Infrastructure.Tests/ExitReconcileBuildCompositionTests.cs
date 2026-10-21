using Mcg.AgentOrchestrator.Core;

// Parallel-safe: each case owns its native dispatch, build storage, repository and state.
public sealed class ExitReconcileBuildCompositionTests
{
    [Fact]
    public async Task TerminalSweep_EnrolledNativeSuccess_ConsumesCanonicalBuild()
    {
        using var fixture = new ExitReconcileBuildFixture();
        var invocation = await fixture.CompleteNativeDispatch();
        Assert.Equal(WorkerBuildOutcome.Success, invocation.Outcome);
        Assert.NotEqual(Path.Combine(fixture.Workspace.ExecutionDirectory, ".orchestrator", "state.db"),
            fixture.Build.StatePath);

        Assert.Equal(1, fixture.TerminalSweep());

        fixture.AssertSuccess(invocation);
    }

    [Fact]
    public async Task BatchRecovery_EnrolledNativeSuccess_ConsumesCanonicalBuild()
    {
        using var fixture = new ExitReconcileBuildFixture();
        var invocation = await fixture.CompleteNativeDispatch();
        Assert.Equal(WorkerBuildOutcome.Success, invocation.Outcome);

        Assert.Equal(1, fixture.BatchRecovery());

        fixture.AssertSuccess(invocation);
    }

    [Fact]
    public async Task DispatchPreflight_EnrolledNativeSuccess_ConsumesCanonicalBuild()
    {
        using var fixture = new ExitReconcileBuildFixture();
        var invocation = await fixture.CompleteNativeDispatch();
        Assert.Equal(WorkerBuildOutcome.Success, invocation.Outcome);

        Assert.Equal(1, fixture.DispatchPreflight());

        fixture.AssertSuccess(invocation);
    }
}
