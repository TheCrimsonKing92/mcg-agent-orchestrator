using Mcg.AgentOrchestrator.Core;
using System.Text.Json;

public sealed class WorkerBuildScopeIdentityTests
{
    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void DispatchScopeIdentitySurvivesGoalSnapshotRoundTrip(bool retained)
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Preserve pinned build scope");
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("fixture", "fixture", "C:/repo", DateTimeOffset.UtcNow));
        var identity = new WorkerBuildScopeIdentity(new string('a', 40), new string('b', 64),
            retained ? Convert.ToBase64String("exact captured bytes"u8) : null, retained ? task.LastDispatch!.DispatchedAt : null);
        kernel.RecordDispatchBaseCommit(goal.Id, task.Id, new string('c', 40), identity);

        var snapshot = JsonSerializer.Deserialize<GoalSnapshot>(JsonSerializer.Serialize(goal.ToSnapshot()))!;
        var restored = Goal.FromSnapshot(snapshot).FindTask(task.Id);

        Assert.Equal(identity, restored.LastDispatch!.WorkerBuildScope);
        Assert.Equal(identity, Assert.Single(restored.DispatchHistory).WorkerBuildScope);
    }

    [Xunit.Fact]
    public void LegacyIdentityDoesNotManufactureRetainedAuthority()
    {
        var identity = JsonSerializer.Deserialize<WorkerBuildScopeIdentity>(
            """{"GoalBaseline":"baseline","MetadataDigest":"digest"}""")!;
        Assert.Null(identity.RetainedMetadataBase64);
        Assert.Null(identity.CapturedDispatchAt);
    }
}
