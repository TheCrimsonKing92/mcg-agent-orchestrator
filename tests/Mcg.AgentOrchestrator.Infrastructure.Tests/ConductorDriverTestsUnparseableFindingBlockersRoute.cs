using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

using static ConductorDriverTests;

[Xunit.Collection("IsolatedProcessSpawning")]
public sealed class ConductorDriverTestsUnparseableFindingBlockersRoute
{
    [Xunit.Fact]
    public void BlockersRouteRequiresReviewerAndMissingStructuredFindings()
    {
        var reviewer = new TaskSpec(TaskId.New(), "Review", AgentRole.Reviewer);
        var tester = new TaskSpec(TaskId.New(), "Test", AgentRole.Tester);

        Xunit.Assert.False(UnparseableFindingBlockersRoute.PrefersBlockersTextRoute(reviewer, string.Empty));
        Xunit.Assert.False(UnparseableFindingBlockersRoute.PrefersBlockersTextRoute(tester, "blocking defect"));
    }

    [Xunit.Fact]
    public void BlockersRouteRejectsReviewerWithParsedStructuredFindings()
    {
        var (kernel, goal) = SoftwareGoal();
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        DispatchTask(kernel, goal, reviewer, "review");
        kernel.RecordDispatchExecutionResult(goal.Id, reviewer.Id, new TaskVerificationRecord(
            "review",
            "C:\\tmp",
            1,
            "WORKER_RESULT:\nfindings: []\ntouched_anchors: []\nblockers: blocking defect\nverdict: needs-work\nEND_WORKER_RESULT",
            string.Empty,
            DateTimeOffset.UtcNow,
            WorkerResultPresent: true));

        Xunit.Assert.NotNull(reviewer.LastVerification?.MergedReviewFindings);
        Xunit.Assert.False(UnparseableFindingBlockersRoute.PrefersBlockersTextRoute(reviewer, "blocking defect"));
    }

    [Xunit.Fact]
    public void NeedsWorkWithBlockersAndEmptyFindingsRetriesDeveloperWithoutRedispatchingReviewer()
    {
        var (kernel, goal) = SoftwareGoal();
        var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task, hasCommittedChanges: task.RequiredRole == AgentRole.Developer);
        }

        const string blocker = "src/Target.cs:42 - the completed round still has a running process record.";
        DispatchTask(kernel, goal, reviewer, "review");
        kernel.RecordDispatchExecutionResult(goal.Id, reviewer.Id, new TaskVerificationRecord(
            "review",
            "C:\\tmp",
            1,
            string.Join(
                Environment.NewLine,
                "WORKER_RESULT:",
                "files: none",
                "commands: review",
                "tests: fail - inspected evidence",
                $"blockers: {blocker}",
                "findings:",
                "touched_anchors:",
                "verdict: needs-work",
                "END_WORKER_RESULT"),
            string.Empty,
            DateTimeOffset.UtcNow,
            StandardOutputPath: "C:\\tmp\\reviewer.out.log",
            WorkerResultPresent: true));

        var reviewerDispatchCount = reviewer.DispatchHistory.Count;
        TaskId? retriedTaskId = null;
        string? retryMessage = null;
        var escalationWritten = false;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            writeEscalation: (_, _, _) => escalationWritten = true,
            retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
            {
                retriedTaskId = taskId;
                retryMessage = message;
                return kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind);
            });

        var timelineCountBeforeAdvance = goal.Timeline.Count;
        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Xunit.Assert.Equal(developer.Id, retriedTaskId);
        Xunit.Assert.Contains(blocker, retryMessage, StringComparison.Ordinal);
        Xunit.Assert.Equal(reviewerDispatchCount, reviewer.DispatchHistory.Count);
        Xunit.Assert.DoesNotContain(goal.Timeline.Skip(timelineCountBeforeAdvance), evt =>
            evt.Message.Contains("structured finding result was missing or unparseable", StringComparison.Ordinal));
        Xunit.Assert.False(escalationWritten);
        Xunit.Assert.True(result.Outcome is ConductorAdvanceOutcome.Executed);
    }
}
