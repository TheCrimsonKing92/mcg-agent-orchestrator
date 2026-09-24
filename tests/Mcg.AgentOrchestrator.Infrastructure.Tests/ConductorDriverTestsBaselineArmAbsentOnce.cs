using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

using static ConductorDriverTests;

public sealed class ConductorDriverTestsBaselineArmAbsentOnce
{
    [Fact]
    public void CandidateOnlyBaselineAttemptRecordsOnceAndRoutesDeveloper()
    {
        const string candidateSha = "abc1234";
        var root = CreateTempDirectory();
        try
        {
            var storeDirectory = Path.Combine(root, ".orchestrator");
            Directory.CreateDirectory(storeDirectory);
            var (kernel, goal) = SoftwareGoal();
            var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
            var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
            foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
            {
                PassVerification(kernel, goal, task, hasCommittedChanges: task == developer);
            }
            FailReviewerNeedsWork(kernel, goal, reviewer, "candidate RED requires a baseline",
                findings: [EvidenceFindingWithRequest("Find the failing test", id: "baseline-missing",
                    classes: ["ConductorDriverTests"])]);

            var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
                root, runInline: true, acquireStableSlotLease: (_, _) => null);
            var retries = 0;
            string? retryMessage = null;
            var driver = MakeDriver(
                getPreReviewEvidenceContext: _ => NoPreReviewContext(candidateSha),
                focusedEvidenceAttemptCoordinator: coordinator,
                executionDirectory: root,
                getLandingFileScopes: _ => [],
                runFocusedEvidence: (_, request) =>
                {
                    var red = CandidateRedFindingEvidence(
                        request, candidateSha, "OutsideTests.FailingMethod(passed: True)");
                    return red with { Arms = red.Arms!.Where(arm => arm.Arm == FindingEvidenceArm.Candidate).ToArray() };
                },
                retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
                {
                    Assert.Equal(developer.Id, taskId);
                    retries++;
                    retryMessage = message;
                    return kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind);
                },
                recordFindingEvidenceRequest: (goalId, taskId, message) =>
                    kernel.RecordFindingEvidenceRequest(goalId, taskId, message),
                recordFindingEvidenceOutcome: (goalId, taskId, stableId, outcome, receipt) =>
                    kernel.RecordFindingEvidenceOutcome(goalId, taskId, stableId, outcome, receipt));

            for (var tick = 0; tick < 5; tick++)
            {
                driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);
            }

            var attempts = Directory.EnumerateFiles(root, "*.attempt.json", SearchOption.AllDirectories)
                .Select(path => JsonDocument.Parse(File.ReadAllText(path)))
                .ToArray();
            try
            {
                Assert.Single(attempts.Where(attempt =>
                    attempt.RootElement.GetProperty("focusedEvidenceRunsBaselineArm").GetBoolean()));
            }
            finally
            {
                foreach (var attempt in attempts) attempt.Dispose();
            }
            var attention = Assert.Single(goal.Timeline.Where(evt =>
                evt.Message.Contains("disposition=baseline-arm-absent", StringComparison.Ordinal)));
            Assert.Contains("baseline arm absent", attention.Message, StringComparison.OrdinalIgnoreCase);
            var notice = Assert.Single(CollaborationItemStore.OpenExisting(storeDirectory)
                .ListAsync(goal.Id.Value).GetAwaiter().GetResult());
            Assert.Equal(CollaborationItemType.Notice, notice.Type);
            Assert.Contains("baseline arm absent", notice.Body, StringComparison.OrdinalIgnoreCase);
            var finding = reviewer.VerificationHistory.Last().MergedReviewFindings!
                .Single(item => item.StableId == "baseline-missing");
            Assert.False(string.IsNullOrWhiteSpace(finding.EvidenceOutcome?.ReceiptId));
            Assert.Equal(1, retries);
            Assert.Contains("ACTIONABLE_CANDIDATE_RED", retryMessage, StringComparison.Ordinal);
            Assert.Contains("OutsideTests.FailingMethod(passed: True)", retryMessage, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
