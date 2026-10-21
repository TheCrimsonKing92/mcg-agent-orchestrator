using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

internal static class WorkerBuildRequestScopeSupport
{
    internal static AgentOrchestratorKernel Load(WorkerBuildRequestFixture fixture) =>
        fixture.Operations.Load(fixture.GoalId.Value);

    internal static void Save(WorkerBuildRequestFixture fixture, AgentOrchestratorKernel kernel) =>
        new SqliteOrchestratorStateRepository(fixture.StatePath).SaveAsync(kernel).GetAwaiter().GetResult();

    internal static TaskId PrepareSuccessor(WorkerBuildRequestFixture fixture, bool abortStart)
    {
        var kernel = Load(fixture);
        var agent = new AgentDefinition(new AgentId("scope-tester"), "Scope tester", AgentRole.Tester,
            new ModelProfile("OpenAI", "fixture", ModelCapability.Text, SubscriptionMode.ApiKey));
        var successor = kernel.AddTask(fixture.GoalId, AgentRole.Tester, "Inspect successor scope", [agent]);
        WorkerBuildScopeDispatchPreparation.Prepare(kernel.GetGoal(fixture.GoalId), successor, fixture.RepositoryRoot, "main");
        kernel.RecordTaskDispatch(fixture.GoalId, successor.Id, new TaskDispatchRecord("fixture-worker", "fixture-successor",
            fixture.RepositoryRoot, fixture.Reference.DispatchAt.AddTicks(1)));
        WorkerBuildScopeDispatchPreparation.RecordDispatchBaseCommit(kernel, fixture.GoalId, successor,
            fixture.RepositoryRoot, fixture.Git("rev-parse", "HEAD").Trim());
        if (abortStart)
        {
            var checkpointReached = false;
            var startInvoked = false;
            var runner = new BackgroundDispatchRunner(disableProcessStart: false,
                startProcess: _ => { startInvoked = true; throw new InvalidOperationException("Unexpected process start."); });
            var rejection = Assert.Throws<DispatchRecordWriteException>(() => runner.TryStartLatestDispatch(
                kernel, fixture.GoalId, successor.Id, Path.Combine(fixture.Root, "successor-logs"),
                checkpointBeforeWorkerStart: (_, goalId, taskId, phase) =>
                {
                    Assert.Equal(DispatchRecordCheckpointPhase.BeforeProcessStart, phase);
                    checkpointReached = true;
                    throw DispatchRecordWriteException.From(new DispatchCheckpointConflictException("Controlled stale-state rejection."),
                        phase, "fixture", goalId, taskId);
                }, sandboxOptions: new(false, "fixture", "fixture")));
            Assert.True(rejection.PreservesAuthoritativeState);
            Assert.True(checkpointReached);
            Assert.False(startInvoked);
            Assert.DoesNotContain(Load(fixture).GetGoal(fixture.GoalId).Tasks, task => task.Id == successor.Id);
        }
        else Save(fixture, kernel);
        return successor.Id;
    }

    internal static WorkerBuildInvocation Invocation(WorkerBuildRequestFixture fixture) =>
        Assert.Single(Load(fixture).GetTask(fixture.GoalId, fixture.TaskId).WorkerBuildInvocations);

    internal static void AssertUnconsumed(WorkerBuildRequestFixture fixture, WorkerBuildInvocation original)
    {
        var retained = Invocation(fixture);
        Assert.Equal(original.Id, retained.Id);
        Assert.False(retained.Consumed);
        Assert.True(retained.CustodyReleased);
        Assert.Equal(original.Outcome, retained.Outcome);
        Assert.Equal(original.Version, retained.Version);
    }
}
