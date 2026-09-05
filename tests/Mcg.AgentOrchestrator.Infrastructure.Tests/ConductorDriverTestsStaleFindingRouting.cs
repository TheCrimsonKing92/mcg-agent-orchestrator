using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using System.Text.Json;

using static ConductorDriverTests;

[Xunit.Collection("IsolatedProcessSpawning")]
public sealed class ConductorDriverTestsStaleFindingRouting
{
    // Before the currency fix, the first two fixtures retried the Developer because the
    // convergence brief resurrected the Tester's superseded findings from verification history.
    [Xunit.Fact]
    public void StaleTesterFindingAfterOperatorCloseDoesNotReopenDeveloper()
    {
        var (kernel, goal) = SoftwareGoal();
        var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
        var tester = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        var startedAt = new DateTimeOffset(2026, 9, 4, 21, 30, 0, TimeSpan.Zero);

        RecordCommittedDeveloperPass(kernel, goal, developer, startedAt);
        RecordTesterFindings(kernel, goal, tester, startedAt.AddMinutes(3));
        kernel.RetryTask(goal.Id, developer.Id, "Developer fixes resolved the Tester findings.");
        RecordCommittedDeveloperPass(kernel, goal, developer, startedAt.AddMinutes(12));
        kernel.ReportTaskProgress(goal.Id, tester.Id, WorkTaskStatus.Completed, "Operator closed the Tester after confirming the fixes.");
        kernel.RecordTaskVerification(
            goal.Id,
            tester.Id,
            ManualVerificationRecorder.Create(
                passed: true,
                "Operator manual pass confirmed the Tester findings are resolved.",
                "C:\\tmp",
                startedAt.AddMinutes(13)));
        RecordUnparseableReviewerNeedsWork(kernel, goal, reviewer, startedAt.AddMinutes(20));

        Assert.Equal(WorkTaskStatus.Completed, tester.Status);
        Assert.Equal(WorkTaskStatus.Failed, reviewer.Status);
        Assert.Null(reviewer.LastVerification!.MergedReviewFindings);
        Assert.Empty(AutoReviewRetryConvergenceBriefBuilder.ReadStructuredReviewFindingState(
            goal,
            AgentRole.Tester,
            reviewer.LastVerification.CompletedAt));

        TaskId? retriedTaskId = null;
        string? retryMessage = null;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
            {
                retriedTaskId = taskId;
                retryMessage = message;
                return kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind);
            });

        var timelineCountBeforeAdvance = goal.Timeline.Count;
        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(reviewer.Id, retriedTaskId);
        Assert.DoesNotContain(goal.Timeline.Skip(timelineCountBeforeAdvance), evt =>
            evt.TaskId == developer.Id && evt.Kind == ProgressKind.TaskRetried);
        Assert.DoesNotContain("owned-exit-200x-saturation-test-missing", retryMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("owned-exit-disposed-handle-probe-test-missing", retryMessage, StringComparison.Ordinal);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Executed);
    }

    [Xunit.Fact]
    public void StaleTesterFindingAfterInvalidationDispatchesReviewerNotDeveloper()
    {
        var (kernel, goal) = SoftwareGoal();
        var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
        var tester = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        var startedAt = new DateTimeOffset(2026, 9, 4, 22, 45, 0, TimeSpan.Zero);

        RecordCommittedDeveloperPass(kernel, goal, developer, startedAt);
        RecordTesterFindings(kernel, goal, tester, startedAt.AddMinutes(3));
        kernel.RetryTask(goal.Id, developer.Id, "Reviewer requested an upstream correction.");
        Assert.Null(tester.LastVerification);
        RecordCommittedDeveloperPass(kernel, goal, developer, startedAt.AddMinutes(28));
        RecordUnparseableReviewerNeedsWork(kernel, goal, reviewer, startedAt.AddMinutes(38));

        Assert.Equal(WorkTaskStatus.Failed, reviewer.Status);
        Assert.Null(reviewer.LastVerification!.MergedReviewFindings);
        Assert.Empty(AutoReviewRetryConvergenceBriefBuilder.ReadStructuredReviewFindingState(
            goal,
            AgentRole.Tester,
            reviewer.LastVerification.CompletedAt));

        TaskId? retriedTaskId = null;
        string? retryMessage = null;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
            {
                retriedTaskId = taskId;
                retryMessage = message;
                return kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind);
            });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(reviewer.Id, retriedTaskId);
        Assert.DoesNotContain(goal.Timeline, evt =>
            evt.TaskId == developer.Id && evt.Kind == ProgressKind.TaskRetried &&
            evt.Message.Contains("auto-review-retry", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("owned-exit-200x-saturation-test-missing", retryMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("owned-exit-disposed-handle-probe-test-missing", retryMessage, StringComparison.Ordinal);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Executed);
    }

    [Xunit.Fact]
    public void ManualPassSupersedesTesterFindingWithoutNewDeveloperCompletion()
    {
        var (kernel, goal) = SoftwareGoal();
        var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
        var tester = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
        var startedAt = new DateTimeOffset(2026, 9, 4, 21, 30, 0, TimeSpan.Zero);

        RecordCommittedDeveloperPass(kernel, goal, developer, startedAt);
        RecordTesterFindings(kernel, goal, tester, startedAt.AddMinutes(3));
        kernel.ReportTaskProgress(goal.Id, tester.Id, WorkTaskStatus.Completed, "Operator closed the Tester.");
        kernel.RecordTaskVerification(
            goal.Id,
            tester.Id,
            ManualVerificationRecorder.Create(
                passed: true,
                "Operator confirmed the Tester finding is resolved.",
                "C:\\tmp",
                startedAt.AddMinutes(4)));

        Assert.Equal(WorkTaskStatus.Completed, tester.Status);
        Assert.Empty(AutoReviewRetryConvergenceBriefBuilder.ReadStructuredReviewFindingState(goal, tester));
    }

    [Xunit.Fact]
    public void ApparatusFailedTesterRerunDoesNotResolveItsCurrentFinding()
    {
        var (kernel, goal) = SoftwareGoal();
        var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
        var tester = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
        var startedAt = new DateTimeOffset(2026, 9, 4, 21, 30, 0, TimeSpan.Zero);

        RecordCommittedDeveloperPass(kernel, goal, developer, startedAt);
        RecordTesterFindings(kernel, goal, tester, startedAt.AddMinutes(3));
        kernel.RetryTask(goal.Id, tester.Id, "Retry Tester after an apparatus failure.");
        RecordUnparseableNeedsWork(kernel, goal, tester, startedAt.AddMinutes(4), "test");

        Assert.Equal(WorkTaskStatus.Failed, tester.Status);
        Assert.Null(tester.LastVerification!.MergedReviewFindings);
        var findings = AutoReviewRetryConvergenceBriefBuilder.ReadStructuredReviewFindingState(
            goal,
            AgentRole.Tester,
            tester.LastVerification.CompletedAt);
        Assert.Equal(2, findings.Count);
        Assert.Contains(findings, finding =>
            finding.StableId == "owned-exit-200x-saturation-test-missing");
    }

    private static void RecordCommittedDeveloperPass(
        AgentOrchestratorKernel kernel,
        Goal goal,
        TaskSpec developer,
        DateTimeOffset completedAt)
    {
        DispatchTask(kernel, goal, developer, baseCommit: "base-commit");
        kernel.RecordDispatchResultCommit(goal.Id, developer.Id, $"result-{completedAt.ToUnixTimeSeconds()}");
        kernel.RecordTaskVerification(goal.Id, developer.Id, new TaskVerificationRecord(
            "develop",
            "C:\\tmp",
            0,
            "ok",
            string.Empty,
            completedAt,
            WorkerResultPresent: true,
            HasCommittedChanges: true));
    }

    private static void RecordTesterFindings(
        AgentOrchestratorKernel kernel,
        Goal goal,
        TaskSpec tester,
        DateTimeOffset completedAt)
    {
        var findings = new[]
        {
            new ReviewFinding(
                "owned-exit-200x-saturation-test-missing",
                ReviewFindingState.Open,
                new ReviewFindingLocation("tests/OwnedProcessExitObservationTests.cs", "Saturation"),
                "The saturation regression test is missing.",
                FindingSeverity.Blocking,
                FindingCategory.TestCoverage),
            new ReviewFinding(
                "owned-exit-disposed-handle-probe-test-missing",
                ReviewFindingState.Open,
                new ReviewFindingLocation("tests/OwnedProcessExitObservationTests.cs", "DisposedHandle"),
                "The disposed-handle probe regression test is missing.",
                FindingSeverity.Blocking,
                FindingCategory.TestCoverage)
        };
        var stdout = string.Join(
            Environment.NewLine,
            "WORKER_RESULT:",
            "files: none",
            "commands: inspect focused behavior",
            "tests: fail - two regression cases are missing",
            "commit: none",
            "blockers: missing regression coverage",
            $"findings: {JsonSerializer.Serialize(findings)}",
            "touched_anchors: []",
            "verdict: needs-work",
            "model_fit: fixture/model - adequate - source verification",
            "skills: none",
            "confidence: high",
            "END_WORKER_RESULT");

        DispatchTask(kernel, goal, tester, "test");
        kernel.RecordDispatchExecutionResult(goal.Id, tester.Id, new TaskVerificationRecord(
            "test",
            "C:\\tmp",
            1,
            stdout,
            string.Empty,
            completedAt,
            StandardOutputPath: "C:\\tmp\\tester.out.log",
            WorkerResultPresent: true));
        Assert.Equal(2, tester.LastVerification!.MergedReviewFindings?.Count);
    }

    private static void RecordUnparseableReviewerNeedsWork(
        AgentOrchestratorKernel kernel,
        Goal goal,
        TaskSpec reviewer,
        DateTimeOffset completedAt) =>
        RecordUnparseableNeedsWork(kernel, goal, reviewer, completedAt, "review");

    private static void RecordUnparseableNeedsWork(
        AgentOrchestratorKernel kernel,
        Goal goal,
        TaskSpec task,
        DateTimeOffset completedAt,
        string command)
    {
        DispatchTask(kernel, goal, task, command);
        kernel.RecordDispatchExecutionResult(goal.Id, task.Id, new TaskVerificationRecord(
            command,
            "C:\\tmp",
            1,
            string.Join(
                Environment.NewLine,
                "blockers: src/Target.cs:1 - Reviewer could not validate the candidate.",
                "verdict: needs-work"),
            string.Empty,
            completedAt,
            StandardOutputPath: "C:\\tmp\\reviewer.out.log",
            WorkerResultPresent: false));
    }
}
