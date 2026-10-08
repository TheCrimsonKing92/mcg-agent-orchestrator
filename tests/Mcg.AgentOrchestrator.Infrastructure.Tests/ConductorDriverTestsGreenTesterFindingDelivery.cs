using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

using static ConductorDriverTests;

[Xunit.Collection("IsolatedProcessSpawning")]
public sealed class ConductorDriverTestsGreenTesterFindingDelivery
{
    [Fact]
    public void GreenDeveloperFindingRunsQueuedRequestAndRoutesTester()
    {
        const string candidateSha = "abc1234";
        var root = ConductorDriverTests.CreateTempDirectory();
        try
        {
            var (kernel, goal) = SoftwareGoal();
            var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
            var tester = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
            PassVerification(kernel, goal, developer, hasCommittedChanges: true);
            DispatchTask(kernel, goal, tester, "test");
            var findings = new[]
            {
                EvidenceFindingWithRequest("First focused request", "green-first",
                    FindingCategory.TestCoverage, classes: ["ConductorDriverTests"]),
                EvidenceFindingWithRequest("Queued focused request", "green-second",
                    FindingCategory.TestEvidence, project: "Core.Tests", classes: ["GoalLifecycleTests"])
            };
            var stdout = string.Join(Environment.NewLine,
                "WORKER_RESULT:", "files: none", "commands: inspect focused behavior",
                "tests: deferred - acceptance owns execution", "commit: none", "blockers: none",
                $"findings: {JsonSerializer.Serialize(findings)}", "touched_anchors: []",
                "verdict: needs-work", "model_fit: fixture/model - adequate - source verification",
                "skills: none", "confidence: high", "END_WORKER_RESULT");
            kernel.RecordDispatchExecutionResult(goal.Id, tester.Id, new TaskVerificationRecord(
                "test", "C:\\tmp", 0, stdout, "", DateTimeOffset.UtcNow,
                StandardOutputPath: "C:\\tmp\\tester.out.log", WorkerResultPresent: true));

            var requests = new List<string>();
            var retried = new List<TaskId>();
            var messages = new List<string>();
            var driver = MakeDriver(
                getPreReviewEvidenceContext: _ => NoPreReviewContext(candidateSha),
                focusedEvidenceAttemptCoordinator: new ConductorParallelAcceptanceAttemptCoordinator(
                    root, runInline: true, acquireStableSlotLease: (_, _) => null),
                runFocusedEvidence: (_, request) =>
                {
                    requests.Add(request);
                    var green = ConductorDriverTestsFindingEvidenceReuse.RetainedEvidenceWithExecutedClasses(
                        root, request, candidateSha);
                    return green with
                    {
                        Arms = green.Arms!.Where(arm => arm.Arm == FindingEvidenceArm.Candidate).ToArray(),
                        OutcomeReason = FindingEvidenceOutcomeReason.ValidEvidence
                    };
                },
                retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
                {
                    retried.Add(taskId);
                    messages.Add(message);
                    return kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind);
                },
                recordFindingEvidenceRequest: (goalId, taskId, message) =>
                    kernel.RecordFindingEvidenceRequest(goalId, taskId, message),
                recordFindingEvidenceOutcome: (goalId, taskId, stableId, outcome, receipt) =>
                    kernel.RecordFindingEvidenceOutcome(goalId, taskId, stableId, outcome, receipt));

            for (var tick = 0; tick < 5 && retried.Count == 0; tick++)
                driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

            Assert.Equal(2, requests.Count);
            Assert.Contains(requests, request => request.Contains("Core.Tests:GoalLifecycleTests", StringComparison.Ordinal));
            Assert.Equal([tester.Id], retried);
            Assert.Contains("route=tester", Assert.Single(messages), StringComparison.Ordinal);
            Assert.Contains(FindingReceiptClosureDiagnosis.FindingCategoryNotTestEvidence, Assert.Single(messages), StringComparison.Ordinal);
            Assert.Contains(candidateSha, Assert.Single(messages), StringComparison.Ordinal);
            Assert.Contains("green-first", Assert.Single(messages), StringComparison.Ordinal);
            Assert.Contains("green-second", Assert.Single(messages), StringComparison.Ordinal);
            Assert.DoesNotContain(developer.Id, retried);
            Assert.Equal(0, FindingEvidenceExecutionClassifier.CountEvidenceDeliveryRetries(
                goal.Timeline, tester.Id, candidateSha, "green-first"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
