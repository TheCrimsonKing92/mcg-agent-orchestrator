using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using System.Text.Json;

using static ConductorDriverTests;

[Xunit.Collection("IsolatedProcessSpawning")]
public sealed class ConductorDriverTestsTesterFindingRetry
{
    [Xunit.Theory]
    [Xunit.InlineData(FindingCategory.Correctness)]
    [Xunit.InlineData(FindingCategory.TestCoverage)]
    [Xunit.InlineData(FindingCategory.SpecCompliance)]
    [Xunit.InlineData(FindingCategory.CodeQuality)]
    public void DeveloperOwnedTesterFindingPreemptsItsEvidenceRequest(FindingCategory category)
    {
        var (kernel, goal) = SoftwareGoal();
        var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
        var tester = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
        PassVerification(kernel, goal, developer, hasCommittedChanges: true);
        RecordTesterFinding(kernel, goal, tester, category, includeEvidenceRequest: true);

        TaskId? retriedTaskId = null;
        var focusedRuns = 0;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            getPreReviewEvidenceContext: _ => NoPreReviewContext("abc1234"),
            runFocusedEvidence: (_, request) =>
            {
                focusedRuns++;
                return DualArmFindingEvidence(request, FindingEvidenceArmDisposition.Red, "abc1234");
            },
            dispatchAndStart: _ => DispatchStartOutcome.Started(),
            retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
            {
                retriedTaskId = taskId;
                return kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind);
            });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(0, focusedRuns);
        Assert.Equal(developer.Id, retriedTaskId);
        Assert.NotEqual(tester.Id, retriedTaskId);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Executed);
    }

    [Xunit.Fact]
    public void DeveloperOwnedTesterFindingReopensDeveloperAfterItsLatestRetryProducedNoCommit()
    {
        var (kernel, goal) = SoftwareGoal();
        var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
        var tester = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
        PassVerification(kernel, goal, developer, hasCommittedChanges: true);
        kernel.RetryTask(goal.Id, developer.Id, "A blocking source finding needs a correction.");
        PassVerification(kernel, goal, developer, hasCommittedChanges: false);
        RecordTesterFinding(kernel, goal, tester, FindingCategory.Correctness, includeEvidenceRequest: true);

        TaskId? retriedTaskId = null;
        var focusedRuns = 0;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            getPreReviewEvidenceContext: _ => NoPreReviewContext("abc1234"),
            runFocusedEvidence: (_, request) =>
            {
                focusedRuns++;
                return DualArmFindingEvidence(request, FindingEvidenceArmDisposition.Red, "abc1234");
            },
            dispatchAndStart: _ => DispatchStartOutcome.Started(),
            retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
            {
                retriedTaskId = taskId;
                return kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind);
            });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(0, focusedRuns);
        Assert.Equal(developer.Id, retriedTaskId);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Executed);
    }

    [Xunit.Fact]
    public void PendingDeveloperRetryPreventsCombinedTesterReviewerEvidenceRepeatAndDispatchesDeveloper()
    {
        const string candidateSha = "abc1234";
        var (kernel, goal) = SoftwareGoal();
        var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
        var tester = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        PassVerification(kernel, goal, developer, hasCommittedChanges: true);
        kernel.RetryTask(
            goal.Id,
            developer.Id,
            "The operator supplied an upstream source correction.",
            retryCause: RetryCause.NewSourceFinding);
        kernel.RecordPreReviewEvidence(goal.Id, reviewer.Id, new PreReviewEvidenceReceipt(
            goal.Id.Value,
            ReviewerRound: 1,
            CandidateSha: candidateSha,
            SelectedFocusedTests: ["Infrastructure.Tests: ConductorDriverTests"],
            Disposition: PreReviewEvidenceDisposition.Green,
            PassedCheckCount: 1,
            FailedCheckCount: 0,
            Checks: [new PreReviewEvidenceCheckReceipt(
                "ConductorDriverTests",
                "Infrastructure.Tests: ConductorDriverTests",
                Passed: true,
                ExitCode: 0)],
            FailingTestIdentities: [],
            MappingReason: "fixture green evidence",
            EvidencePointer: "fixture://green",
            RecordedAt: DateTimeOffset.UtcNow));
        RecordTesterFinding(kernel, goal, tester, FindingCategory.Correctness, includeEvidenceRequest: true);
        FailReviewerNeedsWork(
            kernel,
            goal,
            reviewer,
            "reviewer source finding",
            findings: [EvidenceFindingWithRequest(
                "The unchanged candidate still has a blocking source defect.",
                id: "combined-reviewer-correctness",
                category: FindingCategory.Correctness)]);

        var focusedRuns = 0;
        IReadOnlyList<DispatchedTaskIdentity>? dispatched = null;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            getPreReviewEvidenceContext: _ => NoPreReviewContext(candidateSha),
            runFocusedEvidence: (_, request) =>
            {
                focusedRuns++;
                return DualArmFindingEvidence(request, FindingEvidenceArmDisposition.Red, candidateSha);
            },
            dispatchAndStart: dispatchGoal =>
            {
                var started = new[] { dispatchGoal.Tasks.Single(task => task.Id == developer.Id) };
                var outcome = DispatchStartOutcome.Started(started);
                dispatched = outcome.DispatchedTasks;
                return outcome;
            },
            retryTaskWithCause: (goalId, taskId, message, roundKind, cause) =>
                kernel.RetryTask(goalId, taskId, message, cause, retryRoundKind: roundKind));

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(0, focusedRuns);
        Assert.Single(reviewer.PreReviewEvidenceHistory);
        Assert.NotNull(dispatched);
        Assert.Equal(developer.Id, Assert.Single(dispatched!).TaskId);
        Assert.Equal(AgentRole.Developer, Assert.Single(dispatched!).Role);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Executed);
    }

    [Xunit.Fact]
    public void TestEvidenceTesterFindingUsesEvidencePath()
    {
        var (kernel, goal) = SoftwareGoal();
        var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
        var tester = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
        PassVerification(kernel, goal, developer, hasCommittedChanges: true);
        RecordTesterFinding(kernel, goal, tester, FindingCategory.TestEvidence, includeEvidenceRequest: true);

        TaskId? retriedTaskId = null;
        var focusedRuns = 0;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            getPreReviewEvidenceContext: _ => NoPreReviewContext("abc1234"),
            runFocusedEvidence: (_, request) =>
            {
                focusedRuns++;
                return DualArmFindingEvidence(request, FindingEvidenceArmDisposition.Red, "abc1234");
            },
            dispatchAndStart: _ => DispatchStartOutcome.Started(),
            retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
            {
                retriedTaskId = taskId;
                return kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind);
            },
            recordFindingEvidenceRequest: (goalId, taskId, message) =>
                kernel.RecordFindingEvidenceRequest(goalId, taskId, message),
            recordFindingEvidenceOutcome: (goalId, taskId, stableId, outcome, receipt) =>
                kernel.RecordFindingEvidenceOutcome(goalId, taskId, stableId, outcome, receipt));

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(1, focusedRuns);
        Assert.Equal(tester.Id, retriedTaskId);
        Assert.NotEqual(developer.Id, retriedTaskId);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Executed);
    }

    [Xunit.Fact]
    public void DeveloperOwnedTesterFindingWithoutCommittedDeveloperEscalates()
    {
        var (kernel, goal) = SoftwareGoal();
        var tester = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
        RecordTesterFinding(kernel, goal, tester, FindingCategory.Correctness, includeEvidenceRequest: true);

        var retried = false;
        string? escalation = null;
        var focusedRuns = 0;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            getPreReviewEvidenceContext: _ => NoPreReviewContext("abc1234"),
            runFocusedEvidence: (_, request) =>
            {
                focusedRuns++;
                return DualArmFindingEvidence(request, FindingEvidenceArmDisposition.Red, "abc1234");
            },
            retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
            {
                retried = true;
                return kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind);
            },
            writeEscalation: (_, _, message) => escalation = message);

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(0, focusedRuns);
        Assert.False(retried);
        Assert.Contains("could not be routed to an upstream Developer task", escalation, StringComparison.Ordinal);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Escalated);
    }

    private static void RecordTesterFinding(
        AgentOrchestratorKernel kernel,
        Goal goal,
        TaskSpec tester,
        FindingCategory category,
        bool includeEvidenceRequest)
    {
        DispatchTask(kernel, goal, tester, "test");
        var finding = new ReviewFinding(
            "T-FINDING",
            ReviewFindingState.Open,
            new ReviewFindingLocation("src/Test.cs", "Test.Run"),
            "The candidate requires a finding-specific response.",
            FindingSeverity.Blocking,
            category,
            includeEvidenceRequest
                ? new FindingEvidenceRequest(
                    [new FindingEvidenceSelection("Infrastructure.Tests", "ConductorDriverTests")])
                : null);
        var stdout = string.Join(
            Environment.NewLine,
            "WORKER_RESULT:",
            "files: none",
            "commands: inspect focused behavior",
            "tests: deferred - acceptance owns the focused execution",
            "commit: none",
            "blockers: none",
            $"findings: {JsonSerializer.Serialize(new[] { finding })}",
            "touched_anchors: []",
            "verdict: needs-work",
            "model_fit: fixture/model - adequate - source verification",
            "skills: none",
            "confidence: high",
            "END_WORKER_RESULT");
        kernel.RecordDispatchExecutionResult(goal.Id, tester.Id, new TaskVerificationRecord(
            "test",
            "C:\\tmp",
            0,
            stdout,
            "",
            DateTimeOffset.UtcNow,
            StandardOutputPath: "C:\\tmp\\tester.out.log",
            WorkerResultPresent: true));
    }
}
