using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

using static ConductorDriverTests;

[Xunit.Collection("IsolatedProcessSpawning")]
public sealed class ConductorDriverTestsFindingEvidenceNameSelector
{
    [Xunit.Fact]
    public void ReviewerNameOperandReachesRunnerAndReceiptAsFullyQualifiedName()
    {
        var (kernel, goal) = SoftwareGoal();
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task);
        }

        var finding = EvidenceFindingWithRequest(
            "The method selector must reach focused evidence execution.",
            id: "name-operand",
            classes: ["Name~MissingMethod"]);
        FailReviewerNeedsWork(kernel, goal, reviewer, "focused evidence required", findings: [finding]);
        string? observedRequest = null;
        var focusedRuns = 0;
        var driver = MakeDriver(
            getPreReviewEvidenceContext: _ => NoPreReviewContext("abc1234"),
            runFocusedEvidence: (_, request) =>
            {
                focusedRuns++;
                observedRequest = request;
                return new FocusedEvidenceRunResult(request, true, true, "method selector accepted", []);
            },
            retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
                kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind),
            recordFindingEvidenceOutcome: (goalId, taskId, stableId, outcome, receipt) =>
                kernel.RecordFindingEvidenceOutcome(goalId, taskId, stableId, outcome, receipt));

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(1, focusedRuns);
        Assert.Equal("Infrastructure.Tests:FullyQualifiedName~MissingMethod", observedRequest);
        Assert.Equal(
            "FullyQualifiedName~MissingMethod",
            reviewer.VerificationHistory.Last().FindingEvidenceReceipts!.Single()
                .Request.Selections.Single().TestClass);
    }

    [Xunit.Theory]
    [Xunit.InlineData("DisplayName~MissingMethod")]
    [Xunit.InlineData("Name!~MissingMethod")]
    public void ReviewerUnsupportedOperandIsRefusedWithoutRunningEvidence(string filter)
    {
        var (kernel, goal) = SoftwareGoal();
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task);
        }

        var finding = EvidenceFindingWithRequest(
            "Unsupported method selectors must remain refused.",
            id: "unsupported-operand",
            classes: [filter]);
        FailReviewerNeedsWork(kernel, goal, reviewer, "focused evidence required", findings: [finding]);
        var focusedRuns = 0;
        var driver = MakeDriver(
            getPreReviewEvidenceContext: _ => NoPreReviewContext("abc1234"),
            runFocusedEvidence: (_, request) =>
            {
                focusedRuns++;
                return new FocusedEvidenceRunResult(request, true, true, "unexpected", []);
            },
            retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
                kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind),
            recordFindingEvidenceOutcome: (goalId, taskId, stableId, outcome, receipt) =>
                kernel.RecordFindingEvidenceOutcome(goalId, taskId, stableId, outcome, receipt));

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(0, focusedRuns);
        Assert.Equal(
            FindingEvidenceNotHonouredReason.UnparseableSelection,
            reviewer.VerificationHistory.Last().MergedReviewFindings!
                .Single(item => item.StableId == "unsupported-operand").EvidenceOutcome?.Reason);
    }
}
