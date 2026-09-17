using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

using static ConductorDriverTests;

[Xunit.Collection("IsolatedProcessSpawning")]
public sealed class ConductorDriverTestsFailedGoalExitReconciliation
{
    [Xunit.Fact]
    public void FailedGoalReconcilesExitedDeveloperBeforeRetryDecision()
    {
        var (kernel, goal) = SoftwareGoal();
        var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task, hasCommittedChanges: task.RequiredRole == AgentRole.Developer);
        }

        FailReviewerNeedsWork(kernel, goal, reviewer, "The candidate still has an open blocking finding.");
        kernel.RetryTask(goal.Id, developer.Id, "Route repair for the open finding.", invalidateDownstream: false);
        DispatchTask(kernel, goal, developer);
        var completedAt = DateTimeOffset.UtcNow;
        var process = CompletedProcess(completedAt);
        kernel.RecordTaskProcessStarted(goal.Id, developer.Id, process);
        var snapshot = kernel.ExportSnapshot();
        var goalSnapshot = snapshot.Goals.Single();
        var tasks = goalSnapshot.Tasks
            .Select(task => task.Id == developer.Id.Value
                ? task with { Status = WorkTaskStatus.Assigned }
                : task)
            .ToArray();
        kernel = AgentOrchestratorKernel.FromSnapshot(snapshot with
        {
            Goals = [goalSnapshot with { Tasks = tasks }]
        });
        goal = kernel.GetGoal(goal.Id);
        developer = goal.Tasks.Single(task => task.Id == developer.Id);

        var dispatchCount = 0;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            dispatchAndStart: _ =>
            {
                dispatchCount++;
                return DispatchStartOutcome.Started();
            },
            retryTask: (goalId, taskId, message) => kernel.RetryTask(goalId, taskId, message),
            reconcileExitedDispatch: (_, taskId) =>
            {
                Assert.Equal(developer.Id, taskId);
                kernel.RecordTaskProcessRefreshed(
                    goal.Id,
                    developer.Id,
                    process,
                    SuccessfulDeveloperVerification(completedAt));
                return true;
            });

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        // On HEAD before the fix, the first assertion fails because no reconciliation runs. If the
        // reconcile call is moved below the retry ladder, the first two pass and the final assertion
        // fails, proving that reconciliation must precede every new-dispatch decision.
        Assert.Contains(developer.VerificationHistory, verification =>
            verification.CompletedAt == completedAt && verification.ExitCode == 0);
        Assert.Equal(WorkTaskStatus.Completed, developer.Status);
        Assert.Equal(0, dispatchCount);
    }

    [Xunit.Fact]
    public void ReconciliationThatLeavesFailedLifecycleHoldsBeforeReevaluatingPolicy()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal(
            "Re-observe lifecycle authority after exited dispatch reconciliation",
            [
                new TaskSpec(TaskId.New(), "First exited task", AgentRole.Developer),
                new TaskSpec(TaskId.New(), "Second exited task", AgentRole.Developer)
            ]);
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        var first = goal.Tasks[0];
        var second = goal.Tasks[1];
        DispatchTask(kernel, goal, first);
        DispatchTask(kernel, goal, second);
        var firstProcess = CompletedProcess(DateTimeOffset.UtcNow);
        var secondProcess = CompletedProcess(DateTimeOffset.UtcNow.AddSeconds(1)) with { ProcessId = 12346 };
        kernel.RecordTaskProcessStarted(goal.Id, first.Id, firstProcess);
        kernel.RecordTaskProcessStarted(goal.Id, second.Id, secondProcess);

        var snapshot = kernel.ExportSnapshot();
        var goalSnapshot = snapshot.Goals.Single();
        var tasks = goalSnapshot.Tasks
            .Select(task => task.Id == first.Id.Value
                ? task with { Status = WorkTaskStatus.Failed }
                : task with { Status = WorkTaskStatus.Assigned })
            .ToArray();
        kernel = AgentOrchestratorKernel.FromSnapshot(snapshot with
        {
            Goals = [goalSnapshot with { Tasks = tasks }]
        });
        goal = kernel.GetGoal(goal.Id);
        first = goal.Tasks[0];
        var reconcileCount = 0;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            reconcileExitedDispatch: (_, taskId) =>
            {
                reconcileCount++;
                Assert.Equal(first.Id, taskId);
                kernel.RecordTaskProcessRefreshed(
                    goal.Id,
                    first.Id,
                    firstProcess,
                    SuccessfulDeveloperVerification(firstProcess.CompletedAt!.Value));
                return true;
            });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        var held = Assert.IsType<ConductorAdvanceOutcome.Held>(result.Outcome);
        Assert.Contains("stale-recovery-facts", held.Reason, StringComparison.Ordinal);
        Assert.Equal(1, reconcileCount);
        Assert.Equal(WorkTaskStatus.Completed, first.Status);
        Assert.Equal(WorkTaskStatus.Assigned, second.Status);
    }

    [Xunit.Fact]
    public void DispatchStartRefusesExitedUnappliedLatestProcess()
    {
        var (kernel, goal) = SimpleGoal();
        var task = goal.Tasks.Single();
        DispatchTask(kernel, goal, task);
        kernel.RecordTaskProcessStarted(goal.Id, task.Id, CompletedProcess(DateTimeOffset.UtcNow));
        var snapshot = kernel.ExportSnapshot();
        var goalSnapshot = snapshot.Goals.Single();
        var assignedWithExitedProcess = goalSnapshot.Tasks.Single() with { Status = WorkTaskStatus.Assigned };
        kernel = AgentOrchestratorKernel.FromSnapshot(snapshot with
        {
            Goals = [goalSnapshot with { Tasks = [assignedWithExitedProcess] }]
        });
        goal = kernel.GetGoal(goal.Id);
        task = goal.Tasks.Single();

        var dispatchCount = 0;
        var reconcileCount = 0;
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            dispatchAndStart: _ =>
            {
                dispatchCount++;
                return DispatchStartOutcome.Started();
            },
            recordTaskNote: (goalId, taskId, message) => kernel.RecordTaskNote(goalId, taskId, message),
            reconcileExitedDispatch: (_, _) =>
            {
                reconcileCount++;
                return false;
            });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(0, dispatchCount);
        Assert.Equal(1, reconcileCount);
        var held = Assert.IsType<ConductorAdvanceOutcome.Held>(result.Outcome);
        Assert.Contains("exited-unapplied-process-record", held.Reason, StringComparison.Ordinal);
        Assert.Contains(goal.Timeline, item =>
            item.TaskId == task.Id &&
            item.Kind == ProgressKind.TaskNote &&
            item.Message.Contains("exited-unapplied-process-record", StringComparison.Ordinal));
    }

    [Xunit.Fact]
    public void FailedGoalWithLiveWorkerKeepsExistingHold()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal(
            "Keep live-worker failure hold",
            [
                new TaskSpec(TaskId.New(), "Failed review", AgentRole.Reviewer),
                new TaskSpec(TaskId.New(), "Live repair", AgentRole.Developer)
            ]);
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        var reviewer = goal.Tasks[0];
        var developer = goal.Tasks[1];
        kernel.ReportTaskProgress(goal.Id, reviewer.Id, WorkTaskStatus.Failed, "Open blocking finding.");
        DispatchTask(kernel, goal, developer);
        kernel.RecordTaskProcessStarted(
            goal.Id,
            developer.Id,
            CompletedProcess(completedAt: null, exitCode: null));
        var driver = MakeDriver(getFacts: _ => GoalLifecycleFacts.None);

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        var held = Assert.IsType<ConductorAdvanceOutcome.Held>(result.Outcome);
        Assert.Equal(
            $"Failure handling deferred while task {developer.Id.Value[..8]} still has a live worker process.",
            held.Reason);
    }

    private static TaskProcessRecord CompletedProcess(
        DateTimeOffset? completedAt,
        int? exitCode = 0) =>
        new(
            12345,
            "test.exe",
            @"C:\tmp",
            @"C:\tmp\stdout",
            @"C:\tmp\stderr",
            @"C:\tmp\exit",
            StartedAt: DateTimeOffset.UtcNow - TimeSpan.FromMinutes(1),
            CompletedAt: completedAt,
            ExitCode: exitCode);

    private static TaskVerificationRecord SuccessfulDeveloperVerification(DateTimeOffset completedAt) =>
        new(
            "test.exe",
            @"C:\tmp",
            0,
            string.Join(
                Environment.NewLine,
                "WORKER_RESULT:",
                "files: src/Test.cs",
                "commands: focused verification",
                "tests: pass - focused verification passed",
                "commit: none",
                "blockers: none",
                "model_fit: fixture/model - adequate - regression fixture",
                "skills: none",
                "confidence: high",
                "END_WORKER_RESULT"),
            string.Empty,
            completedAt,
            HasCommittedChanges: true,
            WorkerResultPresent: true);
}
