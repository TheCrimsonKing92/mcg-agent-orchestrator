using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

using static ConductorDriverTests;

[Xunit.Collection("IsolatedProcessSpawning")]
public sealed class ConductorDriverTestsCandidateRerunReceipts
{
    [Theory]
    [InlineData("fault")]
    [InlineData("cancelled")]
    [InlineData("rejected")]
    [InlineData("apparatus")]
    public void CandidateRerunUsesPositiveExecutionEvidence(string rerunOutcome)
    {
        const string candidateSha = "abc1234";
        var root = ConductorDriverTests.CreateTempDirectory();
        try
        {
            var (kernel, goal) = SoftwareGoal();
            var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
            var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
            foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
                PassVerification(kernel, goal, task, hasCommittedChanges: task == developer);
            FailReviewerNeedsWork(kernel, goal, reviewer, "candidate RED needs a usable baseline",
                findings: [EvidenceFindingWithRequest("Run focused evidence", "rerun-fault",
                    classes: ["ConductorDriverTests"])]);

            var runs = 0;
            var retried = new List<TaskId>();
            string? escalation = null;
            var driver = MakeDriver(
                getPreReviewEvidenceContext: _ => NoPreReviewContext(candidateSha),
                focusedEvidenceAttemptCoordinator: new ConductorParallelAcceptanceAttemptCoordinator(
                    root, Mcg.AgentOrchestrator.Infrastructure.TrunkBranchName.Default, runInline: true, acquireStableSlotLease: (_, _) => null),
                getLandingFileScopes: _ => [],
                runFocusedEvidence: (_, request) =>
                {
                    runs++;
                    var red = CandidateRedFindingEvidence(
                        request, candidateSha, "OutsideTests.FailingMethod(passed: True)");
                    if (runs == 3)
                    {
                        return rerunOutcome switch
                        {
                            "cancelled" => throw new OperationCanceledException("rerun cancelled"),
                            "fault" => throw new InvalidOperationException("rerun executor failed"),
                            "rejected" => red with { Accepted = false },
                            "apparatus" => red with
                            {
                                Arms = red.Arms!.Where(arm => arm.Arm == FindingEvidenceArm.Candidate)
                                    .Select(arm => arm with
                                    {
                                        Disposition = FindingEvidenceArmDisposition.ApparatusFailure,
                                        Passed = false
                                    }).ToArray(),
                                OutcomeReason = FindingEvidenceOutcomeReason.ApparatusFailure
                            },
                            _ => throw new InvalidOperationException("Unknown rerun outcome.")
                        };
                    }
                    if (runs == 4)
                    {
                        var green = ConductorDriverTestsFindingEvidenceReuse.RetainedEvidenceWithExecutedClasses(
                            root, request, candidateSha);
                        return green with
                        {
                            Arms = green.Arms!.Where(arm => arm.Arm == FindingEvidenceArm.Candidate).ToArray(),
                            OutcomeReason = FindingEvidenceOutcomeReason.ValidEvidence
                        };
                    }
                    return runs == 1
                        ? red with { Arms = red.Arms!.Where(arm => arm.Arm == FindingEvidenceArm.Candidate).ToArray() }
                        : red with
                        {
                            Arms = red.Arms!.Select(arm => arm.Arm == FindingEvidenceArm.Baseline
                                ? arm with { Disposition = FindingEvidenceArmDisposition.ApparatusFailure, Passed = false }
                                : arm).ToArray()
                        };
                },
                retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
                {
                    retried.Add(taskId);
                    return kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind);
                },
                recordFindingEvidenceRequest: (goalId, taskId, message) =>
                    kernel.RecordFindingEvidenceRequest(goalId, taskId, message),
                recordFindingEvidenceOutcome: (goalId, taskId, stableId, outcome, receipt) =>
                    kernel.RecordFindingEvidenceOutcome(goalId, taskId, stableId, outcome, receipt),
                writeEscalation: (_, _, message) => escalation = message);

            for (var tick = 0; tick < 5 && runs < 3; tick++)
                driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

            Assert.Equal(3, runs);
            Assert.DoesNotContain(developer.Id, retried);
            Assert.DoesNotContain(goal.Timeline, evt =>
                evt.Message.Contains("disposition=candidate-rerun-red", StringComparison.Ordinal));
            if (rerunOutcome == "cancelled")
            {
                Assert.Null(escalation);
                Assert.DoesNotContain(goal.Timeline, evt =>
                    evt.Message.Contains("disposition=candidate-rerun-unusable", StringComparison.Ordinal));
                for (var tick = 0; tick < 4 && retried.Count == 0; tick++)
                    driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);
                Assert.Equal(4, runs);
                Assert.Equal([reviewer.Id], retried);
            }
            else
            {
                Assert.Contains("Baseline execution failure", escalation, StringComparison.Ordinal);
                Assert.Single(goal.Timeline.Where(evt =>
                    evt.Message.Contains("disposition=candidate-rerun-unusable", StringComparison.Ordinal)));
                Assert.Empty(retried);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void GreenRerunReceiptRunsQueuedRequestBeforeDelivery()
    {
        const string candidateSha = "abc1234";
        var root = ConductorDriverTests.CreateTempDirectory();
        try
        {
            var (kernel, goal) = SoftwareGoal();
            var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
            var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
            foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
                PassVerification(kernel, goal, task, hasCommittedChanges: task == developer);
            FailReviewerNeedsWork(kernel, goal, reviewer, "both evidence requests need receipts",
                findings:
                [
                    EvidenceFindingWithRequest("Run candidate evidence", "rerun-green",
                        classes: ["ConductorDriverTests"]),
                    EvidenceFindingWithRequest("Run queued evidence", "queued-green",
                        project: "Core.Tests", classes: ["GoalLifecycleTests"])
                ]);

            var requests = new List<string>();
            var retried = new List<TaskId>();
            var driver = MakeDriver(
                getPreReviewEvidenceContext: _ => NoPreReviewContext(candidateSha),
                focusedEvidenceAttemptCoordinator: new ConductorParallelAcceptanceAttemptCoordinator(
                    root, Mcg.AgentOrchestrator.Infrastructure.TrunkBranchName.Default, runInline: true, acquireStableSlotLease: (_, _) => null),
                getLandingFileScopes: _ => [],
                runFocusedEvidence: (_, request) =>
                {
                    requests.Add(request);
                    if (requests.Count is 3 or 4)
                    {
                        var green = ConductorDriverTestsFindingEvidenceReuse.RetainedEvidenceWithExecutedClasses(
                            root, request, candidateSha);
                        return green with
                        {
                            Arms = green.Arms!.Where(arm => arm.Arm == FindingEvidenceArm.Candidate).ToArray(),
                            OutcomeReason = FindingEvidenceOutcomeReason.ValidEvidence
                        };
                    }
                    var red = CandidateRedFindingEvidence(
                        request, candidateSha, "OutsideTests.FailingMethod(passed: True)");
                    return requests.Count == 1
                        ? red with { Arms = red.Arms!.Where(arm => arm.Arm == FindingEvidenceArm.Candidate).ToArray() }
                        : red with
                        {
                            Arms = red.Arms!.Select(arm => arm.Arm == FindingEvidenceArm.Baseline
                                ? arm with { Disposition = FindingEvidenceArmDisposition.ApparatusFailure, Passed = false }
                                : arm).ToArray()
                        };
                },
                retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
                {
                    retried.Add(taskId);
                    return kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind);
                },
                recordFindingEvidenceRequest: (goalId, taskId, message) =>
                    kernel.RecordFindingEvidenceRequest(goalId, taskId, message),
                recordFindingEvidenceOutcome: (goalId, taskId, stableId, outcome, receipt) =>
                    kernel.RecordFindingEvidenceOutcome(goalId, taskId, stableId, outcome, receipt));

            for (var tick = 0; tick < 5 && requests.Count < 3; tick++)
                driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

            Assert.Equal(3, requests.Count);
            Assert.Empty(retried);
            var first = reviewer.VerificationHistory.Last().MergedReviewFindings!
                .Single(finding => finding.StableId == "rerun-green");
            var firstReceipt = Assert.Single(reviewer.VerificationHistory.Last().FindingEvidenceReceipts!
                .Where(receipt => receipt.ReceiptId == first.EvidenceOutcome?.ReceiptId));
            Assert.False(string.IsNullOrWhiteSpace(firstReceipt.ExecutionBasisIdentity));
            Assert.Contains(firstReceipt.RequestDispositions!, disposition =>
                disposition.FindingStableId == "rerun-green" &&
                disposition.Disposition.StartsWith("executed-", StringComparison.Ordinal));
            var rerunEvent = Assert.Single(goal.Timeline.Where(evt =>
                evt.Message.Contains("disposition=candidate-rerun-green", StringComparison.Ordinal)));
            Assert.Contains($"receipt_id={firstReceipt.ReceiptId}", rerunEvent.Message, StringComparison.Ordinal);

            for (var tick = 0; tick < 4 && retried.Count == 0; tick++)
                driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

            Assert.Equal(4, requests.Count);
            Assert.Contains(requests, request => request.Contains("Core.Tests:GoalLifecycleTests", StringComparison.Ordinal));
            Assert.Equal([reviewer.Id], retried);
            Assert.DoesNotContain(developer.Id, retried);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
