using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class GoalPriorityPersistenceTests
{
    [Xunit.Fact]
    public async Task NonNormalPrioritiesSurviveRepositoryReload()
    {
        var repository = new SqliteOrchestratorStateRepository(TempDb());
        var kernel = new AgentOrchestratorKernel();
        var critical = kernel.CreateGoal("Critical");
        var high = kernel.CreateGoal("High");
        kernel.SetGoalPriority(critical.Id, GoalPriority.Critical, "test", "setup");
        kernel.SetGoalPriority(high.Id, GoalPriority.High, "test", "setup");
        await repository.SaveAsync(kernel);
        var restored = await repository.LoadAsync();
        Assert.Equal(GoalPriority.Critical, restored.GetGoal(critical.Id).Priority);
        Assert.Equal(GoalPriority.High, restored.GetGoal(high.Id).Priority);
    }

    [Xunit.Fact]
    public void MissingFieldDefaultsToNormalAndNormalSnapshotOmitsIt()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Normal");
        var normalSnapshot = kernel.ExportSnapshot().Goals.Single();
        Assert.Null(normalSnapshot.Priority);
        var json = JsonSerializer.Serialize(normalSnapshot);
        Assert.DoesNotContain("\"Priority\"", json);
        var snapshot = JsonSerializer.Deserialize<GoalSnapshot>(json)!;
        var restored = AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot([snapshot], []));
        Assert.Equal(GoalPriority.Normal, restored.GetGoal(goal.Id).Priority);
    }

    [Xunit.Theory]
    [Xunit.InlineData("urgent-ish")]
    [Xunit.InlineData("2")]
    public void UnknownStoredLevelDefaultsToNormal(string level)
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Unknown priority");
        var restored = AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot([kernel.ExportSnapshot().Goals.Single() with { Priority = level }], []));
        Assert.Equal(GoalPriority.Normal, restored.GetGoal(goal.Id).Priority);
    }

    [Xunit.Fact]
    public async Task TickMergePreservesTickPriorityAndConcurrentOperatorEdit()
    {
        var repository = new SqliteOrchestratorStateRepository(TempDb());
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Merge priority");
        await repository.SaveAsync(kernel);
        var tick = await repository.LoadAsync();
        var baseline = tick.ExportSnapshot();
        var operatorKernel = await repository.LoadAsync();
        operatorKernel.SetGoalSourceBacklogItemId(goal.Id, "operator-backlog");
        await repository.SaveAsync(operatorKernel);
        tick.SetGoalPriority(goal.Id, GoalPriority.Critical, "test", "tick");
        var current = tick.ExportSnapshot();
        await repository.SaveGoalSnapshotsWithMergeAsync([
            new GoalSnapshotSaveRequest(baseline.Goals.Single(), current.Goals.Single(), current.HumanInputRequests)]);
        var restored = (await repository.LoadAsync()).GetGoal(goal.Id);
        Assert.Equal(GoalPriority.Critical, restored.Priority);
        Assert.Equal("operator-backlog", restored.SourceBacklogItemId);
    }

    private static string TempDb()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"mcg-priority-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "state.db");
        StateDbMigrations.EnsureUpToDate(path);
        return path;
    }
}
