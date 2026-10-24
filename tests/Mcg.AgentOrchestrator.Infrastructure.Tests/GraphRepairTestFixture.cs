using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

internal sealed class GraphRepairTestFixture : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "mcg-graph-repair-" + Guid.NewGuid().ToString("N"));
    public OrchestratorWorkspace Workspace { get; }
    public SqliteOrchestratorStateRepository Repository { get; }
    public SqliteOperatorIntentStore Store { get; }
    public Goal Goal { get; }
    public Goal Other { get; }
    public static TaskGraphRepairOperatorIntentPayload FiveRoles => new([
        new("Planner", "Plan", "Developer"), new("Researcher", "Research", "Planner"),
        new("Tester", "Test", "Reviewer")], ["Researcher", "Planner", "Developer", "Tester", "Reviewer"]);

    public GraphRepairTestFixture()
    {
        Directory.CreateDirectory(Root);
        Workspace = OrchestratorWorkspace.ForDirectory(Root);
        Repository = InfrastructureTestSupport.CreateMigratedStateRepository(Workspace.SqliteStatePath);
        Store = SqliteOperatorIntentStore.ForDirectories(Workspace.OrchestratorDirectory, Workspace.LogDirectory);
        var kernel = new AgentOrchestratorKernel();
        Goal = kernel.CreateGoal("Repair target", [new(TaskId.New(), "Implement", AgentRole.Developer), new(TaskId.New(), "Review", AgentRole.Reviewer)]);
        Other = kernel.CreateGoal("Other goal", [new(TaskId.New(), "Unrelated", AgentRole.Developer)]);
        Repository.SaveAsync(kernel).GetAwaiter().GetResult();
    }

    public OperatorIntentRecord Enqueue(TaskGraphRepairOperatorIntentPayload? plan = null, string? key = null) =>
        Store.EnqueueAsync(new(Guid.NewGuid().ToString("N"), key ?? Guid.NewGuid().ToString("N"),
            OperatorIntentVerbs.TaskGraphRepair, Goal.Id.Value, null,
            JsonSerializer.Serialize(plan ?? FiveRoles, OperatorIntentJson.Options), [],
            "operator", "cli", "local-process", DateTimeOffset.UtcNow)).GetAwaiter().GetResult();

    public AgentOrchestratorKernel Apply(bool complete = true)
    {
        var kernel = Repository.LoadGoalsAsync([Goal.Id]).GetAwaiter().GetResult();
        var baseline = kernel.ExportGoalSnapshot(Goal.Id);
        var coordinator = new OperatorIntentCoordinator(Store);
        var result = coordinator.ExecutePending(kernel, kernel.GetGoal(Goal.Id));
        if (result.MutatedGoalState)
        {
            var saved = Repository.SaveGoalSnapshotsWithMergeAsync([new(baseline,
                kernel.ExportGoalSnapshot(Goal.Id), [], RejectConflict: true)]).GetAwaiter().GetResult();
            Assert.Equal(GoalSnapshotSaveDisposition.Saved, Assert.Single(saved).Disposition);
        }
        if (complete) coordinator.CompletePersisted([Goal.Id]);
        return kernel;
    }

    public TaskIntentAdmissionReference Prepare(Goal? goal = null)
    {
        goal ??= Repository.LoadGoalsAsync([Goal.Id]).GetAwaiter().GetResult().GetGoal(Goal.Id);
        var task = goal.Tasks[0];
        var reference = Store.CaptureAdmission(goal.Id.Value, task.Id.Value, TaskGraphIdentity.Compute(goal), Workspace.SqliteStatePath);
        var at = DateTimeOffset.UtcNow;
        Store.BindPreparedAdmission(reference, at);
        return Store.GetPreparedAdmission(goal.Id.Value, task.Id.Value, at);
    }

    public static string Snapshot(AgentOrchestratorKernel kernel) => JsonSerializer.Serialize(kernel.ExportSnapshot());
    public void Dispose() => SharedTestSupport.RemoveTempDirectory(Root);
}
