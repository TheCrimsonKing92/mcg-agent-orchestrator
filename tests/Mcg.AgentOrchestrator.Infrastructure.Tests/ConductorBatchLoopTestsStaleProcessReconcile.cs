using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

using static ConductorDriverTests;

public sealed class ConductorBatchLoopTestsStaleProcessReconcile : ConductorBatchLoopTests
{
    public ConductorBatchLoopTestsStaleProcessReconcile(ITestOutputHelper output) : base(output)
    {
    }

    [Xunit.Fact]
    public void NativeExitIsAppliedBeforeDispatchPreparation()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mcg-stale-loop-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var now = DateTimeOffset.Parse("2026-09-05T15:48:00Z");
            var (kernel, goal) = ConductorDriverTests.SimpleGoal("Reconcile before dispatch preparation");
            var task = goal.Tasks.Single();
            var stdout = Path.Combine(root, "out.log");
            var stderr = Path.Combine(root, "err.log");
            var exit = Path.Combine(root, "exit.txt");
            File.WriteAllText(stdout, "done");
            File.WriteAllText(stderr, string.Empty);
            DispatchExitArtifacts.Write(exit, DispatchExitArtifacts.Native(0, "reviewer exited", now));
            kernel.RecordTaskDispatch(
                goal.Id,
                task.Id,
                new TaskDispatchRecord("reviewer", "review", root, now));
            kernel.RecordTaskProcessStarted(
                goal.Id,
                task.Id,
                new TaskProcessRecord(28516, "review", root, stdout, stderr, exit, now, null, null));
            var preparationObserved = false;
            var driver = ConductorDriverTests.MakeDriver(
                getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                startRecordedDispatches: _ =>
                {
                    preparationObserved = true;
                    Xunit.Assert.True(kernel.GetTask(goal.Id, task.Id).LastProcess is { IsRunning: false });
                    return DispatchStartOutcome.Started();
                });

            var summary = new ConductorBatchLoop(utcNow: () => now).Run(
                kernel,
                driver,
                ConductorAutonomyPolicy.Conservative,
                NoStopPath(),
                maxIterations: 1);

            Xunit.Assert.Equal(1, summary.Ticks);
            Xunit.Assert.True(preparationObserved);
            var process = Xunit.Assert.IsType<TaskProcessRecord>(task.LastProcess);
            Xunit.Assert.False(process.IsRunning);
            Xunit.Assert.False(process.WasCancelled);
            Xunit.Assert.DoesNotContain(goal.Timeline, evt => evt.Kind == ProgressKind.TaskCancelled);
            Xunit.Assert.DoesNotContain(goal.Timeline, evt =>
                evt.Message.Contains("Task already has a running process", StringComparison.Ordinal) ||
                evt.Message.Contains("interrupted after conductor loop stop", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
