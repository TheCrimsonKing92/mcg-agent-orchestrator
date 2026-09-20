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
            var kernel = new AgentOrchestratorKernel();
            var task = new TaskSpec(TaskId.New(), "Review the candidate", AgentRole.Reviewer);
            var nextTask = new TaskSpec(TaskId.New(), "Continue after review", AgentRole.Developer);
            var goal = kernel.CreateGoal("Reconcile before dispatch preparation", [task, nextTask]);
            kernel.ActivateGoal(goal.Id, DefaultAgents());
            var stdout = Path.Combine(root, "out.log");
            var stderr = Path.Combine(root, "err.log");
            var exit = Path.Combine(root, "exit.txt");
            File.WriteAllText(stdout, string.Join(Environment.NewLine,
            [
                "WORKER_RESULT:",
                "files: none",
                "commands: review",
                "tests: pass - inspected candidate",
                "commit: none",
                "blockers: none",
                "model_fit: test/reviewer - adequate - review - fixture",
                "skills: none",
                "confidence: high",
                "findings: []",
                "touched_anchors: []",
                "verdict: pass",
                "END_WORKER_RESULT"
            ]));
            File.WriteAllText(stderr, string.Empty);
            DispatchExitArtifacts.Write(exit, DispatchExitArtifacts.Native(0, "reviewer exited", now));
            kernel.RecordTaskDispatch(
                goal.Id,
                task.Id,
                new TaskDispatchRecord("reviewer", "review", root, now));
            kernel.RecordTaskProcessStarted(
                goal.Id,
                task.Id,
                new TaskProcessRecord(int.MaxValue, "review", root, stdout, stderr, exit, now, null, null));
            var snapshot = kernel.ExportGoalSnapshot(goal.Id);
            kernel.ReplaceGoalWithSnapshot(snapshot with
            {
                Tasks = snapshot.Tasks
                    .Select(candidate => candidate.Id == task.Id.Value
                        ? candidate with { Status = WorkTaskStatus.Assigned }
                        : candidate)
                    .ToArray()
            });
            goal = kernel.GetGoal(goal.Id);
            task = kernel.GetTask(goal.Id, task.Id);
            var preparationObserved = false;
            var driver = ConductorDriverTests.MakeDriver(
                getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                reconcileExitedDispatch: (_, _) => throw new Xunit.Sdk.XunitException(
                    "The batch-loop reconciler must apply the completed result before dispatch preparation."),
                dispatchAndStart: _ =>
                {
                    preparationObserved = true;
                    var completedTask = kernel.GetTask(goal.Id, task.Id);
                    Xunit.Assert.True(completedTask.LastProcess is { IsRunning: false });
                    Xunit.Assert.NotNull(completedTask.LastVerification);
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
