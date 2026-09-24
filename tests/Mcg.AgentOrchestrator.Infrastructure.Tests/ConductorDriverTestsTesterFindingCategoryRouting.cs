using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using static ConductorDriverTests;

[Xunit.Collection("IsolatedProcessSpawning")]
public sealed class ConductorDriverTestsTesterFindingCategoryRouting
{
    [Xunit.Fact]
    public void CurrentReceiptOnTestEvidenceFindingDoesNotRetryDeveloper()
    {
        const string candidateSha = "abc1234";
        var (kernel, goal) = SoftwareGoal();
        var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
        var tester = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
        PassVerification(kernel, goal, developer, hasCommittedChanges: true);
        DispatchTask(kernel, goal, tester, "test");
        var request = new FindingEvidenceRequest(
            [new FindingEvidenceSelection("Infrastructure.Tests", "ConductorDriverTests")]);
        var finding = new ReviewFinding("current-receipt", ReviewFindingState.Open,
            new ReviewFindingLocation("tests/Receipt.cs", "Receipt.Class"),
            "Focused test evidence still needs a finding disposition.",
            FindingSeverity.Blocking, FindingCategory.TestEvidence, request);
        var output = string.Join(Environment.NewLine,
            "WORKER_RESULT:", "files: none", "commands: inspect receipt",
            "tests: fail - current receipt needs disposition", "commit: none",
            "blockers: exact-blocker - current receipt needs disposition",
            $"findings: {JsonSerializer.Serialize(new[] { finding })}",
            "touched_anchors: []", "verdict: needs-work",
            "model_fit: fixture/model - adequate - routing fixture", "skills: none",
            "confidence: high", "END_WORKER_RESULT");
        kernel.RecordDispatchExecutionResult(goal.Id, tester.Id, new TaskVerificationRecord(
            "test", @"C:\tmp", 1, output, string.Empty, DateTimeOffset.UtcNow,
            WorkerResultPresent: true));
        kernel.RecordFindingEvidenceOutcome(goal.Id, tester.Id, finding.StableId,
            new FindingEvidenceOutcome(true, "current-receipt-id",
                ResultReason: FindingEvidenceOutcomeReason.ValidEvidence),
            new FindingEvidenceReceipt("current-receipt-id", candidateSha, request,
                Accepted: true, Passed: true, "focused class passed",
                RequestDispositions:
                [
                    new FindingEvidenceRequestDisposition(finding.StableId,
                        "Infrastructure.Tests:ConductorDriverTests", "executed-standalone", "only-request")
                ]));

        var focusedRuns = 0;
        TaskId? retriedTaskId = null;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            getPreReviewEvidenceContext: _ => NoPreReviewContext(candidateSha),
            runFocusedEvidence: (_, actualRequest) =>
            {
                focusedRuns++;
                return new FocusedEvidenceRunResult(actualRequest, true, true, "unexpected run", []);
            },
            dispatchAndStart: _ => DispatchStartOutcome.Started(),
            retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
            {
                retriedTaskId = taskId;
                return kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind);
            });

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Xunit.Assert.Equal(0, focusedRuns);
        Xunit.Assert.Equal(tester.Id, retriedTaskId);
        Xunit.Assert.NotEqual(developer.Id, retriedTaskId);
    }
}
