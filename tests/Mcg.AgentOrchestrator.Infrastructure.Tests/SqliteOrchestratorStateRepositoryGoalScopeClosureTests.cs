using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: every case owns a unique temporary SQLite store.
public sealed class SqliteOrchestratorStateRepositoryGoalScopeClosureTests
{
    [Fact]
    public async Task ExplicitGoalAndClosureRoot_LoadsExactlyForwardReachableGoals()
    {
        var repository = CreateMigratedStateRepository(Path.Combine(CreateTempDirectory(), "state.db"));
        var kernel = new AgentOrchestratorKernel();
        var a = kernel.CreateGoal("Explicit goal A");
        var b = kernel.CreateGoal("Closure root B");
        var c = kernel.CreateGoal("Reachable goal C");
        var d = kernel.CreateGoal("Reachable goal D");
        var unrelated = kernel.CreateGoal("Unrelated goal U");
        kernel.SetGoalDependency(b.Id, c.Id);
        kernel.SetGoalDependency(c.Id, d.Id);
        // Incoming edges must not pull unrelated goals into the forward closure.
        kernel.SetGoalDependency(unrelated.Id, b.Id);
        await repository.SaveAsync(kernel);
        var scope = GoalCreationLoadScope.ForGoals([a.Id], [b.Id]);
        var callbackRan = false;

        await repository.TransactGoalCreationWithOutboxAsync(scope, (loaded, _) =>
        {
            callbackRan = true;
            Assert.Equal(new[] { a.Id.Value, b.Id.Value, c.Id.Value, d.Id.Value }.Order(StringComparer.Ordinal),
                loaded.Goals.Select(goal => goal.Id.Value).Order(StringComparer.Ordinal));
            return Task.FromResult((ShouldSave: false, Result: true,
                OutboxMessages: (IReadOnlyList<OrchestratorStateOutboxMessage>)[]));
        });

        Assert.True(callbackRan);
    }

    [Fact]
    public async Task ExistingCycleAndDanglingEdge_VisitsEachStoredGoalOnce()
    {
        var repository = CreateMigratedStateRepository(Path.Combine(CreateTempDirectory(), "state.db"));
        var kernel = new AgentOrchestratorKernel();
        var b = kernel.CreateGoal("Closure root B");
        var c = kernel.CreateGoal("Reachable goal C");
        kernel.CreateGoal("Unrelated goal");
        var snapshot = kernel.ExportSnapshot();
        kernel = AgentOrchestratorKernel.FromSnapshot(snapshot with
        {
            Goals = snapshot.Goals.Select(goal => goal.Id == b.Id.Value
                ? goal with { DependsOn = [c.Id.Value] }
                : goal.Id == c.Id.Value
                    ? goal with { DependsOn = [b.Id.Value, GoalId.New().Value] }
                    : goal).ToArray()
        });
        await repository.SaveAsync(kernel);
        var callbackRan = false;

        await repository.TransactGoalCreationWithOutboxAsync(GoalCreationLoadScope.ForGoals([], [b.Id, b.Id]),
            (loaded, _) =>
            {
                callbackRan = true;
                Assert.Equal(new[] { b.Id.Value, c.Id.Value }.Order(StringComparer.Ordinal),
                    loaded.Goals.Select(goal => goal.Id.Value).Order(StringComparer.Ordinal));
                return Task.FromResult((ShouldSave: false, Result: true,
                    OutboxMessages: (IReadOnlyList<OrchestratorStateOutboxMessage>)[]));
            });

        Assert.True(callbackRan);
    }
}
