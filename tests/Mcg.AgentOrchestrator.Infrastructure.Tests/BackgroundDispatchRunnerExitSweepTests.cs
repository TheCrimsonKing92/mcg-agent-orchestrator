using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class BackgroundDispatchRunnerExitSweepTests
{
    [Xunit.Fact(DisplayName = "ExitSweep_applies_exited_round_for_Failed_task_without_preparing_a_dispatch")]
    public void ExitSweepAppliesExitedRoundForFailedTaskWithoutPreparingADispatch()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mcg-exit-sweep-failed-{Guid.NewGuid():N}");
        try
        {
            var (kernel, goal, task, process) = DispatchExitSweepEligibilityTests.SeedExitedRound(
                WorkTaskStatus.Failed,
                root);
            var dispatchBefore = task.LastDispatch;
            var verificationsBefore = task.VerificationHistory.Count;
            var timelineBefore = goal.Timeline.Count;

            var reconciled = new BackgroundDispatchRunner(isStillRunning: _ => false)
                .SweepExitedProcesses(kernel);

            var applied = kernel.GetTask(goal.Id, task.Id);
            Xunit.Assert.Equal(1, reconciled);
            // The exit is applied: the process record carries the artifact's exit code and the round is
            // journalled as a verification, so a later sweep sees it as already applied.
            Xunit.Assert.Equal(1, applied.LastProcess!.ExitCode);
            Xunit.Assert.NotNull(applied.LastProcess.CompletedAt);
            Xunit.Assert.Equal(verificationsBefore + 1, applied.VerificationHistory.Count);
            Xunit.Assert.True(DispatchProcessCompletionState.HasAlreadyBeenApplied(applied, applied.LastProcess));
            Xunit.Assert.True(goal.Timeline.Count > timelineBefore);

            // No new dispatch is prepared: the task stays terminal and its dispatch record is untouched,
            // which a requeue would have cleared while moving the task back to Assigned/Pending.
            Xunit.Assert.Equal(WorkTaskStatus.Failed, applied.Status);
            Xunit.Assert.Equal(dispatchBefore!.DispatchedAt, applied.LastDispatch!.DispatchedAt);
            Xunit.Assert.Equal(dispatchBefore.Command, applied.LastDispatch.Command);
            Xunit.Assert.Equal(process.ProcessId, applied.LastProcess.ProcessId);

            // Idempotent: a second sweep reconciles nothing and changes nothing further.
            var second = new BackgroundDispatchRunner(isStillRunning: _ => false).SweepExitedProcesses(kernel);
            Xunit.Assert.Equal(0, second);
            Xunit.Assert.Equal(verificationsBefore + 1, kernel.GetTask(goal.Id, task.Id).VerificationHistory.Count);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Xunit.Fact(DisplayName = "ExitSweep_leaves_Cancelled_task_unreconciled")]
    public void ExitSweepLeavesCancelledTaskUnreconciled()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mcg-exit-sweep-cancelled-{Guid.NewGuid():N}");
        try
        {
            var (kernel, goal, task, _) = DispatchExitSweepEligibilityTests.SeedExitedRound(
                WorkTaskStatus.Cancelled,
                root);
            var verificationsBefore = task.VerificationHistory.Count;

            var reconciled = new BackgroundDispatchRunner(isStillRunning: _ => false)
                .SweepExitedProcesses(kernel);

            var observed = kernel.GetTask(goal.Id, task.Id);
            Xunit.Assert.Equal(0, reconciled);
            Xunit.Assert.Equal(WorkTaskStatus.Cancelled, observed.Status);
            Xunit.Assert.Equal(verificationsBefore, observed.VerificationHistory.Count);
            Xunit.Assert.Null(observed.LastProcess!.ExitCode);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
