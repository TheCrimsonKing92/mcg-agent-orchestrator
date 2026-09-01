using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class ConductWatchSweepScopingTests
{
    [Xunit.Fact(DisplayName = "SweepExitedProcesses_goal_filter_reconciles_watched_goal")]
    public void SweepExitedProcessesGoalFilterReconcilesWatchedGoal()
    {
        var kernel = new AgentOrchestratorKernel();
        var watched = CreateGoalWithExitedProcess(kernel, "watched");
        var runner = new BackgroundDispatchRunner(isStillRunning: _ => false);

        var swept = runner.SweepExitedProcesses(kernel, watched.Goal.Id);

        Assert.Equal(1, swept);
        Assert.False(kernel.GetTask(watched.Goal.Id, watched.Task.Id).LastProcess!.IsRunning);
        Assert.NotNull(kernel.GetTask(watched.Goal.Id, watched.Task.Id).LastVerification);
    }

    [Xunit.Fact(DisplayName = "SweepExitedProcesses_goal_filter_does_not_reconcile_unwatched_goal")]
    public void SweepExitedProcessesGoalFilterDoesNotReconcileUnwatchedGoal()
    {
        var kernel = new AgentOrchestratorKernel();
        var watched = CreateGoalWithExitedProcess(kernel, "watched");
        var unwatched = CreateGoalWithExitedProcess(kernel, "unwatched");
        var runner = new BackgroundDispatchRunner(isStillRunning: _ => false);

        var swept = runner.SweepExitedProcesses(kernel, watched.Goal.Id);

        Assert.Equal(1, swept);
        Assert.False(kernel.GetTask(watched.Goal.Id, watched.Task.Id).LastProcess!.IsRunning);
        Assert.True(kernel.GetTask(unwatched.Goal.Id, unwatched.Task.Id).LastProcess!.IsRunning);
        Assert.Null(kernel.GetTask(unwatched.Goal.Id, unwatched.Task.Id).LastVerification);
    }

    [Xunit.Fact(DisplayName = "SweepExitedProcesses_without_goal_filter_preserves_loop_wide_sweep")]
    public void SweepExitedProcessesWithoutGoalFilterPreservesLoopWideSweep()
    {
        var kernel = new AgentOrchestratorKernel();
        var first = CreateGoalWithExitedProcess(kernel, "first");
        var second = CreateGoalWithExitedProcess(kernel, "second");
        var runner = new BackgroundDispatchRunner(isStillRunning: _ => false);

        var swept = runner.SweepExitedProcesses(kernel);

        Assert.Equal(2, swept);
        Assert.False(kernel.GetTask(first.Goal.Id, first.Task.Id).LastProcess!.IsRunning);
        Assert.False(kernel.GetTask(second.Goal.Id, second.Task.Id).LastProcess!.IsRunning);
    }

    [Xunit.Fact]
    public void SweepExitedProcessesMultipleDiagnosticsReuseOneProcessSnapshot()
    {
        var kernel = new AgentOrchestratorKernel();
        var first = CreateGoalWithExitedProcess(kernel, "first", 900001);
        var second = CreateGoalWithExitedProcess(kernel, "second", 900002);
        var snapshotCreations = 0;
        var runner = new BackgroundDispatchRunner(
            isStillRunning: _ => false,
            processCommandLineSnapshotFactory: () =>
            {
                snapshotCreations++;
                return new ProcessCommandLineSnapshot(
                    new Dictionary<int, string>
                    {
                        [900001] = first.Task.LastProcess!.Command,
                        [900002] = second.Task.LastProcess!.Command
                    });
            });

        var swept = runner.SweepExitedProcesses(kernel);

        Assert.Equal(2, swept);
        Assert.Equal(1, snapshotCreations);
    }

    private static (Goal Goal, TaskSpec Task) CreateGoalWithExitedProcess(
        AgentOrchestratorKernel kernel,
        string objective,
        int processId = 999999)
    {
        var root = CreateTempDirectory();
        var task = new TaskSpec(TaskId.New(), $"{objective} task", AgentRole.Developer);
        var goal = kernel.CreateGoal(objective, [task]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        var workTask = goal.Tasks.Single();
        var stdoutPath = Path.Combine(root, "worker.out.log");
        var stderrPath = Path.Combine(root, "worker.err.log");
        var exitPath = Path.Combine(root, "worker.exit.txt");
        File.WriteAllText(stdoutPath, "Worker completed successfully.");
        File.WriteAllText(stderrPath, "");
        File.WriteAllText(exitPath, "0");

        var now = DateTimeOffset.UtcNow;
        kernel.RecordTaskDispatch(goal.Id, workTask.Id, new TaskDispatchRecord("local", "local-cmd", root, now));
        kernel.RecordTaskProcessStarted(goal.Id, workTask.Id,
            new TaskProcessRecord(processId, "local-cmd", root, stdoutPath, stderrPath, exitPath, now, null, null));
        return (goal, workTask);
    }

    private static string CreateTempDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mcg-watch-sweep-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }
}
