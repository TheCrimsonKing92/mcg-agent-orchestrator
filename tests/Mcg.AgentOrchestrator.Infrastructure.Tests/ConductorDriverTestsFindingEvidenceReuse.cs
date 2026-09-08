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
    public void UnmatchedFindingRoundAtSameCandidateReusesRequestBoundReceipt()
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
            id: "same-sha-original-finding");
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
        var unmatchedFinding = finding with
        {
            StableId = "same-sha-unmatched-finding",
            Description = "A new stable id requests the same focused evidence."
        };
        FailReviewerNeedsWork(kernel, goal, reviewer, "unmatched finding round", findings: [unmatchedFinding]);

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(1, focusedRuns);
        var latestFinding = reviewer.VerificationHistory.Last().MergedReviewFindings!
            .Single(item => item.StableId == unmatchedFinding.StableId);
        Assert.True(latestFinding.EvidenceOutcome?.Honoured);
        Assert.Equal(FindingEvidenceOutcomeReason.ValidEvidence, latestFinding.EvidenceOutcome?.ResultReason);
        Assert.Contains(goal.Timeline, item =>
            item.Message.Contains("finding-evidence disposition=reused-green", StringComparison.Ordinal) &&
            item.Message.Contains($"finding_id={unmatchedFinding.StableId}", StringComparison.Ordinal));
    }

    [Xunit.Fact]
    public void UnionRequestAtSameCandidateReusesConstituentGreenReceipts()
    {
        const string candidateSha = "abc1234";
        var (kernel, goal) = SoftwareGoal();
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task);
        }

        var first = EvidenceFindingWithRequest(
            "The first constituent needs focused evidence.",
            id: "union-first",
            classes: ["ConductorDriverTests"]);
        var second = EvidenceFindingWithRequest(
            "The second constituent needs focused evidence.",
            id: "union-second",
            classes: ["GoalAcceptanceVerifierTests"]);
        var union = EvidenceFindingWithRequest(
            "The compatible union should reuse both green constituents.",
            id: "union-request",
            classes: ["ConductorDriverTests", "GoalAcceptanceVerifierTests"]);
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

        FailReviewerNeedsWork(kernel, goal, reviewer, "first constituent", findings: [first]);
        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);
        FailReviewerNeedsWork(kernel, goal, reviewer, "second constituent", findings: [second]);
        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);
        FailReviewerNeedsWork(kernel, goal, reviewer, "union request", findings: [union]);

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(2, focusedRuns);
        var latestFinding = reviewer.VerificationHistory.Last().MergedReviewFindings!
            .Single(item => item.StableId == union.StableId);
        Assert.True(latestFinding.EvidenceOutcome?.Honoured);
        Assert.Equal(FindingEvidenceOutcomeReason.ValidEvidence, latestFinding.EvidenceOutcome?.ResultReason);
        Assert.StartsWith("finding-evidence-reuse-", latestFinding.EvidenceOutcome?.ReceiptId);
        Assert.Empty(
            reviewer.VerificationHistory.Last().FindingEvidenceReceipts!.Single().RequestDispositions ?? []);
    }

    [Xunit.Fact]
    public void NarrowerRequestAtSameCandidateReusesSingleCoveringGreenReceipt()
    {
        const string candidateSha = "abc1234";
        var (kernel, goal) = SoftwareGoal();
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task);
        }

        var broader = EvidenceFindingWithRequest(
            "The broader request establishes green evidence.",
            id: "broader-request",
            classes: ["ConductorDriverTests", "GoalAcceptanceVerifierTests"]);
        var narrower = EvidenceFindingWithRequest(
            "The narrower request is covered by the broader receipt.",
            id: "narrower-request",
            classes: ["ConductorDriverTests"]);
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

        FailReviewerNeedsWork(kernel, goal, reviewer, "broader request", findings: [broader]);
        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);
        FailReviewerNeedsWork(kernel, goal, reviewer, "narrower request", findings: [narrower]);
        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(1, focusedRuns);
        Assert.True(reviewer.VerificationHistory.Last().MergedReviewFindings!
            .Single(item => item.StableId == narrower.StableId)
            .EvidenceOutcome?.Honoured);
    }

    [Xunit.Fact]
    public void UnionRequestWithUncoveredSelectionDoesNotReuseConstituentReceipt()
    {
        const string candidateSha = "abc1234";
        var (kernel, goal) = SoftwareGoal();
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task);
        }

        var first = EvidenceFindingWithRequest(
            "The only covered constituent needs focused evidence.",
            id: "uncovered-first",
            classes: ["ConductorDriverTests"]);
        var union = EvidenceFindingWithRequest(
            "The union has an uncovered constituent and must run.",
            id: "uncovered-union",
            classes: ["ConductorDriverTests", "GoalAcceptanceVerifierTests"]);
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

        FailReviewerNeedsWork(kernel, goal, reviewer, "covered constituent", findings: [first]);
        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);
        FailReviewerNeedsWork(kernel, goal, reviewer, "uncovered union", findings: [union]);

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(2, focusedRuns);
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
