using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using System.Text.Json;

using static ConductorDriverTests;

[Xunit.Collection("IsolatedProcessSpawning")]
public sealed class ConductorDriverTestsUnusableBaselineCandidateRerun
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ApparatusFailureRerunsCandidateOnceAndRoutesByConfirmedOutcome(bool rerunGreen)
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
            FailReviewerNeedsWork(kernel, goal, reviewer, "Candidate RED needs a usable baseline",
                findings:
                [
                    EvidenceFindingWithRequest("Run focused evidence", "baseline-apparatus",
                        classes: ["ConductorDriverTests", "FindingEvidenceRoundPlannerTests"]),
                    EvidenceFindingWithRequest("Share the superset receipt", "baseline-apparatus-member",
                        classes: ["ConductorDriverTests"])
                ]);

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
                    if (runs == 3 && rerunGreen)
                    {
                        var green = ConductorDriverTestsFindingEvidenceReuse.RetainedEvidenceWithExecutedClasses(
                            root, request, candidateSha, ["ConductorDriverTests", "FindingEvidenceRoundPlannerTests"]);
                        return green with
                        {
                            Arms = green.Arms!.Where(arm => arm.Arm == FindingEvidenceArm.Candidate).ToArray(),
                            OutcomeReason = FindingEvidenceOutcomeReason.ValidEvidence
                        };
                    }
                    var red = CandidateRedFindingEvidence(
                        request, candidateSha, "OutsideTests.FailingMethod(passed: True)");
                    if (runs != 2)
                        return red with { Arms = red.Arms!.Where(arm => arm.Arm == FindingEvidenceArm.Candidate).ToArray() };
                    return red with
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
                recordFindingEvidenceRequestDispositions: (goalId, taskId, dispositions) =>
                    kernel.RecordFindingEvidenceRequestDispositions(goalId, taskId, dispositions),
                recordFindingEvidenceOutcome: (goalId, taskId, stableId, outcome, receipt) =>
                    kernel.RecordFindingEvidenceOutcome(goalId, taskId, stableId, outcome, receipt),
                writeEscalation: (_, _, message) => escalation = message);

            for (var tick = 0; tick < 5 && retried.Count == 0; tick++)
                driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

            Assert.Equal(3, runs);
            Assert.Single(goal.Timeline.Where(evt =>
                evt.Message.Contains("disposition=candidate-rerun-requested", StringComparison.Ordinal)));
            Assert.DoesNotContain(developer.Id, retried);
            if (rerunGreen)
            {
                Assert.Equal([reviewer.Id], retried);
                var reloaded = AgentOrchestratorKernel.FromSnapshot(JsonSerializer.Deserialize<OrchestratorSnapshot>(
                    JsonSerializer.Serialize(kernel.ExportSnapshot()))!);
                var durable = reloaded.Goals.Single().Tasks.Single(task => task.Id == reviewer.Id);
                var verification = durable.VerificationHistory.Last();
                var receipt = Assert.Single(verification.FindingEvidenceReceipts!, item =>
                    item.Arms!.Any(arm => arm.Arm == FindingEvidenceArm.Candidate &&
                        arm.Disposition == FindingEvidenceArmDisposition.Green));
                Assert.Equal(candidateSha, receipt.CandidateSha);
                Assert.Equal(2, Assert.Single(receipt.Arms!).ExecutedTestCount);
                Assert.Equal(2, receipt.RequestDispositions!.Count);
                Assert.Equal(2, verification.FindingEvidenceRequestDispositions!.Count);
                Assert.All(receipt.RequestDispositions, disposition =>
                {
                    Assert.Equal(disposition.FindingStableId == "baseline-apparatus"
                        ? FindingEvidenceRequestDecision.Executed
                        : FindingEvidenceRequestDecision.CoveredByReceipt, disposition.Decision);
                    Assert.Equal(receipt.ReceiptId, Assert.Single(disposition.SourceReceiptIds!));
                    Assert.Equal(candidateSha, disposition.CandidateSha);
                    Assert.Equal(receipt.FindingRoundFingerprint, disposition.FindingRoundFingerprint);
                    Assert.Equal(receipt.ExecutionBasisIdentity, disposition.ExecutionBasisIdentity);
                    var taskDisposition = Assert.Single(verification.FindingEvidenceRequestDispositions,
                        item => item.FindingStableId == disposition.FindingStableId);
                    Assert.Equal(disposition.Decision, taskDisposition.Decision);
                    Assert.Equal(receipt.ReceiptId, Assert.Single(taskDisposition.SourceReceiptIds!));
                });
                var owner = receipt.RequestDispositions.Single(item => item.FindingStableId == "baseline-apparatus");
                var member = receipt.RequestDispositions.Single(item => item.FindingStableId == "baseline-apparatus-member");
                Assert.Null(owner.CoveringRequestIdentity);
                Assert.Equal(owner.RequestIdentity, member.CoveringRequestIdentity);
                Assert.Equal("covered-by-executed-superset", member.Reason);
                Assert.Equal(2, verification.MergedReviewFindings!.Count);
                Assert.All(verification.MergedReviewFindings, finding =>
                {
                    Assert.Equal(FindingEvidenceOutcomeReason.ValidEvidence, finding.EvidenceOutcome?.ResultReason);
                    Assert.Equal(receipt.ReceiptId, finding.EvidenceOutcome?.ReceiptId);
                    Assert.Equal(FindingEvidenceExecutionState.ExecutedOnCandidate,
                        FindingEvidenceExecutionClassifier.Classify(durable, finding, candidateSha));
                });
            }
            else
            {
                Assert.Empty(retried);
                Assert.Contains("Baseline execution failure", escalation, StringComparison.Ordinal);
                Assert.Contains("No worker was dispatched", escalation, StringComparison.Ordinal);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void UsableGreenBaselineStillRoutesCandidateRedToDeveloper()
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
            FailReviewerNeedsWork(kernel, goal, reviewer, "Candidate RED needs a baseline",
                findings: [EvidenceFindingWithRequest("Run focused evidence", "baseline-green",
                    classes: ["ConductorDriverTests"])]);
            var runs = 0;
            var retried = new List<TaskId>();
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
                    return runs == 1
                        ? red with { Arms = red.Arms!.Where(arm => arm.Arm == FindingEvidenceArm.Candidate).ToArray() }
                        : red;
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

            for (var tick = 0; tick < 4 && retried.Count == 0; tick++)
                driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

            Assert.Equal(2, runs);
            Assert.Equal([developer.Id], retried);
            Assert.DoesNotContain(goal.Timeline, evt =>
                evt.Message.Contains("disposition=candidate-rerun-requested", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
