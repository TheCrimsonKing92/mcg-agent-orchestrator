using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class ConductorUnappliedExitWatchTests
{
    [Xunit.Fact(DisplayName = "UnappliedExitWatch_emits_one_record_on_the_third_consecutive_tick")]
    public void EmitsOneRecordOnTheThirdConsecutiveTick()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mcg-unapplied-exit-{Guid.NewGuid():N}");
        try
        {
            var (kernel, goal, task, process) = DispatchExitSweepEligibilityTests.SeedExitedRound(
                WorkTaskStatus.Failed,
                root);
            var watch = new ConductorUnappliedExitWatch();

            Xunit.Assert.Empty(watch.Observe(kernel));
            Xunit.Assert.Empty(watch.Observe(kernel));
            var third = watch.Observe(kernel);
            var fourth = watch.Observe(kernel);

            var record = Xunit.Assert.Single(third);
            Xunit.Assert.StartsWith("EXIT_UNAPPLIED ", record, StringComparison.Ordinal);
            Xunit.Assert.Contains($"goal={goal.Id.Value[..8]}", record, StringComparison.Ordinal);
            Xunit.Assert.Contains($"task={task.Id.Value[..8]}", record, StringComparison.Ordinal);
            Xunit.Assert.Contains($"pid={process.ProcessId}", record, StringComparison.Ordinal);
            Xunit.Assert.Contains(
                $"artifact={JsonSerializer.Serialize(process.ExitCodePath)}",
                record,
                StringComparison.Ordinal);
            Xunit.Assert.Empty(fourth);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Xunit.Fact(DisplayName = "UnappliedExitWatch_rearms_after_the_exit_is_applied")]
    public void RearmsAfterTheExitIsApplied()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mcg-unapplied-exit-rearm-{Guid.NewGuid():N}");
        try
        {
            var (kernel, goal, task, _) = DispatchExitSweepEligibilityTests.SeedExitedRound(
                WorkTaskStatus.Failed,
                root);
            var watch = new ConductorUnappliedExitWatch();
            watch.Observe(kernel);
            watch.Observe(kernel);
            Xunit.Assert.Single(watch.Observe(kernel));

            new BackgroundDispatchRunner(isStillRunning: _ => false).SweepExitedProcesses(kernel);

            Xunit.Assert.True(DispatchProcessCompletionState.HasAlreadyBeenApplied(
                kernel.GetTask(goal.Id, task.Id),
                kernel.GetTask(goal.Id, task.Id).LastProcess!));
            Xunit.Assert.Empty(watch.Observe(kernel));
            Xunit.Assert.Empty(watch.Observe(kernel));
            Xunit.Assert.Empty(watch.Observe(kernel));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Xunit.Fact(DisplayName = "UnappliedExitWatch_is_silent_while_the_round_is_still_running")]
    public void IsSilentWhileTheRoundIsStillRunning()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mcg-unapplied-exit-live-{Guid.NewGuid():N}");
        try
        {
            var (kernel, _, _, _) = DispatchExitSweepEligibilityTests.SeedExitedRound(
                WorkTaskStatus.Running,
                root);
            // The round is durably Running and has published no exit artifact yet.
            var watch = new ConductorUnappliedExitWatch(exitArtifactExists: _ => false);

            Xunit.Assert.Empty(watch.Observe(kernel));
            Xunit.Assert.Empty(watch.Observe(kernel));
            Xunit.Assert.Empty(watch.Observe(kernel));
            Xunit.Assert.Empty(watch.Observe(kernel));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Xunit.Fact(DisplayName = "UnappliedExitWatch_distinguishes_process_id_and_artifact_path_per_key")]
    public void DistinguishesProcessIdAndArtifactPathPerKey()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mcg-unapplied-exit-key-{Guid.NewGuid():N}");
        try
        {
            var (kernel, goal, task, first) = DispatchExitSweepEligibilityTests.SeedExitedRound(
                WorkTaskStatus.Failed,
                root);
            var watch = new ConductorUnappliedExitWatch();
            watch.Observe(kernel);
            watch.Observe(kernel);
            Xunit.Assert.Single(watch.Observe(kernel));

            // A successor round on the same task is a different key: the counter re-arms and must
            // reach the threshold again before its own distinct record is emitted.
            var successorExit = Path.Combine(root, "successor.exit.txt");
            File.WriteAllText(successorExit, "1");
            kernel.RecordTaskProcessRefreshed(
                goal.Id,
                task.Id,
                first with { ProcessId = 5150, ExitCodePath = successorExit },
                null);

            Xunit.Assert.Empty(watch.Observe(kernel));
            Xunit.Assert.Empty(watch.Observe(kernel));
            var record = Xunit.Assert.Single(watch.Observe(kernel));
            Xunit.Assert.Contains("pid=5150", record, StringComparison.Ordinal);
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
