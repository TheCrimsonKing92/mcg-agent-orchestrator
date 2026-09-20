using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

using static ConductorDriverTests;

public sealed class DispatchExitSweepEligibilityTests
{
    [Xunit.Fact]
    public void FailedTaskWithUnappliedExitedProcessIsEligible()
    {
        var (kernel, goal, task, process) = SeedExitedRound(WorkTaskStatus.Failed);

        Xunit.Assert.True(DispatchExitSweepEligibility.IsEligibleForExitSweep(task, process));
        Xunit.Assert.False(DispatchProcessCompletionState.HasAlreadyBeenApplied(task, process));
        Xunit.Assert.Same(kernel.GetTask(goal.Id, task.Id), task);
    }

    [Xunit.Fact]
    public void CancelledTaskStaysSkipped()
    {
        var (_, _, task, process) = SeedExitedRound(WorkTaskStatus.Cancelled);

        Xunit.Assert.False(DispatchExitSweepEligibility.IsEligibleForExitSweep(task, process));
    }

    [Xunit.Fact]
    public void WaitingForHumanTaskStaysSkipped()
    {
        var (kernel, goal, task, process) = SeedExitedRound(WorkTaskStatus.Running);
        kernel.RequestHumanInput(goal.Id, task.Id, "Which candidate should this round target?");

        Xunit.Assert.Equal(WorkTaskStatus.WaitingForHuman, task.Status);
        Xunit.Assert.False(DispatchExitSweepEligibility.IsEligibleForExitSweep(task, process));
    }

    [Xunit.Fact]
    public void CancelledProcessAndMissingProcessStaySkipped()
    {
        var (_, _, task, process) = SeedExitedRound(WorkTaskStatus.Failed);

        Xunit.Assert.False(DispatchExitSweepEligibility.IsEligibleForExitSweep(task, process with { WasCancelled = true }));
        Xunit.Assert.False(DispatchExitSweepEligibility.IsEligibleForExitSweep(task, null));
    }

    [Xunit.Fact]
    public void FenceStripsRequeueForTerminalStatusButKeepsTheDisposition()
    {
        var (_, _, task, process) = SeedExitedRound(WorkTaskStatus.Failed);
        var outcome = new DispatchRefreshOutcome(
            process,
            null,
            AutoRequeueDisposition: new DispatchAutoRequeueDisposition(
                "DISPATCH_PROVIDER_INTERRUPTION",
                "interrupted after conductor loop stop"));

        var fenced = DispatchExitSweepEligibility.FenceAutoRequeue(task, outcome);

        Xunit.Assert.True(DispatchExitSweepEligibility.SuppressesAutoRequeue(task));
        Xunit.Assert.NotNull(fenced.AutoRequeueDisposition);
        Xunit.Assert.False(fenced.AutoRequeueDisposition!.ShouldRequeue);
        Xunit.Assert.Equal("DISPATCH_PROVIDER_INTERRUPTION", fenced.AutoRequeueDisposition.EventName);
        Xunit.Assert.Equal("interrupted after conductor loop stop", fenced.AutoRequeueDisposition.Message);
    }

    [Xunit.Fact]
    public void FenceLeavesRunningTaskRequeueUntouched()
    {
        var (kernel, goal, task, process) = SeedExitedRound(WorkTaskStatus.Failed);
        kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Running, "round resumed");
        var disposition = new DispatchAutoRequeueDisposition("DISPATCH_PROVIDER_INTERRUPTION", "interrupted");
        var outcome = new DispatchRefreshOutcome(process, null, AutoRequeueDisposition: disposition);

        var fenced = DispatchExitSweepEligibility.FenceAutoRequeue(task, outcome);

        Xunit.Assert.False(DispatchExitSweepEligibility.SuppressesAutoRequeue(task));
        Xunit.Assert.Same(outcome, fenced);
        Xunit.Assert.True(fenced.AutoRequeueDisposition!.ShouldRequeue);
    }

    internal static (AgentOrchestratorKernel Kernel, Goal Goal, TaskSpec Task, TaskProcessRecord Process) SeedExitedRound(
        WorkTaskStatus status,
        string? root = null)
    {
        root ??= Path.Combine(Path.GetTempPath(), $"mcg-exit-sweep-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var (kernel, goal) = SimpleGoal("Apply an exited round whose task is already terminal");
        var task = goal.Tasks.First();
        var startedAt = DateTimeOffset.Parse("2026-09-05T15:05:00Z");
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("developer", "worker.exe", root, startedAt));
        var stdout = Path.Combine(root, "worker.out.log");
        var stderr = Path.Combine(root, "worker.err.log");
        var exit = Path.Combine(root, "worker.exit.txt");
        File.WriteAllText(stdout, "no WORKER_RESULT block was emitted");
        File.WriteAllText(stderr, string.Empty);
        File.WriteAllText(exit, "1");
        var process = new TaskProcessRecord(
            4242,
            "worker.exe",
            root,
            stdout,
            stderr,
            exit,
            startedAt,
            null,
            null,
            OwnedProcessIds: [4242]);
        kernel.RecordTaskProcessStarted(goal.Id, task.Id, process);
        kernel.ReportTaskProgress(goal.Id, task.Id, status, $"round terminalized as {status} before its exit was applied");
        return (kernel, goal, kernel.GetTask(goal.Id, task.Id), process);
    }
}
