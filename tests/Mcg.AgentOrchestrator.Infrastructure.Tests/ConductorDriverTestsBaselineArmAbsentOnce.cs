using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

using static ConductorDriverTests;

public sealed class ConductorDriverTestsBaselineArmAbsentOnce
{
    [Theory]
    [InlineData("build-slot", false)]
    [InlineData("build-lock", false)]
    [InlineData("cancelled", false)]
    [InlineData("runner-fault", true)]
    public void BaselineAttemptOutcomeRequiresPositiveFaultEvidence(string failure, bool isFault)
    {
        const string candidateSha = "abc1234";
        var root = ConductorDriverTests.CreateTempDirectory();
        try
        {
            var (kernel, goal) = SoftwareGoal();
            var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
            var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
            foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
            {
                PassVerification(kernel, goal, task, hasCommittedChanges: task == developer);
            }
            FailReviewerNeedsWork(kernel, goal, reviewer, "candidate RED requires a baseline",
                findings: [EvidenceFindingWithRequest("Find the failing test", id: "baseline-fault",
                    classes: ["ConductorDriverTests"])]);

            var focusedRuns = 0;
            var outcomes = 0;
            var retries = 0;
            var driver = MakeDriver(
                getPreReviewEvidenceContext: _ => NoPreReviewContext(candidateSha),
                focusedEvidenceAttemptCoordinator: new ConductorParallelAcceptanceAttemptCoordinator(
                    root, Mcg.AgentOrchestrator.Infrastructure.TrunkBranchName.Default, runInline: true, acquireStableSlotLease: (_, _) => null),
                executionDirectory: root,
                getLandingFileScopes: _ => [],
                runFocusedEvidence: (_, request) =>
                {
                    if (++focusedRuns == 1)
                    {
                        var red = CandidateRedFindingEvidence(
                            request, candidateSha, "OutsideTests.FailingMethod(passed: True)");
                        return red with { Arms = red.Arms!.Where(arm => arm.Arm == FindingEvidenceArm.Candidate).ToArray() };
                    }
                    throw failure switch
                    {
                        "build-slot" => new DotnetBuildSlotsBusyException(
                            new DotnetBuildLeaseAcquisition.SlotsBusy("focused", [])),
                        "build-lock" => new BuildLockBlockedException(
                            new BuildLockAttribution("locked.dll", [], "test")),
                        "cancelled" => new OperationCanceledException("cancelled"),
                        _ => new InvalidOperationException("runner fault")
                    };
                },
                retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
                {
                    retries++;
                    return kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind);
                },
                recordFindingEvidenceRequest: (goalId, taskId, message) =>
                    kernel.RecordFindingEvidenceRequest(goalId, taskId, message),
                recordFindingEvidenceOutcome: (goalId, taskId, stableId, outcome, receipt) =>
                {
                    outcomes++;
                    kernel.RecordFindingEvidenceOutcome(goalId, taskId, stableId, outcome, receipt);
                });

            for (var tick = 0; tick < 3; tick++)
            {
                driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);
            }

            Assert.Equal(isFault ? 1 : 0, outcomes);
            Assert.Equal(0, retries);
            Assert.Equal(isFault ? 1 : 0, goal.Timeline.Count(evt =>
                evt.Message.Contains("disposition=candidate-rerun-requested", StringComparison.Ordinal)));
            Assert.Equal(isFault ? 1 : 0, goal.Timeline.Count(evt =>
                evt.Message.Contains("disposition=baseline-arm-absent", StringComparison.Ordinal)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CandidateOnlyBaselineAttemptRecordsOnceAndRerunsCandidate()
    {
        const string candidateSha = "abc1234";
        var root = ConductorDriverTests.CreateTempDirectory();
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
                root, Mcg.AgentOrchestrator.Infrastructure.TrunkBranchName.Default, runInline: true, acquireStableSlotLease: (_, _) => null);
            var retries = 0;
            var outcomes = 0;
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
                    retries++;
                    return kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind);
                },
                recordFindingEvidenceRequest: (goalId, taskId, message) =>
                    kernel.RecordFindingEvidenceRequest(goalId, taskId, message),
                recordFindingEvidenceOutcome: (goalId, taskId, stableId, outcome, receipt) =>
                {
                    outcomes++;
                    kernel.RecordFindingEvidenceOutcome(goalId, taskId, stableId, outcome, receipt);
                });

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
                Assert.Equal(3, attempts.Length);
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
            Assert.Equal(1, outcomes);
            Assert.Equal(0, retries);
            Assert.Single(goal.Timeline.Where(evt =>
                evt.Message.Contains("disposition=candidate-rerun-requested", StringComparison.Ordinal)));
            Assert.Single(goal.Timeline.Where(evt =>
                evt.Message.Contains("disposition=candidate-rerun-red", StringComparison.Ordinal)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DetachedBaselineAttemptIsReconciledAsBaselineAcrossTicks(bool processDies)
    {
        const string candidateSha = "abc1234";
        var root = ConductorDriverTests.CreateTempDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, ".orchestrator"));
            var (kernel, goal) = SoftwareGoal();
            var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
            var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
            foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
            {
                PassVerification(kernel, goal, task, hasCommittedChanges: task == developer);
            }
            FailReviewerNeedsWork(kernel, goal, reviewer, "candidate RED requires a baseline",
                findings: [EvidenceFindingWithRequest("Find the failing test", id: "detached-baseline",
                    classes: ["ConductorDriverTests"])]);

            var launches = 0;
            var outcomes = 0;
            var retries = 0;
            string? escalation = null;
            var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
                root, Mcg.AgentOrchestrator.Infrastructure.TrunkBranchName.Default,
                isProcessAlive: _ => false,
                launchOwnedProcess: _ => new ConductorParallelAcceptanceOwnedProcessLaunchResult(7100 + ++launches),
                acquireStableSlotLease: (_, _) => null,
                recentHeartbeatGrace: TimeSpan.Zero);
            var driver = MakeDriver(
                getPreReviewEvidenceContext: _ => NoPreReviewContext(candidateSha),
                focusedEvidenceAttemptCoordinator: coordinator,
                executionDirectory: root,
                getLandingFileScopes: _ => [],
                runFocusedEvidence: (_, request) => CandidateRedFindingEvidence(
                    request, candidateSha, "OutsideTests.FailingMethod(passed: True)"),
                retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
                {
                    retries++;
                    return kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind);
                },
                recordFindingEvidenceRequest: (goalId, taskId, message) =>
                    kernel.RecordFindingEvidenceRequest(goalId, taskId, message),
                recordFindingEvidenceOutcome: (goalId, taskId, stableId, outcome, receipt) =>
                {
                    outcomes++;
                    kernel.RecordFindingEvidenceOutcome(goalId, taskId, stableId, outcome, receipt);
                },
                writeEscalation: (_, _, message) => escalation = message);

            driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);
            var candidateAttempt = Assert.Single(coordinator.GetUnreconciledAttempts([goal.Id.Value]));
            Assert.False(candidateAttempt.FocusedEvidenceRunsBaselineArm);
            CompleteCandidateOnly(candidateAttempt);

            driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);
            var baselineAttempt = Assert.Single(coordinator.GetUnreconciledAttempts([goal.Id.Value]));
            Assert.True(baselineAttempt.FocusedEvidenceRunsBaselineArm);
            Assert.NotNull(baselineAttempt.CandidateEvidenceBeforeBaseline);
            if (!processDies) CompleteCandidateOnly(baselineAttempt);

            for (var tick = 0; tick < 4; tick++)
            {
                driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);
            }

            Assert.Equal(3, launches);
            Assert.Equal(1, outcomes);
            Assert.Equal(0, retries);
            Assert.Contains("Baseline execution failure", escalation, StringComparison.Ordinal);
            Assert.Single(goal.Timeline.Where(evt =>
                evt.Message.Contains("disposition=candidate-rerun-unusable", StringComparison.Ordinal)));
            Assert.Single(goal.Timeline.Where(evt =>
                evt.Message.Contains("disposition=baseline-arm-absent", StringComparison.Ordinal)));
            Assert.Single(CollaborationItemStore.OpenExisting(Path.Combine(root, ".orchestrator"))
                .ListAsync(goal.Id.Value).GetAwaiter().GetResult());
            Assert.False(string.IsNullOrWhiteSpace(reviewer.VerificationHistory.Last()
                .MergedReviewFindings!.Single(finding => finding.StableId == "detached-baseline")
                .EvidenceOutcome?.ReceiptId));

            void CompleteCandidateOnly(ConductorParallelAcceptanceAttempt attempt)
            {
                var candidate = ConductorParallelAcceptanceCandidate.Create(
                    goal, 0, [], candidateSha, mainHeadSha: null);
                ConductorParallelAcceptanceAttemptCoordinator.RunPreReviewEvidenceAttempt(
                    coordinator, attempt, candidate, ConductorAutonomyPolicy.Permissive,
                    (attemptCandidate, request, _, _, _) =>
                    {
                        var red = CandidateRedFindingEvidence(
                            request, candidateSha, "OutsideTests.FailingMethod(passed: True)");
                        return ConductorParallelAcceptanceRunResult.Focused(attemptCandidate,
                            red with { Arms = red.Arms!.Where(arm => arm.Arm == FindingEvidenceArm.Candidate).ToArray() });
                    });
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
