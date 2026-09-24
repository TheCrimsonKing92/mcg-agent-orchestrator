using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using static ConductorDriverTests;

public sealed class TesterTestEvidenceRequestContractTests
{
    [Fact]
    public void OpenBlockingTestEvidenceWithoutRequestIsRejectedWithItsIdentityAndShape()
    {
        var (_, goal, tester) = RecordFinding(FindingSeverity.Blocking,
            ReviewFindingState.Open, FindingCategory.TestEvidence, request: null);

        var violation = Assert.IsType<ReviewFindingContractViolation>(
            tester.LastVerification!.ReviewFindingContractViolation);
        Assert.Equal("ERR_TESTER_TEST_EVIDENCE_REQUEST_MISSING", violation.Code);
        Assert.Equal("missing-class-receipt", violation.SubmittedStableId);
        Assert.Contains("stable_id=missing-class-receipt", violation.Message, StringComparison.Ordinal);
        Assert.Contains("evidence_request:{selections:[{test_project,test_class}]}",
            violation.Message, StringComparison.Ordinal);
        Assert.Equal(WorkTaskStatus.Failed, tester.Status);
        Assert.Contains(goal.Timeline, evt =>
            evt.Kind == ProgressKind.ReviewFindingContractViolationRecorded &&
            evt.Message.Contains(violation.Code, StringComparison.Ordinal));
    }

    [Fact]
    public void MissingRequestUsesTesterContractRepairInsteadOfDeveloperRetry()
    {
        var (kernel, goal, tester) = RecordFinding(FindingSeverity.Blocking,
            ReviewFindingState.Open, FindingCategory.TestEvidence, request: null);
        TaskId? retriedTaskId = null;
        string? retryMessage = null;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            dispatchAndStart: _ => DispatchStartOutcome.Started(),
            retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
            {
                retriedTaskId = taskId;
                retryMessage = message;
                return kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind);
            });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(tester.Id, retriedTaskId);
        Assert.StartsWith("review-finding contract-repair:", retryMessage, StringComparison.Ordinal);
        Assert.Contains("missing-class-receipt", retryMessage, StringComparison.Ordinal);
        Assert.IsType<ConductorAdvanceOutcome.Executed>(result.Outcome);
    }

    [Theory]
    [InlineData(FindingSeverity.Advisory, ReviewFindingState.Open, FindingCategory.TestEvidence)]
    [InlineData(FindingSeverity.Blocking, ReviewFindingState.Resolved, FindingCategory.TestEvidence)]
    [InlineData(FindingSeverity.Blocking, ReviewFindingState.Open, FindingCategory.Correctness)]
    public void OtherFindingsDoNotRequireARequest(
        FindingSeverity severity, ReviewFindingState state, FindingCategory category)
    {
        var (_, _, tester) = RecordFinding(severity, state, category, request: null);

        Assert.Null(tester.LastVerification!.ReviewFindingContractViolation);
    }

    [Fact]
    public void TypedRequestSatisfiesTheContract()
    {
        var request = new FindingEvidenceRequest(
            [new FindingEvidenceSelection("Infrastructure.Tests", "ConductorDriverTests")]);
        var (_, _, tester) = RecordFinding(FindingSeverity.Blocking,
            ReviewFindingState.Open, FindingCategory.TestEvidence, request);

        Assert.Null(tester.LastVerification!.ReviewFindingContractViolation);
    }

    private static (AgentOrchestratorKernel Kernel, Goal Goal, TaskSpec Tester) RecordFinding(
        FindingSeverity severity, ReviewFindingState state, FindingCategory category,
        FindingEvidenceRequest? request)
    {
        var (kernel, goal) = SoftwareGoal();
        var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
        var tester = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
        PassVerification(kernel, goal, developer, hasCommittedChanges: true);
        DispatchTask(kernel, goal, tester, "test");
        var finding = new ReviewFinding("missing-class-receipt", state,
            new ReviewFindingLocation("tests/Receipt.cs", "Receipt.Class"),
            "The focused class needs execution.", severity, category, request);
        var output = string.Join(Environment.NewLine,
            "WORKER_RESULT:", "files: none", "commands: inspect",
            "tests: deferred - focused class requested", "commit: none", "blockers: none",
            $"findings: {JsonSerializer.Serialize(new[] { finding })}",
            "touched_anchors: []", "verdict: needs-work",
            "model_fit: fixture/model - adequate - contract fixture", "skills: none",
            "confidence: high", "END_WORKER_RESULT");
        kernel.RecordDispatchExecutionResult(goal.Id, tester.Id, new TaskVerificationRecord(
            "test", @"C:\tmp", 0, output, string.Empty, DateTimeOffset.UtcNow,
            WorkerResultPresent: true));
        return (kernel, goal, tester);
    }
}
