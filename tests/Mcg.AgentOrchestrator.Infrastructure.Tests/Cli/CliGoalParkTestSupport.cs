using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public abstract class CliGoalParkTestSupport : CliGoalUnparkTestSupport
{
    private protected static async Task<ParkSeed> CreateActiveSeed(string root, int liveDispatchCount = 0)
    {
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        StateDbMigrations.EnsureUpToDate(workspace.SqliteStatePath);
        var repository = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath);
        var kernel = new AgentOrchestratorKernel();
        var tasks = Enumerable.Range(1, Math.Max(1, liveDispatchCount))
            .Select(number => new TaskSpec(TaskId.New(), $"Task {number}", AgentRole.Developer))
            .ToArray();
        var goal = kernel.CreateGoal("Park target", tasks);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        for (var index = 0; index < liveDispatchCount; index++)
        {
            var task = tasks[index];
            var pid = 900001 + index;
            kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord(
                "codex-cli", "codex exec prompt.md", root, DateTimeOffset.UtcNow));
            kernel.RecordTaskProcessStarted(goal.Id, task.Id, new TaskProcessRecord(
                pid, "codex exec prompt.md", root,
                Path.Combine(root, $"{task.Id.Value}.out"),
                Path.Combine(root, $"{task.Id.Value}.err"),
                Path.Combine(root, $"{task.Id.Value}.exit"),
                DateTimeOffset.UtcNow, null, null));
        }

        await repository.SaveAsync(kernel);
        return new ParkSeed(workspace, repository, goal.Id, tasks.Select(task => task.Id).ToArray());
    }

    private protected sealed record ParkSeed(
        OrchestratorWorkspace Workspace,
        SqliteOrchestratorStateRepository Repository,
        GoalId GoalId,
        IReadOnlyList<TaskId> TaskIds);
}
