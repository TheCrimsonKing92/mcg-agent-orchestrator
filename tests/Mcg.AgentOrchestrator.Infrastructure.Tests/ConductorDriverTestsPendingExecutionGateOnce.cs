using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

using static ConductorDriverTests;

[Xunit.Collection("IsolatedProcessSpawning")]
public sealed class ConductorDriverTestsPendingExecutionGateOnce
{
    [Fact]
    public void RunningAttemptRecordsOnePendingRequestAcrossTicks()
    {
        const string candidateSha = "abc1234";
        var root = CreateTempDirectory();
        try
        {
            var (kernel, goal) = SoftwareGoal();
            var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
            var tester = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
            PassVerification(kernel, goal, developer, hasCommittedChanges: true);
            DispatchTask(kernel, goal, tester, "test");
            var finding = EvidenceFindingWithRequest(
                "The candidate needs focused evidence before a Developer retry.",
                id: "pending-once", category: FindingCategory.Correctness,
                classes: ["ConductorDriverTests"]);
            var stdout = string.Join(Environment.NewLine,
                "WORKER_RESULT:", "files: none", "commands: inspect focused behavior",
                "tests: deferred - acceptance owns execution", "commit: none", "blockers: none",
                $"findings: {JsonSerializer.Serialize(new[] { finding })}",
                "touched_anchors: []", "verdict: needs-work",
                "model_fit: fixture/model - adequate - source verification",
                "skills: none", "confidence: high", "END_WORKER_RESULT");
            kernel.RecordDispatchExecutionResult(goal.Id, tester.Id, new TaskVerificationRecord(
                "test", "C:\\tmp", 0, stdout, "", DateTimeOffset.UtcNow,
                StandardOutputPath: "C:\\tmp\\tester.out.log", WorkerResultPresent: true));

            var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
                root, isProcessAlive: processId => processId == 7102,
                launchOwnedProcess: _ => new ConductorParallelAcceptanceOwnedProcessLaunchResult(7102),
                acquireStableSlotLease: (_, _) => null);
            var driver = MakeDriver(
                getPreReviewEvidenceContext: _ => NoPreReviewContext(candidateSha),
                focusedEvidenceAttemptCoordinator: coordinator,
                runFocusedEvidence: (_, request) => PassingPreReviewEvidence(request),
                recordFindingEvidenceRequest: (goalId, taskId, message) =>
                    kernel.RecordFindingEvidenceRequest(goalId, taskId, message),
                recordFindingEvidenceOutcome: (goalId, taskId, stableId, outcome, receipt) =>
                    kernel.RecordFindingEvidenceOutcome(goalId, taskId, stableId, outcome, receipt));

            for (var tick = 0; tick < 4; tick++)
            {
                driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);
            }

            Assert.Single(goal.Timeline.Where(evt =>
                evt.Kind == ProgressKind.FindingEvidenceRequestRecorded &&
                evt.Message.Contains("disposition=pending-execution-gate", StringComparison.Ordinal) &&
                evt.Message.Contains($"candidate_sha={candidateSha}", StringComparison.Ordinal) &&
                evt.Message.Contains("finding_round=", StringComparison.Ordinal) &&
                evt.Message.Contains("request_identity=", StringComparison.Ordinal)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
