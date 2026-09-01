using Mcg.AgentOrchestrator.App.Dashboard.Api;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class DashboardNextActionsProcessSnapshotTests
{
    [Xunit.Fact]
    public void ToNextActionsDto_MultipleTasksAndItems_ReusesProcessSnapshot()
    {
        var kernel = new AgentOrchestratorKernel();
        var tasks = new[]
        {
            new TaskSpec(TaskId.New(), "First running task", AgentRole.Developer),
            new TaskSpec(TaskId.New(), "Second running task", AgentRole.Tester)
        };
        var goal = kernel.CreateGoal("Map multiple running actions", tasks);
        var agents = AgentCatalog.Default().Agents;
        kernel.ActivateGoal(goal.Id, agents);
        RecordRunningProcess(kernel, goal, tasks[0], 4201);
        RecordRunningProcess(kernel, goal, tasks[1], 4202);
        var actions = kernel.BuildNextActions(goal.Id);
        var calls = 0;

        var dto = DashboardResponseMapper.ToNextActionsDto(
            goal,
            actions,
            WorkerProfileCatalog.Default(),
            agents,
            processSnapshotFactory: () =>
            {
                calls++;
                return ProcessCommandLineSnapshot.Empty;
            });

        Xunit.Assert.Equal(2, actions.Items.Count(item => item.Kind == NextActionKind.RefreshRunningProcess));
        Xunit.Assert.Equal(actions.Items.Count, dto.Items.Count);
        Xunit.Assert.Equal(1, calls);
    }

    private static void RecordRunningProcess(
        AgentOrchestratorKernel kernel,
        Goal goal,
        TaskSpec task,
        int processId)
    {
        const string root = @"C:\fixture";
        kernel.RecordTaskDispatch(
            goal.Id,
            task.Id,
            new TaskDispatchRecord("fixture", "worker", root, DateTimeOffset.UtcNow));
        kernel.RecordTaskProcessStarted(
            goal.Id,
            task.Id,
            new TaskProcessRecord(
                processId,
                "worker",
                root,
                $@"C:\fixture\{processId}.out.log",
                $@"C:\fixture\{processId}.err.log",
                $@"C:\fixture\{processId}.exit.txt",
                DateTimeOffset.UtcNow,
                null,
                null));
    }
}
