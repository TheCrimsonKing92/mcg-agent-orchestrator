using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core.Conductor;

public sealed class ConductorBatchLoopTestsMainSuspectRelease
{
    [Fact]
    public async Task TickContinuesWhileProbeIsInFlightAndDrainsBeforeReturning()
    {
        using var scenario = AcceptanceCohortWorkflowTestsMainSuspect.CreateScenario();
        scenario.Run();
        var tip = new AcceptanceEngineMainSuspectReleaseTests.TipReader();
        var probe = new AcceptanceEngineMainSuspectReleaseTests.ProbeStub(MainSuspectReleaseProbeOutcome.Passed);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource<MainSuspectReleaseProbeResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var tick = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lines = new List<string>();
        probe.Handler = (_, _, _) => { entered.SetResult(); return finish.Task; };
        var release = new AcceptanceEngineMainSuspectRelease(scenario.Events, scenario.Circuit, tip, probe, lines.Add);
        var loop = new ConductorBatchLoop(acceptanceEngineCircuit: scenario.Circuit).WithMainSuspectRelease(release);
        var run = Task.Run(() => loop.Run(scenario.Kernel, scenario.Driver, ConductorAutonomyPolicy.Permissive,
            Path.Combine(scenario.Repo, "stop-does-not-exist"), maxIterations: 1,
            onTick: _ => tick.SetResult(), persistGoalTick: (_, _) => { }));
        try
        {
            await entered.Task;
            await tick.Task;
            Assert.False(run.IsCompleted);
            Assert.Single(probe.Calls);
            Assert.Equal(AcceptanceEngineHealth.Unhealthy, (await scenario.Circuit.ReadAsync()).Health);
        }
        finally
        {
            finish.SetResult(new(MainSuspectReleaseProbeOutcome.Passed, "loop-probe-receipt"));
            await run;
        }
        Assert.Equal(AcceptanceEngineHealth.Healthy, (await scenario.Circuit.ReadAsync()).Health);
        Assert.Contains("result=released", Assert.Single(lines));
    }
}
