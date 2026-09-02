using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

using static ConductorDriverTests;

[Xunit.Collection("IsolatedProcessSpawning")]
public sealed class ConductorDriverTestsFindingEvidenceReuse
{
    [Xunit.Fact]
    public void ChangedFindingRoundAtSameCandidateReusesCandidateAndRequestBoundReceipt()
    {
        const string candidateSha = "abc1234";
        var (kernel, goal) = SoftwareGoal();
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task);
        }

        var finding = EvidenceFindingWithRequest(
            "The first finding round needs focused evidence.",
            id: "same-sha-new-round");
        FailReviewerNeedsWork(kernel, goal, reviewer, "first finding round", findings: [finding]);
        var focusedRuns = 0;
        var driver = MakeDriver(
            getPreReviewEvidenceContext: _ => NoPreReviewContext(candidateSha),
            runFocusedEvidence: (_, request) =>
            {
                focusedRuns++;
                return DualArmFindingEvidence(request, FindingEvidenceArmDisposition.Red, candidateSha);
            },
            dispatchAndStart: _ => DispatchStartOutcome.Started(),
            retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
                kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind),
            recordFindingEvidenceRequest: (goalId, taskId, message) =>
                kernel.RecordFindingEvidenceRequest(goalId, taskId, message),
            recordFindingEvidenceOutcome: (goalId, taskId, stableId, outcome, receipt) =>
                kernel.RecordFindingEvidenceOutcome(goalId, taskId, stableId, outcome, receipt));

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);
        var repeatedFinding = finding with { Description = "A changed finding round still needs focused evidence." };
        FailReviewerNeedsWork(kernel, goal, reviewer, "changed finding round", findings: [repeatedFinding]);

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(1, focusedRuns);
        var receipts = reviewer.VerificationHistory
            .SelectMany(verification => verification.FindingEvidenceReceipts ?? [])
            .DistinctBy(receipt => receipt.ReceiptId)
            .ToArray();
        var receipt = Assert.Single(receipts);
        Assert.Equal(candidateSha, receipt.CandidateSha);
        Assert.Equal(
            receipt.ReceiptId,
            reviewer.VerificationHistory.Last().MergedReviewFindings!.Single().EvidenceOutcome?.ReceiptId);
        Assert.Equal(
            receipt.ReceiptId,
            Assert.Single(reviewer.VerificationHistory.Last().FindingEvidenceReceipts!).ReceiptId);
        Assert.Contains(goal.Timeline, item =>
            item.Message.Contains("finding-evidence disposition=reused-green", StringComparison.Ordinal));
    }

    [Xunit.Fact]
    public void ChangedFindingRequestAtSameCandidateDoesNotReusePriorReceipt()
    {
        const string candidateSha = "abc1234";
        var (kernel, goal) = SoftwareGoal();
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task);
        }

        var finding = EvidenceFindingWithRequest(
            "The first request needs focused evidence.",
            id: "same-sha-changed-request",
            classes: ["ConductorDriverTests"]);
        FailReviewerNeedsWork(kernel, goal, reviewer, "first request", findings: [finding]);
        var focusedRequests = new List<string>();
        var driver = MakeDriver(
            getPreReviewEvidenceContext: _ => NoPreReviewContext(candidateSha),
            runFocusedEvidence: (_, request) =>
            {
                focusedRequests.Add(request);
                return DualArmFindingEvidence(request, FindingEvidenceArmDisposition.Red, candidateSha);
            },
            dispatchAndStart: _ => DispatchStartOutcome.Started(),
            retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
                kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind),
            recordFindingEvidenceRequest: (goalId, taskId, message) =>
                kernel.RecordFindingEvidenceRequest(goalId, taskId, message),
            recordFindingEvidenceOutcome: (goalId, taskId, stableId, outcome, receipt) =>
                kernel.RecordFindingEvidenceOutcome(goalId, taskId, stableId, outcome, receipt));

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);
        var changedRequest = EvidenceFindingWithRequest(
            "The changed request needs different evidence.",
            id: finding.StableId,
            classes: ["GoalAcceptanceVerifierTests"]);
        FailReviewerNeedsWork(kernel, goal, reviewer, "changed request", findings: [changedRequest]);

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(2, focusedRequests.Count);
        Assert.Contains("Infrastructure.Tests:ConductorDriverTests", focusedRequests);
        Assert.Contains("Infrastructure.Tests:GoalAcceptanceVerifierTests", focusedRequests);
        Assert.Equal(
            2,
            reviewer.VerificationHistory
                .SelectMany(verification => verification.FindingEvidenceReceipts ?? [])
                .DistinctBy(receipt => receipt.ReceiptId)
                .Count());
    }

    [Xunit.Fact]
    public void NonGreenReceiptAtSameCandidateDoesNotSuppressRerun()
    {
        const string candidateSha = "abc1234";
        var (kernel, goal) = SoftwareGoal();
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task);
        }

        var finding = EvidenceFindingWithRequest(
            "The request encountered an apparatus failure.",
            id: "same-sha-apparatus-failure");
        FailReviewerNeedsWork(kernel, goal, reviewer, "first attempt", findings: [finding]);
        var focusedRuns = 0;
        var driver = MakeDriver(
            getPreReviewEvidenceContext: _ => NoPreReviewContext(candidateSha),
            runFocusedEvidence: (_, request) =>
            {
                focusedRuns++;
                return new FocusedEvidenceRunResult(
                    request,
                    Accepted: true,
                    Passed: false,
                    Summary: "selection apparatus failed",
                    Checks: [],
                    OutcomeReason: FindingEvidenceOutcomeReason.ApparatusFailure);
            },
            dispatchAndStart: _ => DispatchStartOutcome.Started(),
            retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
                kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind),
            recordFindingEvidenceRequest: (goalId, taskId, message) =>
                kernel.RecordFindingEvidenceRequest(goalId, taskId, message),
            recordFindingEvidenceOutcome: (goalId, taskId, stableId, outcome, receipt) =>
                kernel.RecordFindingEvidenceOutcome(goalId, taskId, stableId, outcome, receipt));

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);
        FailReviewerNeedsWork(
            kernel,
            goal,
            reviewer,
            "same request after apparatus failure",
            findings: [finding with { Description = "The unchanged request must be reissued." }]);

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(2, focusedRuns);
        Assert.All(
            reviewer.VerificationHistory.SelectMany(item => item.FindingEvidenceReceipts ?? []),
            receipt => Assert.False(receipt.Passed));
    }
}
