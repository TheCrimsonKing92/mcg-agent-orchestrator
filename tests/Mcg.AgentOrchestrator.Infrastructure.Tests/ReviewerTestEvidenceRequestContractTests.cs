using Mcg.AgentOrchestrator.Core;
using static ConductorDriverTests;

public sealed class ReviewerTestEvidenceRequestContractTests
{
    [Fact]
    public void OpenBlockingTestEvidenceWithoutRequestIsRejected()
    {
        var (kernel, goal) = SoftwareGoal();
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
            PassVerification(kernel, goal, task);
        FailReviewerNeedsWork(kernel, goal, reviewer, "focused receipt missing",
            findings:
            [
                new ReviewFinding("reviewer-missing-receipt", ReviewFindingState.Open,
                    new ReviewFindingLocation("tests/Receipt.cs", "Receipt.Class"),
                    "Focused receipt missing.", FindingSeverity.Blocking, FindingCategory.TestEvidence)
            ]);

        var violation = Assert.IsType<ReviewFindingContractViolation>(
            reviewer.LastVerification!.ReviewFindingContractViolation);
        Assert.Equal("ERR_REVIEWER_TEST_EVIDENCE_REQUEST_MISSING", violation.Code);
        Assert.Equal("reviewer-missing-receipt", violation.SubmittedStableId);
        Assert.Contains("evidence_request:{selections:[{test_project,test_class}]}",
            violation.Message, StringComparison.Ordinal);
        Assert.Equal(WorkTaskStatus.Failed, reviewer.Status);
    }
}
