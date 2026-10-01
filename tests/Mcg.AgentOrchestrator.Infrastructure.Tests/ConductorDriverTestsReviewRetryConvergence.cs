using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;
using System.Text.Json;
using System.Xml.Linq;

using static ConductorDriverTests;

[Xunit.Collection("IsolatedProcessSpawning")]
public sealed class ConductorDriverTestsReviewRetryConvergence
{
    [Xunit.Fact(DisplayName = "ConductorDriver_auto_review_retry_convergence_brief_deduplicates_multi_round_findings")]
    public void ConductorDriverAutoReviewRetryConvergenceBriefDeduplicatesMultiRoundFindings()
    {
        var brief = AutoReviewRetryConvergenceBriefBuilder.BuildConvergenceBrief(
            3,
            AgentRole.Reviewer,
            new TaskId("reviewer-task-0001"),
            "verdict=needs-work",
            AgentRole.Developer,
            "C:\\tmp\\reviewer.out.log",
            [
                "ConductorDriverTests still expects the raw findings passthrough.",
                "Tester receipt omits ProgressiveReviewGlanceTests.",
                "  ConductorDriverTests   still expects the raw findings passthrough.  ",
                "Reviewer still needs InquiryDispatcherTests coverage."
            ],
            [
                "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ConductorDriverTests.cs",
                "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ProgressiveReviewGlanceTests.cs",
                "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/InquiryDispatcherTests.cs"
            ]);

        Assert.Contains("auto-review-retry round 3 convergence brief", brief);
        Assert.Contains("Existing implementation shape is accepted", brief);
        Assert.Contains("Do NOT rewrite", brief);
        Assert.Contains("Deduplicated residual blockers", brief);
        Assert.Equal(1, CountOccurrences(brief, "- ConductorDriverTests still expects the raw findings passthrough."));
        Assert.Contains("- Tester receipt omits ProgressiveReviewGlanceTests.", brief);
        Assert.Contains("- Reviewer still needs InquiryDispatcherTests coverage.", brief);
        Assert.Contains("Rerun these focused test classes at your final commit and quote receipts:", brief);
        Assert.Contains("ConductorDriverTests", brief);
        Assert.Contains("ProgressiveReviewGlanceTests", brief);
        Assert.Contains("InquiryDispatcherTests", brief);
        Assert.Contains("C:\\tmp\\reviewer.out.log", brief);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_auto_review_retry_convergence_brief_keeps_single_round_findings")]
    public void ConductorDriverAutoReviewRetryConvergenceBriefKeepsSingleRoundFindings()
    {
        var blocker = "Developer left tester receipt parsing unwired.";
        var brief = AutoReviewRetryConvergenceBriefBuilder.BuildConvergenceBrief(
            1,
            AgentRole.Tester,
            new TaskId("tester-task-0001"),
            "WORKER_RESULT blocker",
            AgentRole.Developer,
            "C:\\tmp\\tester.out.log",
            [blocker],
            []);

        Assert.Contains("auto-review-retry round 1 convergence brief", brief);
        Assert.Contains("Existing implementation shape is accepted", brief);
        Assert.Equal(1, CountOccurrences(brief, $"- {blocker}"));
        Assert.Contains("Rerun the focused test classes covering your changed files at your final commit and quote receipts.", brief);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_auto_review_retry_convergence_brief_keeps_all_unique_findings")]
    public void ConductorDriverAutoReviewRetryConvergenceBriefKeepsAllUniqueFindings()
    {
        var brief = AutoReviewRetryConvergenceBriefBuilder.BuildConvergenceBrief(
            2,
            AgentRole.Reviewer,
            new TaskId("reviewer-task-0001"),
            "verdict=needs-work",
            AgentRole.Developer,
            "reviewer output",
            [
                "First blocker remains open.",
                "Second blocker remains open.",
                "Third blocker remains open."
            ],
            ["tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ConductorDriverTests.cs"]);

        Assert.Equal(1, CountOccurrences(brief, "- First blocker remains open."));
        Assert.Equal(1, CountOccurrences(brief, "- Second blocker remains open."));
        Assert.Equal(1, CountOccurrences(brief, "- Third blocker remains open."));
        Assert.Contains("ConductorDriverTests", brief);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_reviewer_retry_convergence_brief_uses_latest_stable_finding_state")]
    public void ConductorDriverReviewerRetryConvergenceBriefUsesLatestStableFindingState()
    {
        var (kernel, goal) = SoftwareGoal();
        var developer = goal.Tasks.Single(t => t.RequiredRole == AgentRole.Developer);
        var tester = goal.Tasks.Single(t => t.RequiredRole == AgentRole.Tester);
        var reviewer = goal.Tasks.Single(t => t.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(t => t.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task);
        }

        FailReviewerNeedsWork(kernel, goal, reviewer, "Shared blocker stays open.");
        kernel.RetryTask(goal.Id, developer.Id, "auto-review-retry round 1 convergence brief: prior retry");
        PassVerification(kernel, goal, developer);
        PassVerification(kernel, goal, tester);
        FailReviewerNeedsWork(kernel, goal, reviewer, "Shared blocker remains open after focused recheck.");
        string? retryMessage = null;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            dispatchAndStart: _ => DispatchStartOutcome.Started(),
            retryTaskWithRoundKind: (gid, tid, msg, roundKind) =>
            {
                retryMessage = msg;
                return kernel.RetryTask(gid, tid, msg, retryRoundKind: roundKind);
            });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Contains("auto-review-retry round 2 convergence brief", retryMessage);
        Assert.Contains("## RESIDUAL_OPEN_ACTION_ITEMS", retryMessage);
        Assert.Equal(1, CountOccurrences(retryMessage!, "- stable_id: finding-1"));
        Assert.Contains("Shared blocker remains open after focused recheck.", retryMessage);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Executed);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_reviewer_needs_work_auto_retries_developer_with_convergence_brief")]
    public void ConductorDriverReviewerNeedsWorkAutoRetriesDeveloperWithConvergenceBrief()
    {
        var (kernel, goal) = SoftwareGoal();
        var developer = goal.Tasks.Single(t => t.RequiredRole == AgentRole.Developer);
        var tester = goal.Tasks.Single(t => t.RequiredRole == AgentRole.Tester);
        var reviewer = goal.Tasks.Single(t => t.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(t => t.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task);
        }

        var blocker = "Developer omitted retry receipt injection in TaskBriefs.";
        FailReviewerNeedsWork(kernel, goal, reviewer, blocker);
        var dispatched = false;
        var escalated = false;
        string? retryMessage = null;
        RetryRoundKind? retryRoundKind = null;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            dispatchAndStart: _ => { dispatched = true; return DispatchStartOutcome.Started(); },
            retryTaskWithRoundKind: (gid, tid, msg, roundKind) =>
            {
                retryMessage = msg;
                retryRoundKind = roundKind;
                return kernel.RetryTask(gid, tid, msg, retryRoundKind: roundKind);
            },
            writeEscalation: (_, _, _) => { escalated = true; });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.True(dispatched);
        Assert.False(escalated);
        Assert.Equal(WorkTaskStatus.Assigned, developer.Status);
        Assert.Equal(WorkTaskStatus.Assigned, tester.Status);
        Assert.Equal(WorkTaskStatus.Assigned, reviewer.Status);
        Assert.Contains("auto-review-retry round 1 convergence brief", retryMessage);
        Assert.Contains("Existing implementation shape is accepted", retryMessage);
        Assert.DoesNotContain("retry upstream Developer task with findings:", retryMessage);
        Assert.Contains(blocker, retryMessage);
        Assert.Contains("Rerun", retryMessage);
        Assert.Contains("C:\\tmp\\reviewer.out.log", retryMessage);
        Assert.Null(retryRoundKind);
        Assert.Null(developer.PendingRetryRoundKind);
        Assert.Contains(goal.Timeline, evt =>
            evt.TaskId == developer.Id &&
            evt.Kind == ProgressKind.TaskRetried &&
            evt.Message.Contains("auto-review-retry", StringComparison.OrdinalIgnoreCase));
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Executed);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_reviewer_identity_violation_salvages_needs_work_for_developer")]
    public void ConductorDriverReviewerIdentityViolationSalvagesNeedsWorkForDeveloper()
    {
        var (kernel, goal) = SoftwareGoal();
        var developer = goal.Tasks.Single(t => t.RequiredRole == AgentRole.Developer);
        var reviewer = goal.Tasks.Single(t => t.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(t => t.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task);
        }

        var priorLocation = new ReviewFindingLocation("src/A.cs", "A.Run", "guard");
        DispatchTask(kernel, goal, reviewer, "review-1");
        kernel.RecordDispatchBaseCommit(goal.Id, reviewer.Id, "prior-commit");
        FailStructuredReviewerRound(
            kernel,
            goal,
            reviewer,
            "review-1",
            [new ReviewFinding("F-CARRIED", ReviewFindingState.Open, priorLocation, "Carried blocker remains.")]);

        kernel.RetryTask(goal.Id, reviewer.Id, "recheck");
        DispatchTask(kernel, goal, reviewer, "review-2");
        kernel.RecordDispatchBaseCommit(goal.Id, reviewer.Id, "current-commit");
        FailStructuredReviewerRound(
            kernel,
            goal,
            reviewer,
            "review-2",
            [
                new ReviewFinding(
                    "F-CARRIED",
                    ReviewFindingState.Open,
                    new ReviewFindingLocation("src/Moved.cs", "Moved.Run", "guard"),
                    "Carried blocker remains."),
                new ReviewFinding(
                    "F-NEW",
                    ReviewFindingState.Open,
                    new ReviewFindingLocation("src/New.cs", "New.Run"),
                    "New blocker also remains.")
            ]);

        Assert.Equal(WorkTaskStatus.Failed, reviewer.Status);
        Assert.Equal(
            ReviewFindingConvergence.IdentityMovedViolationCode,
            reviewer.LastVerification!.ReviewFindingContractViolation?.Code);
        Assert.Equal(priorLocation, reviewer.LastVerification.MergedReviewFindings!.Single(item => item.StableId == "F-CARRIED").Location);

        TaskId? retriedTaskId = null;
        string? retryMessage = null;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            dispatchAndStart: _ => DispatchStartOutcome.Started(),
            retryTaskWithRoundKind: (gid, tid, message, roundKind) =>
            {
                retriedTaskId = tid;
                retryMessage = message;
                return kernel.RetryTask(gid, tid, message, retryRoundKind: roundKind);
            });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(developer.Id, retriedTaskId);
        Assert.Contains("F-CARRIED", retryMessage, StringComparison.Ordinal);
        Assert.Contains("F-NEW", retryMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("contract-repair", retryMessage, StringComparison.OrdinalIgnoreCase);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Executed);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_structured_open_findings_retry_when_blockers_is_none")]
    public void ConductorDriverStructuredOpenFindingsRetryWhenBlockersIsNone()
    {
        var (kernel, goal) = SoftwareGoal();
        var developer = goal.Tasks.Single(t => t.RequiredRole == AgentRole.Developer);
        var reviewer = goal.Tasks.Single(t => t.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(t => t.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task);
        }

        DispatchTask(kernel, goal, reviewer, "review");
        var stdout = string.Join(
            Environment.NewLine,
            "WORKER_RESULT:",
            "files: none",
            "commands: review",
            "tests: pass - inspected evidence",
            "blockers: none",
            """findings: [{"stable_id":"F-OPEN","state":"open","location":{"file":"src/Test.cs","region":"Test.Run","hunk":"guard"},"description":"Developer must restore the guard."}]""",
            "touched_anchors: []",
            "verdict: needs-work",
            "END_WORKER_RESULT");
        kernel.RecordDispatchExecutionResult(goal.Id, reviewer.Id, new TaskVerificationRecord(
            "review",
            "C:\\tmp",
            0,
            stdout,
            "",
            DateTimeOffset.UtcNow,
            StandardOutputPath: "C:\\tmp\\reviewer.out.log",
            WorkerResultPresent: true));
        Assert.Equal(WorkTaskStatus.Failed, reviewer.Status);

        string? retryMessage = null;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            retryTask: (gid, tid, msg) =>
            {
                retryMessage = msg;
                return kernel.RetryTask(gid, tid, msg);
            });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(WorkTaskStatus.Assigned, developer.Status);
        Assert.Contains("- stable_id: F-OPEN", retryMessage);
        Assert.Contains("Developer must restore the guard.", retryMessage);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Executed);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_unparseable_typed_evidence_finding_reports_typed_refusal")]
    public void ConductorDriverUnparseableTypedEvidenceFindingReportsTypedRefusal()
    {
        var (kernel, goal) = SoftwareGoal();
        var reviewer = goal.Tasks.Single(t => t.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(t => t.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task);
        }

        DispatchTask(kernel, goal, reviewer, "review");
        var stdout = string.Join(
            Environment.NewLine,
            "WORKER_RESULT:",
            "files: none",
            "commands: review",
            "tests: pass - inspected evidence",
            "blockers: focused receipt is missing",
            """findings: [{"stable_id":"F-TEST","state":"open","severity":"blocking","category":"test-evidence","location":{"file":"tests/Test.cs","region":"Test.Run"},"description":"Focused receipt is missing.","evidence_request":{"selections":[]}}]""",
            "touched_anchors: []",
            "verdict: needs-work",
            "END_WORKER_RESULT");
        kernel.RecordDispatchExecutionResult(goal.Id, reviewer.Id, new TaskVerificationRecord(
            "review",
            "C:\\tmp",
            0,
            stdout,
            "",
            DateTimeOffset.UtcNow,
            WorkerResultPresent: true));
        TaskId? retriedTask = null;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            retryTask: (goalId, taskId, message) =>
            {
                retriedTask = taskId;
                return kernel.RetryTask(goalId, taskId, message);
            },
            recordFindingEvidenceRequest: (goalId, taskId, message) =>
                kernel.RecordFindingEvidenceRequest(goalId, taskId, message),
            recordFindingEvidenceOutcome: (goalId, taskId, stableId, outcome, receipt) =>
                kernel.RecordFindingEvidenceOutcome(goalId, taskId, stableId, outcome, receipt));

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(reviewer.Id, retriedTask);
        Assert.Contains(goal.Timeline, evt =>
            evt.Kind == ProgressKind.FindingEvidenceRequestRecorded &&
            evt.Message.Contains("role=Reviewer", StringComparison.Ordinal) &&
            evt.Message.Contains("reason=unparseable-selection", StringComparison.Ordinal));
        Assert.Equal(
            FindingEvidenceNotHonouredReason.UnparseableSelection,
            reviewer.VerificationHistory.Last().MergedReviewFindings?.Single().EvidenceOutcome?.Reason);
        var retryBrief = kernel.BuildTaskBrief(goal.Id, reviewer.Id).Content;
        Assert.Contains("verdict=not-honoured", retryBrief, StringComparison.Ordinal);
        Assert.Contains("reason=unparseable-selection", retryBrief, StringComparison.Ordinal);
        Assert.Contains("evidence_not_honoured: detail=", retryBrief, StringComparison.Ordinal);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Executed);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_tester_worker_result_blocker_auto_retries_developer_with_convergence_brief")]
    public void ConductorDriverTesterWorkerResultBlockerAutoRetriesDeveloperWithConvergenceBrief()
    {
        var (kernel, goal) = SoftwareGoal();
        var developer = goal.Tasks.Single(t => t.RequiredRole == AgentRole.Developer);
        var tester = goal.Tasks.Single(t => t.RequiredRole == AgentRole.Tester);
        PassVerification(kernel, goal, developer, hasCommittedChanges: true);

        var blocker = "Developer left the retry feedback path unwired.";
        FailTesterBlocker(kernel, goal, tester, blocker);
        var dispatched = false;
        var escalated = false;
        TaskId? retriedTaskId = null;
        string? retryMessage = null;
        RetryRoundKind? retryRoundKind = null;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            dispatchAndStart: _ => { dispatched = true; return DispatchStartOutcome.Started(); },
            retryTaskWithRoundKind: (gid, tid, msg, roundKind) =>
            {
                retriedTaskId = tid;
                retryMessage = msg;
                retryRoundKind = roundKind;
                return kernel.RetryTask(gid, tid, msg, retryRoundKind: roundKind);
            },
            writeEscalation: (_, _, _) => { escalated = true; });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.True(dispatched);
        Assert.False(escalated);
        Assert.Equal(developer.Id, retriedTaskId);
        Assert.Equal(WorkTaskStatus.Assigned, developer.Status);
        Assert.Equal(WorkTaskStatus.Assigned, tester.Status);
        Assert.Contains("auto-review-retry round 1 convergence brief", retryMessage);
        Assert.Contains("Tester task", retryMessage);
        Assert.Contains("WORKER_RESULT blocker", retryMessage);
        Assert.Contains("Existing implementation shape is accepted", retryMessage);
        Assert.DoesNotContain("retry upstream Developer task with findings:", retryMessage);
        Assert.Contains(blocker, retryMessage);
        Assert.Contains("Rerun", retryMessage);
        Assert.Contains("C:\\tmp\\tester.out.log", retryMessage);
        Assert.Null(retryRoundKind);
        Assert.Null(developer.PendingRetryRoundKind);
        Assert.Contains(goal.Timeline, evt =>
            evt.TaskId == developer.Id &&
            evt.Kind == ProgressKind.TaskRetried &&
            evt.Message.Contains("auto-review-retry", StringComparison.OrdinalIgnoreCase));
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Executed);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_tester_identity_violation_salvages_failure_for_developer")]
    public void ConductorDriverTesterIdentityViolationSalvagesFailureForDeveloper()
    {
        var (kernel, goal) = SoftwareGoal();
        var developer = goal.Tasks.Single(t => t.RequiredRole == AgentRole.Developer);
        var tester = goal.Tasks.Single(t => t.RequiredRole == AgentRole.Tester);
        PassVerification(kernel, goal, developer, hasCommittedChanges: true);

        var priorLocation = new ReviewFindingLocation("tests/A.cs", "A.Tests", "guard");
        DispatchTask(kernel, goal, tester, "test-1");
        kernel.RecordDispatchBaseCommit(goal.Id, tester.Id, "prior-commit");
        kernel.RecordDispatchExecutionResult(goal.Id, tester.Id, new TaskVerificationRecord(
            "test-1",
            "C:\\tmp",
            0,
            ReviewerPassWithFinding(
                new ReviewFinding(
                    "T-1",
                    ReviewFindingState.Open,
                    priorLocation,
                    "Residual behavior test still fails.")),
            string.Empty,
            DateTimeOffset.UtcNow,
            WorkerResultPresent: true));

        kernel.RetryTask(goal.Id, tester.Id, "recheck");
        DispatchTask(kernel, goal, tester, "test-2");
        kernel.RecordDispatchBaseCommit(goal.Id, tester.Id, "current-commit");
        var blocker = "Residual behavior test still fails.";
        var moved = new ReviewFinding(
            "T-1",
            ReviewFindingState.Open,
            new ReviewFindingLocation("tests/B.cs", "B.Tests", "guard"),
            blocker);
        var stdout = string.Join(
            Environment.NewLine,
            "WORKER_RESULT:",
            "files: none",
            "commands: focused test",
            "tests: fail - one residual failure",
            "commit: none",
            $"blockers: {blocker}",
            $"findings: {JsonSerializer.Serialize(new[] { moved })}",
            "touched_anchors: []",
            "verdict: needs-work",
            "model_fit: fixture/model - adequate - focused test",
            "skills: none",
            "confidence: high",
            "END_WORKER_RESULT");
        kernel.RecordDispatchExecutionResult(goal.Id, tester.Id, new TaskVerificationRecord(
            "test-2",
            "C:\\tmp",
            1,
            stdout,
            string.Empty,
            DateTimeOffset.UtcNow,
            StandardOutputPath: "C:\\tmp\\tester.out.log",
            WorkerResultPresent: true));

        Assert.Equal(WorkTaskStatus.Failed, tester.Status);
        Assert.Equal(
            ReviewFindingConvergence.IdentityMovedViolationCode,
            tester.LastVerification!.ReviewFindingContractViolation?.Code);
        Assert.Equal(priorLocation, Assert.Single(tester.LastVerification.MergedReviewFindings!).Location);
        Assert.True(WorkerResultBlockers.TryGetTestsStatus(tester.LastVerification, out var testsStatus));
        Assert.Equal(WorkerResultBlockers.TestsStatus.Fail, testsStatus);
        Assert.True(WorkerResultBlockers.TryFindHardFailureBlocker(tester.LastVerification, out _));

        TaskId? retriedTaskId = null;
        string? retryMessage = null;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            dispatchAndStart: _ => DispatchStartOutcome.Started(),
            retryTaskWithRoundKind: (gid, tid, message, roundKind) =>
            {
                retriedTaskId = tid;
                retryMessage = message;
                return kernel.RetryTask(gid, tid, message, retryRoundKind: roundKind);
            });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.True(
            developer.Id == retriedTaskId,
            $"Expected Developer retry but got {retriedTaskId?.Value ?? "none"}; outcome={result.Outcome}.");
        Assert.Contains(blocker, retryMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("contract-repair", retryMessage, StringComparison.OrdinalIgnoreCase);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Executed);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_tester_worker_result_blocker_uses_shared_auto_review_retry_cap")]
    public void ConductorDriverTesterWorkerResultBlockerUsesSharedAutoReviewRetryCap()
    {
        var (kernel, goal) = SoftwareGoal();
        var developer = goal.Tasks.Single(t => t.RequiredRole == AgentRole.Developer);
        var tester = goal.Tasks.Single(t => t.RequiredRole == AgentRole.Tester);
        for (var i = 1; i <= 6; i++)
        {
            kernel.RetryTask(goal.Id, developer.Id, $"auto-review-retry round {i}: prior verifying-role finding");
            PassVerification(kernel, goal, developer, hasCommittedChanges: true);
        }

        FailTesterBlocker(kernel, goal, tester, "Developer still misses the tester correctness blocker.");
        var retried = false;
        var dispatched = false;
        string? escalation = null;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            dispatchAndStart: _ => { dispatched = true; return DispatchStartOutcome.Started(); },
            retryTask: (gid, tid, msg) => { retried = true; return kernel.RetryTask(gid, tid, msg); },
            writeEscalation: (_, _, message) => { escalation = message; });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.False(retried);
        Assert.False(dispatched);
        Assert.Contains("auto-review-retry stopped at review round 7/7", escalation);
        Assert.Contains("operator decision required", escalation);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Escalated);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_tester_empty_output_flake_auto_recovers_without_developer_retry")]
    public void ConductorDriverTesterEmptyOutputFlakeAutoRecoversWithoutDeveloperRetry()
    {
        var (kernel, goal) = SoftwareGoal();
        var developer = goal.Tasks.Single(t => t.RequiredRole == AgentRole.Developer);
        var tester = goal.Tasks.Single(t => t.RequiredRole == AgentRole.Tester);
        PassVerification(kernel, goal, developer, hasCommittedChanges: true);
        DispatchTask(kernel, goal, tester, "test");
        kernel.RecordDispatchExecutionResult(goal.Id, tester.Id,
            new TaskVerificationRecord("test", "C:\\tmp", 1, "", "", DateTimeOffset.UtcNow));

        TaskId? retriedTaskId = null;
        var escalated = false;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            dispatchAndStart: _ => DispatchStartOutcome.Started(),
            retryTaskWithRoundKind: (gid, tid, msg, roundKind) =>
            {
                retriedTaskId = tid;
                return kernel.RetryTask(gid, tid, msg, retryRoundKind: roundKind);
            },
            writeEscalation: (_, _, _) => { escalated = true; });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.False(escalated);
        Assert.Equal(tester.Id, retriedTaskId);
        Assert.NotEqual(developer.Id, retriedTaskId);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Executed);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_Tester_inconclusive_retries_same_task_and_preserves_upstream_commit")]
    public void ConductorDriverTesterInconclusiveRetriesSameTaskAndPreservesUpstreamCommit()
    {
        var (kernel, goal) = SoftwareGoal();
        var developer = goal.Tasks.Single(t => t.RequiredRole == AgentRole.Developer);
        var tester = goal.Tasks.Single(t => t.RequiredRole == AgentRole.Tester);
        var reviewer = goal.Tasks.Single(t => t.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks)
        {
            PassVerification(kernel, goal, task, hasCommittedChanges: task == developer);
        }

        kernel.RecordDispatchBaseCommit(goal.Id, developer.Id, "base1234");
        kernel.RecordDispatchResultCommit(goal.Id, developer.Id, "result5678");
        var developerVerification = developer.LastVerification;
        var developerDispatch = developer.LastDispatch;
        kernel.RetryTask(goal.Id, tester.Id, "simulate environmental re-verification");
        DispatchTask(kernel, goal, tester, "test-inconclusive");
        var stdout = string.Join(
            Environment.NewLine,
            "WORKER_RESULT:",
            "files: none",
            "commands: dotnet test --no-build --filter Focused",
            "tests: inconclusive - command timed out; no TRX",
            "commit: none",
            "blockers: stale product blocker from prior round",
            "model_fit: fixture/model - adequate - tester retry",
            "skills: dotnet-windows-build-hygiene",
            "confidence: medium",
            "END_WORKER_RESULT");
        kernel.RecordDispatchExecutionResult(
            goal.Id,
            tester.Id,
            new TaskVerificationRecord(
                "test-inconclusive",
                "C:\\tmp",
                0,
                stdout,
                "",
                DateTimeOffset.UtcNow,
                WorkerResultPresent: true));

        TaskId? retriedTaskId = null;
        string? retryMessage = null;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            dispatchAndStart: _ => DispatchStartOutcome.Started(),
            retryTaskWithRoundKind: (gid, tid, message, roundKind) =>
            {
                retriedTaskId = tid;
                retryMessage = message;
                return kernel.RetryTask(gid, tid, message, retryRoundKind: roundKind);
            });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(tester.Id, retriedTaskId);
        Assert.NotEqual(developer.Id, retriedTaskId);
        Assert.Contains("verification-inconclusive Tester task", retryMessage, StringComparison.Ordinal);
        Assert.Contains("schema conflict retained for audit", retryMessage, StringComparison.Ordinal);
        Assert.Equal(WorkTaskStatus.Assigned, tester.Status);
        Assert.Equal(WorkTaskStatus.Assigned, reviewer.Status);
        Assert.Equal(WorkTaskStatus.Completed, developer.Status);
        Assert.Same(developerVerification, developer.LastVerification);
        Assert.Same(developerDispatch, developer.LastDispatch);
        Assert.Equal("result5678", developer.LastDispatch!.ResultCommit);
        Assert.Equal(2, tester.VerificationHistory.Count);
        Assert.DoesNotContain(goal.Timeline, evt =>
            evt.TaskId == developer.Id && evt.Kind == ProgressKind.TaskRetried);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Executed);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_Tester_inconclusive_budget_exhaustion_escalates_with_latest_receipt")]
    public void ConductorDriverTesterInconclusiveBudgetExhaustionEscalatesWithLatestReceipt()
    {
        var (kernel, goal) = SoftwareGoal();
        var developer = goal.Tasks.Single(t => t.RequiredRole == AgentRole.Developer);
        var tester = goal.Tasks.Single(t => t.RequiredRole == AgentRole.Tester);
        foreach (var task in goal.Tasks.TakeWhile(task => task.RequiredRole != AgentRole.Tester))
        {
            PassVerification(kernel, goal, task, hasCommittedChanges: task == developer);
        }

        var policy = ConductorAutonomyPolicy.Permissive with
        {
            MaxEmptyOutputDispatchRetries = 1,
            MaxEmptyOutputAutoRecoverCycles = 1
        };
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            dispatchAndStart: _ => DispatchStartOutcome.Started(),
            retryTaskWithRoundKind: (gid, tid, message, roundKind) =>
                kernel.RetryTask(gid, tid, message, retryRoundKind: roundKind));

        RecordInconclusiveTester(kernel, goal, tester, "round 1 process killed; no TRX");
        var result = driver.AdvanceOnce(goal, policy);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Executed);

        RecordInconclusiveTester(kernel, goal, tester, "round 2 timed out; no results");
        string? escalationMessage = null;
        var escalationDriver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            dispatchAndStart: _ => throw new Xunit.Sdk.XunitException("Developer or Tester dispatch must not start after retry exhaustion."),
            retryTaskWithRoundKind: (_, _, _, _) => throw new Xunit.Sdk.XunitException("Retry budget is exhausted."),
            writeEscalation: (_, _, message) => escalationMessage = message);

        result = escalationDriver.AdvanceOnce(goal, policy);

        Assert.True(result.Outcome is ConductorAdvanceOutcome.Escalated);
        Assert.Contains(tester.Id.Value[..8], escalationMessage, StringComparison.Ordinal);
        Assert.Contains("round 2 timed out; no results", escalationMessage, StringComparison.Ordinal);
        Assert.Equal(2, tester.VerificationHistory.Count);
        Assert.Equal(WorkTaskStatus.Completed, developer.Status);
        Assert.DoesNotContain(goal.Timeline, evt =>
            evt.TaskId == developer.Id && evt.Kind == ProgressKind.TaskRetried);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_premise_invalid_planner_waits_for_operator_before_Developer_dispatch")]
    public void ConductorDriverPremiseInvalidPlannerWaitsForOperatorBeforeDeveloperDispatch()
    {
        var (kernel, goal) = SoftwareGoal();
        var planner = goal.Tasks.Single(t => t.RequiredRole == AgentRole.Planner);
        var developer = goal.Tasks.Single(t => t.RequiredRole == AgentRole.Developer);
        DispatchTask(kernel, goal, planner, "plan");
        var stdout = string.Join(
            Environment.NewLine,
            "WORKER_RESULT:",
            "files: none",
            "commands: inspect src/Api.cs",
            "tests: not-run - repository inspection only",
            "commit: none",
            "blockers: premise-invalid - required API does not exist; src/Api.cs proves replacement semantics",
            "model_fit: fixture/model - adequate - planner inspection",
            "skills: none",
            "confidence: high",
            "END_WORKER_RESULT");
        kernel.RecordDispatchExecutionResult(
            goal.Id,
            planner.Id,
            new TaskVerificationRecord(
                "plan",
                "C:\\tmp",
                0,
                stdout,
                "",
                DateTimeOffset.UtcNow,
                WorkerResultPresent: true));

        var dispatched = false;
        string? escalationMessage = null;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            dispatchAndStart: _ =>
            {
                dispatched = true;
                return DispatchStartOutcome.Started();
            },
            writeEscalation: (_, _, message) => escalationMessage = message);

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.False(dispatched);
        Assert.Null(developer.LastDispatch);
        Assert.Equal(WorkTaskStatus.WaitingForHuman, planner.Status);
        Assert.Single(kernel.GetPendingHumanInput(goal.Id));
        Assert.Contains("AwaitingHumanInput", escalationMessage, StringComparison.Ordinal);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Escalated);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_auto_review_retry_drops_superseded_findings_before_retry_feedback")]
    public void ConductorDriverAutoReviewRetryDropsSupersededFindingsBeforeRetryFeedback()
    {
        var (kernel, goal) = SoftwareGoal();
        var developer = goal.Tasks.Single(t => t.RequiredRole == AgentRole.Developer);
        var reviewer = goal.Tasks.Single(t => t.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(t => t.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task);
        }

        kernel.RecordTaskNote(
            goal.Id,
            reviewer.Id,
            "CRITERIA CORRECTION: supersedes=\"full Infrastructure suite before review\"; correction=\"focused build-check evidence is sufficient\"");
        FailReviewerNeedsWork(
            kernel,
            goal,
            reviewer,
            "missing full Infrastructure suite before review; Developer omitted retry receipt injection in TaskBriefs.");
        string? retryMessage = null;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            dispatchAndStart: _ => DispatchStartOutcome.Started(),
            retryTaskWithRoundKind: (gid, tid, msg, roundKind) =>
            {
                retryMessage = msg;
                return kernel.RetryTask(gid, tid, msg, retryRoundKind: roundKind);
            },
            recordTaskNote: (gid, tid, message) => kernel.RecordTaskNote(gid, tid, message));

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(WorkTaskStatus.Assigned, developer.Status);
        Assert.Contains("Developer omitted retry receipt injection in TaskBriefs", retryMessage);
        Assert.DoesNotContain("missing full Infrastructure suite before review", retryMessage);
        Assert.Contains(goal.Timeline, evt =>
            evt.TaskId == reviewer.Id &&
            evt.Kind == ProgressKind.TaskNote &&
            evt.Message.Contains("Suppressed auto-review-retry finding", StringComparison.Ordinal) &&
            evt.Message.Contains("missing full Infrastructure suite before review", StringComparison.Ordinal));
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Executed);
    }

}
