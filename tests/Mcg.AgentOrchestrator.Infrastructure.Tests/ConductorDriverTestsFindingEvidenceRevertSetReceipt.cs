using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using static ConductorDriverTests;

[Xunit.Collection("IsolatedProcessSpawning")]
public sealed class ConductorDriverTestsFindingEvidenceRevertSetReceipt
{
    [Fact]
    public void TesterFinding_PersistsRestoredAndDroppedPaths()
    {
        const string candidate = "0123456789abcdef0123456789abcdef01234567";
        var root = ConductorDriverTests.CreateTempDirectory();
        try
        {
            var (kernel, goal) = SoftwareGoal();
            var tester = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
            foreach (var task in goal.Tasks.TakeWhile(task => task.Id != tester.Id))
                PassVerification(kernel, goal, task, hasCommittedChanges: task.RequiredRole == AgentRole.Developer);
            var finding = EvidenceFindingWithRequest("Negative control is missing", "declared-revert") with
            {
                EvidenceRequest = new FindingEvidenceRequest([new("Infrastructure.Tests", "ConductorDriverTests")],
                    FindingEvidenceNegativeControl.RevertSrc, ["tests/A/Support.cs", "tests/A/Counter.cs"])
            };
            DispatchTask(kernel, goal, tester, "test", workingDirectory: root, baseCommit: candidate);
            var stdout = string.Join(Environment.NewLine,
                "WORKER_RESULT:", "files: none", "commands: inspect", "tests: deferred - conductor evidence",
                "commit: none", "blockers: none", $"findings: {JsonSerializer.Serialize(new[] { finding })}",
                "touched_anchors: []", "verdict: needs-work", "model_fit: fixture/model - adequate - receipt",
                "skills: none", "confidence: high", "END_WORKER_RESULT");
            kernel.RecordDispatchExecutionResult(goal.Id, tester.Id, new TaskVerificationRecord(
                "test", root, 0, stdout, "", DateTimeOffset.UtcNow,
                StandardOutputPath: Path.Combine(root, "worker.out.log"), WorkerResultPresent: true));
            var calls = 0;
            var driver = MakeDriver(
                getPreReviewEvidenceContext: _ => NoPreReviewContext(candidate),
                focusedEvidenceAttemptCoordinator: new ConductorParallelAcceptanceAttemptCoordinator(
                    root, runInline: true, acquireStableSlotLease: (_, _) => null),
                runFocusedEvidence: (_, request) =>
                {
                    calls++;
                    var green = ConductorDriverTestsFindingEvidenceReuse.RetainedEvidenceWithExecutedClasses(root, request, candidate);
                    var candidateArm = green.Arms!.Single(arm => arm.Arm == FindingEvidenceArm.Candidate);
                    return green with
                    {
                        Arms = [candidateArm, new(FindingEvidenceArm.SourceReverted, candidate,
                            FindingEvidenceArmDisposition.Red, true, false, "red", [],
                            RestoredPaths: ["tests/A/Support.cs"], DroppedPaths: ["tests/A/Counter.cs"])],
                        NegativeControlOutcome = FindingEvidenceNegativeControlOutcome.Demonstrated
                    };
                },
                retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
                    kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind),
                recordFindingEvidenceRequest: (goalId, taskId, message) =>
                    kernel.RecordFindingEvidenceRequest(goalId, taskId, message),
                recordFindingEvidenceOutcome: (goalId, taskId, stableId, outcome, receipt) =>
                    kernel.RecordFindingEvidenceOutcome(goalId, taskId, stableId, outcome, receipt));
            for (var tick = 1; tick <= 6 && !tester.VerificationHistory.Any(verification => verification.FindingEvidenceReceipts?.Count > 0); tick++)
            {
                driver.BeginTick(kernel, tick);
                driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);
            }
            Assert.Equal(1, calls);
            var receipt = Assert.Single(tester.VerificationHistory.SelectMany(verification => verification.FindingEvidenceReceipts ?? [])
                .DistinctBy(receipt => receipt.ReceiptId));
            Assert.Equal(FindingEvidenceNegativeControl.RevertSrc, receipt.Request.NegativeControl);
            Assert.Equal(finding.EvidenceRequest.RevertPaths, receipt.Request.RevertPaths);
            var arm = Assert.Single(receipt.Arms!, arm => arm.Arm == FindingEvidenceArm.SourceReverted);
            Assert.Equal(["tests/A/Support.cs"], arm.RestoredPaths);
            Assert.Equal(["tests/A/Counter.cs"], arm.DroppedPaths);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
