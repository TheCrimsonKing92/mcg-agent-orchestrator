using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core.Conductor;

// The two ticks operate only on the scenario's private git repository and stores.
public sealed class AcceptanceCohortWorkflowTestsMainSuspectAdmission
{
    // Git fixture setup and two bounded ticks have no elapsed-time acceptance contract.
    [Fact]
    [Trait("Category", "CrossTick")]
    public async Task MainSuspect_HoldsAdmissionUntilOperatorClear()
    {
        using var scenario = AcceptanceCohortWorkflowTestsMainSuspect.CreateScenario();
        scenario.Run();
        Assert.Equal(AcceptanceEngineHealth.Unhealthy, (await scenario.Circuit.ReadAsync()).Health);
        var verifierRuns = scenario.RunCount;
        var attempts = scenario.Driver.ParallelAcceptanceAttemptCoordinator;
        Assert.Empty(attempts.GetUnreconciledAttempts(scenario.Goals.Select(goal => goal.Id.Value).ToArray()));
        var loop = new ConductorBatchLoop(acceptanceEngineCircuit: scenario.Circuit,
            conductEventLogWriter: new ConductEventLogWriter(scenario.Workspace.ConductEventsLogPath));
        BatchTickSummary? heldTick = null;

        loop.Run(scenario.Kernel, scenario.Driver, ConductorAutonomyPolicy.Permissive,
            Path.Combine(scenario.Repo, "stop-does-not-exist"), maxIterations: 1,
            onTick: tick => heldTick = tick, persistGoalTick: (_, _) => { });

        Assert.Equal(verifierRuns, scenario.RunCount);
        Assert.Empty(attempts.GetUnreconciledAttempts(scenario.Goals.Select(goal => goal.Id.Value).ToArray()));
        Assert.NotNull(heldTick);
        foreach (var goal in scenario.Goals)
        {
            Assert.Contains(heldTick.ProgressLines!, line =>
                line.StartsWith("ADMISSION ", StringComparison.Ordinal) &&
                line.Contains($"goal={goal.Id.Value[..8]}", StringComparison.Ordinal) &&
                line.Contains("result=held reason=acceptance-engine-circuit", StringComparison.Ordinal));
            // GOAL reasons are serialized as single tokens with spaces replaced by underscores.
            Assert.Contains(heldTick.ProgressLines!, line =>
                line.StartsWith($"GOAL goal={goal.Id.Value[..8]} ", StringComparison.Ordinal) &&
                line.Contains("result=held", StringComparison.Ordinal) &&
                line.Contains("acceptance_engine_circuit_is_tripped", StringComparison.Ordinal) &&
                line.Contains("acceptance-engine_clear", StringComparison.Ordinal));
        }
        Assert.DoesNotContain(scenario.ReadLog(), record =>
            record.Detail.StartsWith("ACCEPTANCE_COHORT_ENTRY", StringComparison.Ordinal));
        AcceptanceCohortWorkflowTestsMainSuspect.AssertMembersUnrouted(scenario);

        scenario.Circuit.Clear("operator verified main repair");
        Assert.Equal(AcceptanceEngineHealth.Healthy, (await scenario.Circuit.ReadAsync()).Health);
        var logCount = scenario.ReadLog().Length;
        BatchTickSummary? admittedTick = null;
        loop.Run(scenario.Kernel, scenario.Driver, ConductorAutonomyPolicy.Permissive,
            Path.Combine(scenario.Repo, "stop-does-not-exist"), maxIterations: 1,
            onTick: tick => admittedTick = tick, persistGoalTick: (_, _) => { });

        var entry = Assert.Single(scenario.ReadLog().Skip(logCount).Where(record =>
            record.Detail.StartsWith("ACCEPTANCE_COHORT_ENTRY", StringComparison.Ordinal)));
        foreach (var goal in scenario.Goals)
            Assert.Contains(goal.Id.Value[..8], entry.Detail, StringComparison.Ordinal);
        Assert.NotNull(admittedTick);
        Assert.DoesNotContain(admittedTick.ProgressLines!, line =>
            line.Contains("reason=acceptance-engine-circuit", StringComparison.Ordinal));
        // The unchanged red identity is reused after admission; clear does not rerun its verifier.
        Assert.Equal(verifierRuns, scenario.RunCount);
        AcceptanceCohortWorkflowTestsMainSuspect.AssertMembersUnrouted(scenario);
    }
}
