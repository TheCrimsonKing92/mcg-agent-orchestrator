using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using static ConductorDriverTests;

[Xunit.Collection("IsolatedProcessSpawning")]
public sealed class ConductorDriverTestsMalformedFindingRecovery
{
    [Xunit.Theory]
    [Xunit.InlineData("pass", false)]
    [Xunit.InlineData("needs-work", false)]
    [Xunit.InlineData("needs-work", true)]
    public void FormatFailureGetsOneRepairAcrossRestoredState(string verdict, bool blankFields)
    {
        var (kernel, goal) = SoftwareGoal();
        foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
            PassVerification(kernel, goal, task, hasCommittedChanges: task.RequiredRole == AgentRole.Developer);
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        RecordFinding(kernel, goal, reviewer, verdict, malformed: true, blankFields: blankFields);
        Assert.Equal(WorkTaskStatus.Failed, reviewer.Status);
        Assert.Equal(ReviewFindingConvergence.FindingFormatViolationCode,
            reviewer.LastVerification!.ReviewFindingContractViolation!.Code);
        if (blankFields)
        {
            Assert.True(WorkerResultBlockers.TryFindHardFailureBlocker(reviewer.LastVerification, out var blocker));
            Assert.Contains("The guard is broken", blocker);
        }

        var retried = new List<TaskId>();
        string? brief = null;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
            {
                retried.Add(taskId);
                brief = message;
                Assert.Equal(RetryRoundKind.Mechanical, roundKind);
                return kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind);
            });
        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);
        Assert.Equal(reviewer.Id, Assert.Single(retried));
        Assert.Contains("attempt 1/1", brief);
        Assert.Contains("finding-format contract", brief);
        Assert.Contains(blankFields ? "one-line findings and touched_anchors JSON arrays"
            : "mutation requires negative_control 'revert-src'", brief);
        Assert.Contains("SAME substantive conclusion and verdict", brief);
        Assert.Contains("Keep verdict and blockers unchanged", brief);
        Assert.Equal(WorkTaskStatus.Completed, goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer).Status);

        // A new driver/kernel must count durable retries rather than grant a new repair budget.
        kernel = AgentOrchestratorKernel.FromSnapshot(kernel.ExportSnapshot());
        goal = kernel.Goals.Single();
        reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        RecordFinding(kernel, goal, reviewer, verdict, malformed: true, blankFields: blankFields);
        string? escalation = null;
        var resumed = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            retryTaskWithRoundKind: (_, taskId, _, _) =>
            {
                retried.Add(taskId);
                return reviewer;
            },
            writeEscalation: (_, _, message) => escalation = message);
        var result = resumed.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);
        Assert.IsType<ConductorAdvanceOutcome.Escalated>(result.Outcome);
        Assert.Contains("contract-repair limit (1)", escalation);
        Assert.Contains(ReviewFindingConvergence.FindingFormatViolationCode, escalation);
        resumed.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);
        Assert.Single(retried);
        Assert.Equal(WorkTaskStatus.Failed, reviewer.Status);
        if (blankFields)
        {
            Assert.True(WorkerResultBlockers.TryFindHardFailureBlocker(reviewer.LastVerification, out var blocker));
            Assert.Contains("The guard is broken", blocker);
        }
        Assert.Equal(WorkTaskStatus.Completed, goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer).Status);
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void CorrectedBlockingFindingReturnsToSubstantiveDeveloperRouting(bool blankFields)
    {
        var (kernel, goal) = SoftwareGoal();
        foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
            PassVerification(kernel, goal, task, hasCommittedChanges: task.RequiredRole == AgentRole.Developer);
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        RecordFinding(kernel, goal, reviewer, "needs-work", malformed: true, blankFields: blankFields);
        var retried = new List<TaskId>();
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
            {
                retried.Add(taskId);
                return kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind);
            });
        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);
        RecordFinding(kernel, goal, reviewer, "needs-work", malformed: false);
        Assert.Null(reviewer.LastVerification!.ReviewFindingContractViolation);
        Assert.Equal("format-blocker", Assert.Single(reviewer.LastVerification.MergedReviewFindings!).StableId);
        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);
        Assert.Equal(new[] { reviewer.Id, goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer).Id }, retried);
    }

    private static void RecordFinding(AgentOrchestratorKernel kernel, Goal goal, TaskSpec reviewer,
        string verdict, bool malformed, bool blankFields = false)
    {
        DispatchTask(kernel, goal, reviewer);
        var finding = new ReviewFinding("format-blocker", ReviewFindingState.Open,
            new("src/Target.cs", "Run"), "The guard is broken", FindingSeverity.Blocking, FindingCategory.Correctness,
            malformed ? new FindingEvidenceRequest([], Mutation: new("src/Target.cs", "true", "false")) : null);
        var output = string.Join("\n", "WORKER_RESULT:", "files: none", "commands: none", "tests: pass",
            "commit: none", blankFields ? "blockers: src/Target.cs:Run - The guard is broken" : "blockers: none",
            $"verdict: {verdict}",
            "findings: " + (blankFields ? "" : JsonSerializer.Serialize(new[] { finding })),
            "touched_anchors: " + (blankFields ? "" : "[]"), "END_WORKER_RESULT");
        kernel.RecordDispatchExecutionResult(goal.Id, reviewer.Id,
            new("test.exe", "C:\\tmp", 0, output, "", DateTimeOffset.UtcNow, WorkerResultPresent: true));
    }
}
