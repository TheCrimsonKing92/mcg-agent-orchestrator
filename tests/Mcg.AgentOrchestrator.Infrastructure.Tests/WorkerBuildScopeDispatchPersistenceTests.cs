using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Class-owned template; isolated database and worktree copies without environment mutation.
public sealed class WorkerBuildScopeDispatchPersistenceTests(WorkerBuildScopeDispatchFixtureTemplate template)
    : IClassFixture<WorkerBuildScopeDispatchFixtureTemplate>
{
    [Fact]
    public async Task SqliteReloadRetainsScopeForCompletionAfterSuccessorPreparation()
    {
        using var fixture = template.Copy();
        var database = Path.Combine(fixture.Main, "scope-state.db");
        _ = StateDbMigrations.EnsureUpToDate(database);
        var identity = fixture.Predecessor.LastDispatch!.WorkerBuildScope;
        Assert.NotNull(identity!.RetainedMetadataBase64);
        Assert.Equal(fixture.Predecessor.LastDispatch.DispatchedAt, identity.CapturedDispatchAt);
        await new SqliteOrchestratorStateRepository(database).SaveAsync(fixture.Kernel);
        fixture.Prepare(fixture.Successor);
        var successorBytes = File.ReadAllBytes(WorkerBuildProjectScope.MetadataPath(fixture.Root));

        fixture.Kernel = await new SqliteOrchestratorStateRepository(database).LoadGoalsAsync([fixture.GoalId]);

        Assert.Null(fixture.Successor.LastDispatch);
        Assert.Equal(identity, fixture.Predecessor.LastDispatch!.WorkerBuildScope);
        Assert.Equal(identity, Assert.Single(fixture.Predecessor.DispatchHistory).WorkerBuildScope);
        fixture.Reconcile();
        Assert.Equal(WorkTaskStatus.Completed, fixture.Predecessor.Status);
        Assert.Equal(0, fixture.Builds);
        Assert.Equal(successorBytes, File.ReadAllBytes(WorkerBuildProjectScope.MetadataPath(fixture.Root)));
    }
}
