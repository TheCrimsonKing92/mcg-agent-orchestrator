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
public sealed class ConductorDriverTestsContractRepairBounds
{
    private static string CreateTempDirectory() => ConductorDriverTests.CreateTempDirectory();

    [Xunit.Fact(DisplayName = "Equal SHA identity violations salvage the prior ledger and converge normally")]
    public void IdentityViolations_EqualShaProof_SalvagePriorLedgerAndConvergeNormally()
    {
        using var repository = CreateSeededGitRepository();
        var (workingDirectory, unchangedCommit) = repository;
        var (kernel, goal) = SoftwareGoal();
        var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
        var tester = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task);
        }

        DispatchTask(kernel, goal, reviewer, "review-1", workingDirectory: workingDirectory);
        kernel.RecordDispatchBaseCommit(goal.Id, reviewer.Id, unchangedCommit);
        var opened = Enumerable.Range(0, 15)
            .Select(index => new ReviewFinding(
                $"F-{index:D2}",
                ReviewFindingState.Open,
                new ReviewFindingLocation($"src/F{index:D2}.cs", $"F{index:D2}.Run", "guard"),
                $"Blocking finding {index:D2}.",
                FindingSeverity.Blocking))
            .ToArray();
        var firstRound = string.Join(
            Environment.NewLine,
            "WORKER_RESULT:",
            "files: none",
            "commands: review",
            "tests: pass - inspected evidence",
            "blockers: none",
            $"findings: {JsonSerializer.Serialize(opened)}",
            "touched_anchors: []",
            "verdict: pass",
            "END_WORKER_RESULT");
        kernel.RecordDispatchExecutionResult(goal.Id, reviewer.Id, new TaskVerificationRecord(
            "review-1",
            workingDirectory,
            0,
            firstRound,
            "",
            DateTimeOffset.UtcNow,
            WorkerResultPresent: true));
        kernel.RetryTask(goal.Id, reviewer.Id, "fresh review");
        var touchScope = WorkerProfileDispatcher.ReadReviewRoundTouchScope(
            goal,
            reviewer,
            workingDirectory,
            unchangedCommit);
        Assert.Empty(touchScope.TouchedAnchors);
        Assert.Null(touchScope.Diagnostic);
        DispatchTask(
            kernel,
            goal,
            reviewer,
            "review-2",
            touchScope.Diagnostic,
            touchScope.TouchedAnchors,
            workingDirectory);
        kernel.RecordDispatchBaseCommit(goal.Id, reviewer.Id, unchangedCommit);
        var rejected = opened
            .Reverse()
            .Select(finding => finding.StableId == "F-00"
                ? finding with { Location = new ReviewFindingLocation("src/Moved.cs", "Moved.Run", "guard") }
                : finding)
            .Prepend(new ReviewFinding(
                "F-RECYCLED",
                ReviewFindingState.Open,
                opened[2].Location,
                "Recycled anchor.",
                FindingSeverity.Advisory))
            .ToArray();
        var rejectedRound = string.Join(
            Environment.NewLine,
            "WORKER_RESULT:",
            "files: none",
            "commands: review",
            "tests: pass - inspected evidence",
            "blockers: identity contract fixture",
            $"findings: {JsonSerializer.Serialize(rejected)}",
            "touched_anchors: []",
            "verdict: needs-work",
            "END_WORKER_RESULT");
        kernel.RecordDispatchExecutionResult(goal.Id, reviewer.Id, new TaskVerificationRecord(
            "review-2",
            workingDirectory,
            1,
            rejectedRound,
            "",
            DateTimeOffset.UtcNow,
            StandardOutputPath: Path.Combine(workingDirectory, "reviewer.out.log"),
            WorkerResultPresent: true));
        var violation = Assert.IsType<ReviewFindingContractViolation>(
            reviewer.LastVerification!.ReviewFindingContractViolation);
        Assert.Equal(ReviewFindingConvergence.IdentityMovedViolationCode, violation.Code);
        Assert.Equal(2, violation.IdentityMismatches!.Count);
        var salvaged = Assert.IsAssignableFrom<IReadOnlyList<ReviewFinding>>(
            reviewer.LastVerification.MergedReviewFindings);
        Assert.Equal(opened.Length, salvaged.Count);
        Assert.All(opened, finding =>
            Assert.Contains(salvaged, candidate => candidate == finding));

        TaskId? retriedTaskId = null;
        string? retryMessage = null;
        RetryRoundKind? retryRoundKind = null;
        string? escalation = null;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            dispatchAndStart: _ => DispatchStartOutcome.Started(),
            retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
            {
                retriedTaskId = taskId;
                retryMessage = message;
                retryRoundKind = roundKind;
                return kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind);
            },
            writeEscalation: (_, _, message) => escalation = message);

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.True(
            developer.Id == retriedTaskId,
            $"Expected normal Developer convergence retry but got {retriedTaskId?.Value ?? "none"}; " +
            $"outcome={result.Outcome}; escalation={escalation ?? "none"}");
        Assert.Null(retryRoundKind);
        Assert.Equal(WorkTaskStatus.Assigned, developer.Status);
        Assert.Equal(WorkTaskStatus.Assigned, tester.Status);
        Assert.Equal(WorkTaskStatus.Assigned, reviewer.Status);
        Assert.DoesNotContain("contract-repair", retryMessage, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("suppression=missing-system-derived-round-diff-proof", retryMessage, StringComparison.Ordinal);
        Assert.Contains("open_count: 15", retryMessage, StringComparison.Ordinal);
        Assert.All(opened, finding =>
            Assert.Contains($"stable_id: {finding.StableId}", retryMessage, StringComparison.Ordinal));
        Assert.Null(escalation);
        Assert.Empty(goal.Timeline.Where(evt =>
            evt.TaskId == reviewer.Id &&
            evt.Kind == ProgressKind.TaskRetried &&
            evt.Message.StartsWith("review-finding contract-repair:", StringComparison.Ordinal)));
        Assert.Contains(goal.Timeline, evt =>
            evt.TaskId == developer.Id &&
            evt.Kind == ProgressKind.TaskRetried &&
            evt.Message.Contains("auto-review-retry", StringComparison.OrdinalIgnoreCase));
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Executed);
    }

    [Xunit.Fact(DisplayName = "Equal SHA untouched reopen consumes mechanical repair budget")]
    public void UntouchedReopen_ComputedEmptyProof_ConsumesRepairBudget()
    {
        using var repository = CreateSeededGitRepository();
        var (workingDirectory, unchangedCommit) = repository;
        var (kernel, goal) = SoftwareGoal();
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task);
        }

        var anchor = new ReviewFindingLocation("src/A.cs", "A.Run", "guard");
        var open = new ReviewFinding(
            "F-1",
            ReviewFindingState.Open,
            anchor,
            "Missing guard.",
            FindingSeverity.Advisory);
        DispatchTask(kernel, goal, reviewer, "review-open", workingDirectory: workingDirectory);
        kernel.RecordDispatchBaseCommit(goal.Id, reviewer.Id, unchangedCommit);
        kernel.RecordDispatchExecutionResult(goal.Id, reviewer.Id, new TaskVerificationRecord(
            "review-open",
            workingDirectory,
            0,
            ReviewerPassWithFinding(open),
            string.Empty,
            DateTimeOffset.UtcNow,
            WorkerResultPresent: true));

        kernel.RetryTask(goal.Id, reviewer.Id, "confirm resolution");
        var resolvedTouchScope = WorkerProfileDispatcher.ReadReviewRoundTouchScope(
            goal,
            reviewer,
            workingDirectory,
            unchangedCommit);
        Assert.Empty(resolvedTouchScope.TouchedAnchors);
        DispatchTask(
            kernel,
            goal,
            reviewer,
            "review-resolved",
            resolvedTouchScope.Diagnostic,
            resolvedTouchScope.TouchedAnchors,
            workingDirectory);
        kernel.RecordDispatchBaseCommit(goal.Id, reviewer.Id, unchangedCommit);
        kernel.RecordDispatchExecutionResult(goal.Id, reviewer.Id, new TaskVerificationRecord(
            "review-resolved",
            workingDirectory,
            0,
            ReviewerPassWithFinding(open with { State = ReviewFindingState.Resolved }),
            string.Empty,
            DateTimeOffset.UtcNow,
            WorkerResultPresent: true));
        Assert.Null(reviewer.LastVerification!.ReviewFindingContractViolation);

        kernel.RetryTask(goal.Id, reviewer.Id, "recheck unchanged commit");
        var reopenedTouchScope = WorkerProfileDispatcher.ReadReviewRoundTouchScope(
            goal,
            reviewer,
            workingDirectory,
            unchangedCommit);
        Assert.Empty(reopenedTouchScope.TouchedAnchors);
        DispatchTask(
            kernel,
            goal,
            reviewer,
            "review-reopened",
            reopenedTouchScope.Diagnostic,
            reopenedTouchScope.TouchedAnchors,
            workingDirectory);
        kernel.RecordDispatchBaseCommit(goal.Id, reviewer.Id, unchangedCommit);
        kernel.RecordDispatchExecutionResult(goal.Id, reviewer.Id, new TaskVerificationRecord(
            "review-reopened",
            workingDirectory,
            0,
            ReviewerPassWithFinding(open),
            string.Empty,
            DateTimeOffset.UtcNow,
            StandardOutputPath: Path.Combine(workingDirectory, "reviewer.out.log"),
            WorkerResultPresent: true));

        var violation = Assert.IsType<ReviewFindingContractViolation>(
            reviewer.LastVerification!.ReviewFindingContractViolation);
        Assert.Equal(ReviewFindingConvergence.UntouchedReopenViolationCode, violation.Code);

        RetryRoundKind? retryRoundKind = null;
        string? retryMessage = null;
        string? escalation = null;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
            {
                retryRoundKind = roundKind;
                retryMessage = message;
                return kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind);
            },
            writeEscalation: (_, _, message) => escalation = message);

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(RetryRoundKind.Mechanical, retryRoundKind);
        Assert.Equal(RetryRoundKind.Mechanical, reviewer.PendingRetryRoundKind);
        Assert.Contains(ReviewFindingConvergence.UntouchedReopenViolationCode, retryMessage, StringComparison.Ordinal);
        Assert.Null(escalation);
        Assert.Single(goal.Timeline.Where(evt =>
            evt.TaskId == reviewer.Id &&
            evt.Kind == ProgressKind.TaskRetried &&
            evt.Message.StartsWith("review-finding contract-repair:", StringComparison.Ordinal)));
        Assert.IsType<ConductorAdvanceOutcome.Executed>(result.Outcome);
    }

    [Xunit.Fact(DisplayName = "Omitted open Reviewer IDs consume mechanical repair without reopening Developer")]
    public void OmittedOpenReviewerIdsMechanicallyRetryReviewerWithoutDeveloperReopen()
    {
        var (kernel, goal) = SoftwareGoal();
        var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
        var tester = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        PassVerification(kernel, goal, developer);
        PassVerification(kernel, goal, tester);
        var open = new ReviewFinding(
            "F-OMITTED",
            ReviewFindingState.Open,
            new ReviewFindingLocation("src/A.cs", "A.Run", "guard"),
            "Guard remains advisory.",
            FindingSeverity.Advisory);
        DispatchTask(kernel, goal, reviewer, "review-open");
        kernel.RecordDispatchExecutionResult(goal.Id, reviewer.Id, new TaskVerificationRecord(
            "review-open",
            "C:\\tmp",
            0,
            ReviewerPassWithFinding(open),
            string.Empty,
            DateTimeOffset.UtcNow,
            WorkerResultPresent: true));
        Assert.Contains(
            reviewer.LastVerification!.MergedReviewFindings!,
            finding => finding.StableId == open.StableId && finding.State == ReviewFindingState.Open);

        kernel.RetryTask(goal.Id, reviewer.Id, "confirm advisory resolution");
        Assert.Contains(
            reviewer.VerificationHistory,
            verification => verification.MergedReviewFindings?.Any(
                finding => finding.StableId == open.StableId && finding.State == ReviewFindingState.Open) == true);
        DispatchTask(kernel, goal, reviewer, "review-omitted");
        var omittedPass = string.Join(
            Environment.NewLine,
            "WORKER_RESULT:",
            "files: none",
            "commands: review",
            "tests: pass - inspected evidence",
            "blockers: none",
            "findings: []",
            "touched_anchors: []",
            "verdict: pass",
            "END_WORKER_RESULT");
        kernel.RecordDispatchExecutionResult(goal.Id, reviewer.Id, new TaskVerificationRecord(
            "review-omitted",
            "C:\\tmp",
            0,
            omittedPass,
            string.Empty,
            DateTimeOffset.UtcNow,
            StandardOutputPath: "C:\\tmp\\reviewer.out.log",
            WorkerResultPresent: true));

        Assert.True(WorkerResultBlockers.TryFindReviewFindingRound(reviewer.LastVerification, out _, out var parseDiagnostic), parseDiagnostic);
        Assert.True(WorkerResultBlockers.TryFindPassVerdict(reviewer.LastVerification));
        Assert.Contains(
            reviewer.LastVerification!.MergedReviewFindings!,
            finding => finding.StableId == open.StableId && finding.State == ReviewFindingState.Open);
        Assert.Equal(WorkTaskStatus.Failed, reviewer.Status);
        Assert.Equal(
            ReviewFindingConvergence.OmittedOpenFindingViolationCode,
            reviewer.LastVerification!.ReviewFindingContractViolation?.Code);
        TaskId? retriedTaskId = null;
        RetryRoundKind? retryRoundKind = null;
        string? retryMessage = null;
        string? escalation = null;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
            {
                retriedTaskId = taskId;
                retryRoundKind = roundKind;
                retryMessage = message;
                return kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind);
            },
            writeEscalation: (_, _, message) => escalation = message);

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(reviewer.Id, retriedTaskId);
        Assert.Equal(RetryRoundKind.Mechanical, retryRoundKind);
        Assert.Equal(WorkTaskStatus.Completed, developer.Status);
        Assert.Equal(WorkTaskStatus.Completed, tester.Status);
        Assert.Equal(WorkTaskStatus.Assigned, reviewer.Status);
        Assert.Contains(ReviewFindingConvergence.OmittedOpenFindingViolationCode, retryMessage, StringComparison.Ordinal);
        Assert.Contains("avoided_developer_reopen=1", retryMessage, StringComparison.Ordinal);
        Assert.Contains("stable_id: F-OMITTED", retryMessage, StringComparison.Ordinal);
        Assert.Null(escalation);
        Assert.IsType<ConductorAdvanceOutcome.Executed>(result.Outcome);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_tester_contract_violation_mechanically_retries_the_same_tester")]
    public void ConductorDriverTesterContractViolationMechanicallyRetriesSameTester()
    {
        var (kernel, goal) = SoftwareGoal();
        var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
        var tester = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
        PassVerification(kernel, goal, developer);

        DispatchTask(kernel, goal, tester, "test-1");
        kernel.RecordDispatchExecutionResult(goal.Id, tester.Id, new TaskVerificationRecord(
            "test-1",
            "C:\\tmp",
            0,
            ReviewerPassWithAdvisory(
                "T-1",
                new ReviewFindingLocation("tests/A.cs", "A.Tests", "guard")),
            "",
            DateTimeOffset.UtcNow,
            WorkerResultPresent: true));
        kernel.RetryTask(goal.Id, tester.Id, "recheck");
        DispatchTask(kernel, goal, tester, "test-2");
        kernel.RecordDispatchExecutionResult(goal.Id, tester.Id, new TaskVerificationRecord(
            "test-2",
            "C:\\tmp",
            0,
            ReviewerPassWithAdvisory(
                "T-1",
                new ReviewFindingLocation("tests/B.cs", "B.Tests", "guard")),
            "",
            DateTimeOffset.UtcNow,
            StandardOutputPath: "C:\\tmp\\tester.out.log",
            WorkerResultPresent: true));

        Assert.Equal(WorkTaskStatus.Failed, tester.Status);
        Assert.NotNull(tester.LastVerification!.ReviewFindingContractViolation);

        TaskId? retriedTaskId = null;
        string? retryMessage = null;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            dispatchAndStart: _ => DispatchStartOutcome.Started(),
            retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
            {
                retriedTaskId = taskId;
                retryMessage = message;
                return kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind);
            });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(tester.Id, retriedTaskId);
        Assert.Equal(WorkTaskStatus.Completed, developer.Status);
        Assert.Equal(WorkTaskStatus.Assigned, tester.Status);
        Assert.StartsWith("review-finding contract-repair:", retryMessage, StringComparison.Ordinal);
        Assert.Contains("Tester task", retryMessage, StringComparison.Ordinal);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Executed);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_contract_repair_escalates_at_the_repair_cap")]
    public void ConductorDriverContractRepairEscalatesAtRepairCap()
    {
        var (kernel, goal) = SoftwareGoal();
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task);
        }

        SeedReviewerIdentityViolation(kernel, goal, reviewer);
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            kernel.RetryTask(
                goal.Id,
                reviewer.Id,
                $"review-finding contract-repair: attempt {attempt}/2",
                retryRoundKind: RetryRoundKind.Mechanical);
            RecordMovedReviewerIdentityViolation(kernel, goal, reviewer);
        }

        string? escalation = null;
        var retried = false;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            retryTaskWithRoundKind: (_, _, _, _) =>
            {
                retried = true;
                return reviewer;
            },
            writeEscalation: (_, _, message) => escalation = message);

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.False(retried);
        Assert.Contains("contract-repair limit (2)", escalation, StringComparison.Ordinal);
        Assert.Contains("ERR_REVIEW_FINDING_IDENTITY_MOVED", escalation, StringComparison.Ordinal);
        Assert.Contains("canonical_open_count=1", escalation, StringComparison.Ordinal);
        Assert.Contains("adjudicate --goal <goal> <task#> close", escalation, StringComparison.Ordinal);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Escalated);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_unavailable_touch_proof_escalates_without_consuming_exhausted_repair_budget")]
    public void ConductorDriverUnavailableTouchProofEscalatesWithoutConsumingExhaustedRepairBudget()
    {
        const string diagnostic =
            "Round-diff touch proof unavailable because the carried finding round has no reviewed-commit baseline.";
        var (kernel, goal) = SoftwareGoal();
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task);
        }

        SeedReviewerIdentityViolation(kernel, goal, reviewer, diagnostic);
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            kernel.RetryTask(
                goal.Id,
                reviewer.Id,
                $"review-finding contract-repair: attempt {attempt}/2",
                retryRoundKind: RetryRoundKind.Mechanical);
            RecordMovedReviewerIdentityViolation(kernel, goal, reviewer, diagnostic);
        }

        var retried = false;
        string? escalation = null;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            retryTaskWithRoundKind: (_, _, _, _) =>
            {
                retried = true;
                return reviewer;
            },
            writeEscalation: (_, _, message) => escalation = message);

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.False(retried);
        Assert.IsType<ConductorAdvanceOutcome.Escalated>(result.Outcome);
        Assert.Contains("ERR_REVIEW_FINDING_IDENTITY_MOVED", escalation, StringComparison.Ordinal);
        Assert.Contains("suppression=missing-system-derived-round-diff-proof", escalation, StringComparison.Ordinal);
        Assert.Contains("mechanical repair budget was not consumed", escalation, StringComparison.Ordinal);
        Assert.Contains(diagnostic, escalation, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "Legacy identical-SHA diagnostic reproduces missing-proof suppression")]
    public void IdenticalShaLegacyDiagnostic_ReproducesMissingProofSuppression()
    {
        const string unchangedCommit = "bd7854c15aa6088fee94c13b50034ecf901f7591";
        const string legacyDiagnostic =
            $"Round-diff touch proof unavailable because the carried and current reviewed commits are identical ({unchangedCommit}).";
        var (kernel, goal) = SoftwareGoal();
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task);
        }

        var anchor = new ReviewFindingLocation("src/A.cs", "A.Run", "guard");
        var open = new ReviewFinding(
            "F-1",
            ReviewFindingState.Open,
            anchor,
            "Missing guard.",
            FindingSeverity.Advisory);
        DispatchTask(kernel, goal, reviewer, "review-open");
        kernel.RecordDispatchBaseCommit(goal.Id, reviewer.Id, unchangedCommit);
        kernel.RecordDispatchExecutionResult(goal.Id, reviewer.Id, new TaskVerificationRecord(
            "review-open",
            "C:\\tmp",
            0,
            ReviewerPassWithFinding(open),
            string.Empty,
            DateTimeOffset.UtcNow,
            WorkerResultPresent: true));

        kernel.RetryTask(goal.Id, reviewer.Id, "confirm resolution");
        DispatchTask(kernel, goal, reviewer, "review-resolved");
        kernel.RecordDispatchBaseCommit(goal.Id, reviewer.Id, unchangedCommit);
        kernel.RecordDispatchExecutionResult(goal.Id, reviewer.Id, new TaskVerificationRecord(
            "review-resolved",
            "C:\\tmp",
            0,
            ReviewerPassWithFinding(open with { State = ReviewFindingState.Resolved }),
            string.Empty,
            DateTimeOffset.UtcNow,
            WorkerResultPresent: true));

        kernel.RetryTask(goal.Id, reviewer.Id, "recheck unchanged commit");
        DispatchTask(kernel, goal, reviewer, "review-reopened", legacyDiagnostic);
        kernel.RecordDispatchBaseCommit(goal.Id, reviewer.Id, unchangedCommit);
        kernel.RecordDispatchExecutionResult(goal.Id, reviewer.Id, new TaskVerificationRecord(
            "review-reopened",
            "C:\\tmp",
            0,
            ReviewerPassWithFinding(open),
            string.Empty,
            DateTimeOffset.UtcNow,
            WorkerResultPresent: true));
        Assert.Equal(
            ReviewFindingConvergence.UntouchedReopenViolationCode,
            reviewer.LastVerification!.ReviewFindingContractViolation?.Code);

        var retried = false;
        string? escalation = null;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            retryTaskWithRoundKind: (_, _, _, _) =>
            {
                retried = true;
                return reviewer;
            },
            writeEscalation: (_, _, message) => escalation = message);

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.False(retried);
        Assert.IsType<ConductorAdvanceOutcome.Escalated>(result.Outcome);
        Assert.Contains(ReviewFindingConvergence.UntouchedReopenViolationCode, escalation, StringComparison.Ordinal);
        Assert.Contains("suppression=missing-system-derived-round-diff-proof", escalation, StringComparison.Ordinal);
        Assert.Contains("mechanical repair budget was not consumed", escalation, StringComparison.Ordinal);
        Assert.Contains(legacyDiagnostic, escalation, StringComparison.Ordinal);
        Assert.Empty(goal.Timeline.Where(evt =>
            evt.TaskId == reviewer.Id &&
            evt.Kind == ProgressKind.TaskRetried &&
            evt.Message.StartsWith("review-finding contract-repair:", StringComparison.Ordinal)));
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_legacy_reviewer_evidence_counters_do_not_cap_structured_requests")]
    public void ConductorDriverLegacyReviewerEvidenceCountersDoNotCapStructuredRequests()
    {
        var (kernel, goal) = SoftwareGoal();
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task);
        }

        PassVerification(kernel, goal, reviewer);
        kernel.RecordReviewerEvidenceRequestReceived(goal.Id, reviewer.Id, "prior evidence request 1");
        kernel.RecordReviewerEvidenceRequestReceived(goal.Id, reviewer.Id, "prior evidence request 2");
        kernel.RecordReviewerEvidenceRequestReceived(goal.Id, reviewer.Id, "prior evidence request 3");
        kernel.RetryTask(
            goal.Id,
            reviewer.Id,
            "review-finding contract-repair: prior mechanical repair",
            retryRoundKind: RetryRoundKind.Mechanical);
        FailReviewerNeedsWork(
            kernel,
            goal,
            reviewer,
            "still missing focused evidence",
            "Infrastructure.Tests: FullyQualifiedName~ConductorDriverTests", implicitFindingCategory: FindingCategory.AcceptanceOwned);
        var focusedRuns = 0;
        var retried = false;
        string? escalation = null;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            getPreReviewEvidenceContext: _ => NoPreReviewContext("abc1234"),
            runFocusedEvidence: (_, _) =>
            {
                focusedRuns++;
                return new FocusedEvidenceRunResult("", true, true, "structured request ran", []);
            },
            retryTask: (goalId, taskId, message) =>
            {
                retried = true;
                return kernel.RetryTask(goalId, taskId, message);
            },
            recordFindingEvidenceOutcome: (goalId, taskId, stableId, outcome, receipt) =>
                kernel.RecordFindingEvidenceOutcome(goalId, taskId, stableId, outcome, receipt),
            writeEscalation: (_, _, message) => escalation = message);

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(1, focusedRuns);
        Assert.True(retried);
        Assert.Null(escalation);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Executed);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_second_reviewer_evidence_request_within_bound_runs")]
    public void ConductorDriverSecondReviewerEvidenceRequestWithinBoundRuns()
    {
        var (kernel, goal) = SoftwareGoal();
        var reviewer = goal.Tasks.Single(t => t.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(t => t.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task);
        }

        // One prior evidence request already served this round; a legitimate second suite must
        // still run (multi-file goals commonly need receipts for more than one changed area),
        // not escalate as a "repeat".
        kernel.RecordReviewerEvidenceRequestReceived(goal.Id, reviewer.Id, "prior reviewer evidence request");
        FailReviewerNeedsWork(
            kernel,
            goal,
            reviewer,
            "still missing focused conductor evidence",
            "Infrastructure.Tests: FullyQualifiedName~ConductorDriverTests", implicitFindingCategory: FindingCategory.AcceptanceOwned);
        var focusedRuns = 0;
        var retried = false;
        string? escalation = null;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            getPreReviewEvidenceContext: _ => NoPreReviewContext("abc1234"),
            runFocusedEvidence: (_, _) =>
            {
                focusedRuns++;
                return new FocusedEvidenceRunResult("", true, true, "focused evidence passed", []);
            },
            retryTask: (gid, tid, msg) =>
            {
                retried = true;
                return kernel.RetryTask(gid, tid, msg);
            },
            writeEscalation: (_, _, message) => { escalation = message; });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(1, focusedRuns);
        Assert.True(retried);
        Assert.Null(escalation);
        Assert.False(result.Outcome is ConductorAdvanceOutcome.Escalated);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_legacy_request_count_does_not_suppress_current_structured_request")]
    public void ConductorDriverLegacyRequestCountDoesNotSuppressCurrentStructuredRequest()
    {
        var (kernel, goal) = SoftwareGoal();
        var reviewer = goal.Tasks.Single(t => t.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(t => t.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task);
        }

        // Three focused evidence requests already served this round; the fourth exceeds the
        // per-round bound and must escalate normally (loop protection is preserved).
        kernel.RecordReviewerEvidenceRequestReceived(goal.Id, reviewer.Id, "prior evidence request 1");
        kernel.RecordReviewerEvidenceRequestReceived(goal.Id, reviewer.Id, "prior evidence request 2");
        kernel.RecordReviewerEvidenceRequestReceived(goal.Id, reviewer.Id, "prior evidence request 3");
        FailReviewerNeedsWork(
            kernel,
            goal,
            reviewer,
            "still missing focused conductor evidence",
            "Infrastructure.Tests: FullyQualifiedName~ConductorDriverTests", implicitFindingCategory: FindingCategory.AcceptanceOwned);
        var focusedRuns = 0;
        var retried = false;
        string? escalation = null;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            getPreReviewEvidenceContext: _ => NoPreReviewContext("abc1234"),
            runFocusedEvidence: (_, _) =>
            {
                focusedRuns++;
                return new FocusedEvidenceRunResult("", true, true, "current structured request ran", []);
            },
            retryTask: (gid, tid, msg) =>
            {
                retried = true;
                return kernel.RetryTask(gid, tid, msg);
            },
            writeEscalation: (_, _, message) => { escalation = message; });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(1, focusedRuns);
        Assert.True(retried);
        Assert.Null(escalation);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Executed);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_recover_style_reviewer_retry_resets_evidence_round")]
    public void ConductorDriverRecoverStyleReviewerRetryResetsEvidenceRound()
    {
        var (kernel, goal) = SoftwareGoal();
        var reviewer = goal.Tasks.Single(t => t.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(t => t.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task);
        }

        // Three evidence requests from a prior review round would exceed the per-round bound.
        kernel.RecordReviewerEvidenceRequestReceived(goal.Id, reviewer.Id, "stale request 1");
        kernel.RecordReviewerEvidenceRequestReceived(goal.Id, reviewer.Id, "stale request 2");
        kernel.RecordReviewerEvidenceRequestReceived(goal.Id, reviewer.Id, "stale request 3");
        FailReviewerNeedsWork(kernel, goal, reviewer, "prior round needs evidence", "Infrastructure.Tests: prior", implicitFindingCategory: FindingCategory.AcceptanceOwned);

        // Operator recover retries the reviewer task with a NON-mechanical message, starting a fresh
        // evidence round; the three stale requests above must no longer count toward the bound.
        kernel.RetryTask(goal.Id, reviewer.Id, "operator recover reset the review round", invalidateDownstream: false);

        FailReviewerNeedsWork(kernel, goal, reviewer, "fresh round needs evidence", "Infrastructure.Tests: fresh", implicitFindingCategory: FindingCategory.AcceptanceOwned);
        goal = kernel.GetGoal(goal.Id);
        var focusedRuns = 0;
        var retried = false;
        string? escalation = null;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            getPreReviewEvidenceContext: _ => NoPreReviewContext("abc1234"),
            runFocusedEvidence: (_, _) =>
            {
                focusedRuns++;
                return new FocusedEvidenceRunResult("", true, true, "focused evidence passed", []);
            },
            retryTask: (gid, tid, msg) =>
            {
                retried = true;
                return kernel.RetryTask(gid, tid, msg);
            },
            writeEscalation: (_, _, message) => { escalation = message; });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(1, focusedRuns);
        Assert.True(retried);
        Assert.Null(escalation);
        Assert.False(result.Outcome is ConductorAdvanceOutcome.Escalated);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_unaccepted_structured_evidence_request_records_typed_refusal")]
    public void ConductorDriverUnacceptedStructuredEvidenceRequestRecordsTypedRefusal()
    {
        var (kernel, goal) = SoftwareGoal();
        var reviewer = goal.Tasks.Single(t => t.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(t => t.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task);
        }

        FailReviewerNeedsWork(kernel, goal, reviewer, "missing full test evidence", "Infrastructure.Tests: all", implicitFindingCategory: FindingCategory.AcceptanceOwned);
        var retried = false;
        var focusedRuns = 0;
        string? escalation = null;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            getPreReviewEvidenceContext: _ => NoPreReviewContext("abc1234"),
            runFocusedEvidence: (_, request) =>
            {
                focusedRuns++;
                Assert.Equal("Infrastructure.Tests:all", request);
                return new FocusedEvidenceRunResult(
                    request,
                    Accepted: false,
                    Passed: false,
                    Summary: "unbounded evidence request rejected",
                    Checks: [],
                    Rejection: new FocusedEvidenceRejection(
                        FocusedEvidenceRejectionCode.UnsafeFilter,
                        "all",
                        "unbounded evidence request rejected"));
            },
            retryTask: (gid, tid, msg) =>
            {
                retried = true;
                return kernel.RetryTask(gid, tid, msg);
            },
            recordFindingEvidenceRequest: (gid, tid, msg) => kernel.RecordFindingEvidenceRequest(gid, tid, msg),
            recordFindingEvidenceOutcome: (gid, tid, stableId, outcome, receipt) =>
                kernel.RecordFindingEvidenceOutcome(gid, tid, stableId, outcome, receipt),
            writeEscalation: (_, _, message) => { escalation = message; });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(1, focusedRuns);
        Assert.True(retried);
        Assert.Null(escalation);
        var outcome = reviewer.VerificationHistory.Last().MergedReviewFindings!.Single().EvidenceOutcome;
        Assert.False(outcome?.Honoured);
        Assert.Equal(FindingEvidenceNotHonouredReason.UnparseableSelection, outcome?.Reason);
        Assert.Contains("offending_filter='all'", outcome?.Detail, StringComparison.Ordinal);
        Assert.Contains(goal.Timeline, evt =>
            evt.TaskId == reviewer.Id &&
            evt.Kind == ProgressKind.FindingEvidenceRequestRecorded &&
            evt.Message.Contains("reason=unparseable-selection", StringComparison.Ordinal));
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Executed);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_failing_structured_evidence_run_retries_with_receipt")]
    public void ConductorDriverFailingStructuredEvidenceRunRetriesWithReceipt()
    {
        var (kernel, goal) = SoftwareGoal();
        var reviewer = goal.Tasks.Single(t => t.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(t => t.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task);
        }

        FailReviewerNeedsWork(
            kernel,
            goal,
            reviewer,
            "missing focused conductor evidence",
            "Infrastructure.Tests: FullyQualifiedName~ConductorDriverTests", implicitFindingCategory: FindingCategory.AcceptanceOwned);
        var retried = false;
        string? escalation = null;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            getPreReviewEvidenceContext: _ => NoPreReviewContext("abc1234"),
            runFocusedEvidence: (_, request) => new FocusedEvidenceRunResult(
                request,
                Accepted: true,
                Passed: false,
                Summary: "focused evidence failed: reviewer focused evidence exit 1; receipts: C:\\tmp\\failed-trx",
                Checks:
                [
                    new AcceptanceCheckResult(
                        "reviewer focused evidence",
                        false,
                        1,
                        "Failed! - Failed: 1",
                        ArtifactsPath: "C:\\tmp\\failed-trx")
                ]),
            retryTask: (gid, tid, msg) =>
            {
                retried = true;
                return kernel.RetryTask(gid, tid, msg);
            },
            recordFindingEvidenceRun: (gid, tid, msg) => kernel.RecordFindingEvidenceRun(gid, tid, msg),
            recordFindingEvidenceOutcome: (gid, tid, stableId, outcome, receipt) =>
                kernel.RecordFindingEvidenceOutcome(gid, tid, stableId, outcome, receipt),
            writeEscalation: (_, _, message) => { escalation = message; });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.True(retried);
        Assert.Null(escalation);
        Assert.Contains(goal.Timeline, evt =>
            evt.TaskId == reviewer.Id &&
            evt.Kind == ProgressKind.FindingEvidenceRunRecorded &&
            evt.Message.Contains("C:\\tmp\\failed-trx", StringComparison.Ordinal));
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Executed);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_reviewer_retry_warning_is_recorded_on_the_target_task")]
    public void ConductorDriverReviewerRetryWarningIsRecordedOnTheTargetTask()
    {
        var (kernel, goal) = SoftwareGoal();
        var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
            PassVerification(kernel, goal, task);

        for (var round = 1; round <= 3; round++)
        {
            kernel.RetryTask(goal.Id, developer.Id, $"auto-review-retry round {round}: prior reviewer finding");
            PassVerification(kernel, goal, developer);
        }

        FailReviewerNeedsWork(kernel, goal, reviewer, "Developer still misses the review blocker.");
        var notes = new List<(TaskId TaskId, string Message)>();
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            dispatchAndStart: _ => DispatchStartOutcome.Started(),
            retryTask: (goalId, taskId, message) => kernel.RetryTask(goalId, taskId, message),
            recordTaskNote: (goalId, taskId, message) =>
            {
                notes.Add((taskId, message));
                kernel.RecordTaskNote(goalId, taskId, message);
            });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Contains(notes, note =>
            note.TaskId == developer.Id &&
            note.Message.Contains("auto-review-retry escalation-warning round 4/6", StringComparison.Ordinal));
        Assert.Contains(goal.Timeline, item =>
            item.TaskId == developer.Id &&
            item.Message.Contains("auto-review-retry escalation-warning round 4/6", StringComparison.Ordinal));
        Assert.IsType<ConductorAdvanceOutcome.Executed>(result.Outcome);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_reviewer_needs_work_round_7_stops_and_escalates")]
    public void ConductorDriverReviewerNeedsWorkRound7StopsAndEscalates()
    {
        var (kernel, goal) = SoftwareGoal();
        var developer = goal.Tasks.Single(t => t.RequiredRole == AgentRole.Developer);
        var tester = goal.Tasks.Single(t => t.RequiredRole == AgentRole.Tester);
        var reviewer = goal.Tasks.Single(t => t.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(t => t.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task);
        }

        for (var i = 1; i <= 6; i++)
        {
            kernel.RetryTask(goal.Id, developer.Id, $"auto-review-retry round {i}: prior reviewer finding");
            PassVerification(kernel, goal, developer);
            PassVerification(kernel, goal, tester);
        }

        FailReviewerNeedsWork(kernel, goal, reviewer, "Developer still misses the review blocker.");
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

    [Xunit.Fact(DisplayName = "ConductorDriver_reviewer_false_pass_at_cap_surfaces_retained_findings")]
    public void ConductorDriverReviewerFalsePassAtCapSurfacesRetainedFindings()
    {
        var (kernel, goal) = SoftwareGoal();
        var developer = goal.Tasks.Single(t => t.RequiredRole == AgentRole.Developer);
        var reviewer = goal.Tasks.Single(t => t.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(t => t.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task);
        }

        var finding = new ReviewFinding(
            "F-CAP-BOUNDARY",
            ReviewFindingState.Open,
            new ReviewFindingLocation("src/Guard.cs", "Guard.Run", "preemption"),
            "Preemption remains unproved.",
            FindingSeverity.Blocking,
            FindingCategory.TestCoverage);
        FailReviewerNeedsWork(kernel, goal, reviewer, finding.Description, findings: [finding]);
        for (var i = 1; i <= 6; i++)
        {
            kernel.RetryTask(goal.Id, developer.Id, $"auto-review-retry round {i}: prior reviewer finding");
            PassVerification(kernel, goal, developer);
        }

        kernel.RetryTask(goal.Id, reviewer.Id, "operator requested final review");
        kernel.RecordTaskDispatch(goal.Id, reviewer.Id, new TaskDispatchRecord(
            "reviewer",
            "review-cap",
            "C:\\tmp",
            DateTimeOffset.UtcNow,
            BaseCommit: "4cc96e28",
            ReviewFindingTouchedAnchors: [],
            ReviewRetryCap: new ReviewRetryCapReceipt(7, 7)));
        var resolved = finding with { State = ReviewFindingState.Resolved };
        var stdout = string.Join(
            Environment.NewLine,
            "WORKER_RESULT:",
            "files: none",
            "commands: review",
            "tests: pass - inspected unchanged candidate",
            "commit: none",
            "blockers: none",
            $"findings: {JsonSerializer.Serialize(new[] { resolved })}",
            "touched_anchors: []",
            "verdict: pass",
            "model_fit: fixture/model - adequate - review",
            "skills: none",
            "confidence: high",
            "END_WORKER_RESULT");
        kernel.RecordDispatchExecutionResult(goal.Id, reviewer.Id, new TaskVerificationRecord(
            "review-cap",
            "C:\\tmp",
            0,
            stdout,
            string.Empty,
            DateTimeOffset.UtcNow,
            StandardOutputPath: "C:\\tmp\\reviewer-cap.out.log",
            WorkerResultPresent: true));

        Assert.Equal(WorkTaskStatus.Failed, reviewer.Status);
        Assert.Equal(
            ReviewFindingConvergence.UnprovenResolutionAtCapViolationCode,
            reviewer.LastVerification!.ReviewFindingContractViolation!.Code);
        var dispatched = false;
        string? escalation = null;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            dispatchAndStart: _ => { dispatched = true; return DispatchStartOutcome.Started(); },
            writeEscalation: (_, _, message) => { escalation = message; });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.False(dispatched);
        Assert.Contains("stopped at review round 7/7", escalation);
        Assert.Contains("surviving_stable_ids=F-CAP-BOUNDARY", escalation);
        Assert.Contains("candidate_sha=4cc96e28", escalation);
        Assert.Contains("retry --goal", escalation);
        Assert.Contains("explicit override", escalation);
        Assert.Contains("goal-amend", escalation);
        Assert.Contains("supersede", escalation);
        Assert.Contains("C:\\tmp\\reviewer-cap.out.log", escalation);
        Assert.False(goal.IsTerminal);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Escalated);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_reviewer_honest_blocked_at_cap_surfaces_operator_decision")]
    public void ConductorDriverReviewerHonestBlockedAtCapSurfacesOperatorDecision()
    {
        var (kernel, goal) = SoftwareGoal();
        var developer = goal.Tasks.Single(t => t.RequiredRole == AgentRole.Developer);
        var reviewer = goal.Tasks.Single(t => t.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(t => t.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task);
        }

        for (var i = 1; i <= 6; i++)
        {
            kernel.RetryTask(goal.Id, developer.Id, $"auto-review-retry round {i}: prior reviewer finding");
            PassVerification(kernel, goal, developer);
        }

        kernel.RecordTaskDispatch(goal.Id, reviewer.Id, new TaskDispatchRecord(
            "reviewer",
            "review-cap",
            "C:\\tmp",
            DateTimeOffset.UtcNow,
            BaseCommit: "candidate-cap-sha",
            ReviewRetryCap: new ReviewRetryCapReceipt(7, 7)));
        var finding = new ReviewFinding(
            "F-HONEST-CAP",
            ReviewFindingState.Open,
            new ReviewFindingLocation("src/Guard.cs", "Guard.Run", "guard"),
            "Guard is still missing.",
            FindingSeverity.Blocking,
            FindingCategory.Correctness);
        var stdout = string.Join(
            Environment.NewLine,
            "WORKER_RESULT:",
            "files: none",
            "commands: review",
            "tests: pass - inspected candidate",
            "commit: none",
            "blockers: F-HONEST-CAP - Guard is still missing.",
            $"findings: {JsonSerializer.Serialize(new[] { finding })}",
            "touched_anchors: []",
            "verdict: blocked-at-cap",
            "model_fit: fixture/model - adequate - review",
            "skills: none",
            "confidence: high",
            "END_WORKER_RESULT");
        kernel.RecordDispatchExecutionResult(goal.Id, reviewer.Id, new TaskVerificationRecord(
            "review-cap",
            "C:\\tmp",
            0,
            stdout,
            string.Empty,
            DateTimeOffset.UtcNow,
            StandardOutputPath: "C:\\tmp\\honest-cap.out.log",
            WorkerResultPresent: true,
            ReviewedCommit: "candidate-cap-sha"));
        Assert.Equal(WorkTaskStatus.Failed, reviewer.Status);

        var dispatched = false;
        string? escalation = null;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            dispatchAndStart: _ => { dispatched = true; return DispatchStartOutcome.Started(); },
            writeEscalation: (_, _, message) => { escalation = message; });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.False(dispatched);
        Assert.Contains("blocked-at-cap", escalation);
        Assert.Contains("surviving_stable_ids=F-HONEST-CAP", escalation);
        Assert.Contains("candidate_sha=candidate-cap-sha", escalation);
        Assert.Contains("operator decision required", escalation);
        Assert.Contains("C:\\tmp\\honest-cap.out.log", escalation);
        Assert.False(goal.IsTerminal);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Escalated);
    }

    [Xunit.Fact(DisplayName = "Subscription_dispatch_paths_use_workspace_configured_review_stop_round")]
    public void SubscriptionDispatchPathsUseWorkspaceConfiguredReviewStopRound()
    {
        var root = CreateTempDirectory();
        MainBranchGitRepositoryTemplate.CopyTo(root, includeSkillCatalog: true);
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        _ = StateDbMigrations.EnsureUpToDate(workspace.SqliteStatePath);
        Directory.CreateDirectory(workspace.OrchestratorDirectory);
        var configuredPolicy = ConductorAutonomyPolicy.Permissive with
        {
            ReviewAutoRetryStopRound = 9
        };
        File.WriteAllText(
            Path.Combine(workspace.OrchestratorDirectory, "conductor-policy.json"),
            configuredPolicy.ToJson());

        ReviewRetryCapReceipt Prepare(string path)
        {
            var kernel = new AgentOrchestratorKernel();
            var reviewer = new TaskSpec(TaskId.New(), "Review configured cap", AgentRole.Reviewer);
            var goal = kernel.CreateGoal("Use the configured review cap", [reviewer]);
            kernel.SetGoalRefinedSpec(goal.Id, new RefinedSpec(
                "Review configured cap",
                ["The dispatch receipt uses the configured stop round."],
                VerificationClass.TestVerifiable,
                [],
                []));
            kernel.ActivateGoal(goal.Id, DefaultAgents());
            var profiles = WorkerProfileCatalog.Default();
            if (path == "ready-batch")
            {
                var batch = new GoalDispatchOperations().SubscriptionDispatchReadyBatch(
                    kernel,
                    workspace,
                    goal,
                    DefaultAgents(),
                    profiles);
                Assert.Single(batch.Dispatches);
            }
            else if (path == "profile")
            {
                new GoalDispatchOperations().ProfileDispatchTask(
                    kernel,
                    workspace,
                    goal,
                    reviewer,
                    profiles.GetRequired("codex-cli"),
                    DefaultAgents());
            }
            else
            {
                new GoalDispatchOperations().SubscriptionDispatchTask(
                    kernel,
                    workspace,
                    goal,
                    reviewer,
                    DefaultAgents(),
                    profiles);
                if (path == "refresh")
                {
                    new GoalDispatchOperations().RefreshPreparedDispatchBeforeStart(
                        kernel,
                        workspace,
                        goal,
                        reviewer,
                        DefaultAgents(),
                        profiles);
                }
            }

            return Assert.IsType<ReviewRetryCapReceipt>(reviewer.LastDispatch!.ReviewRetryCap);
        }

        Assert.Equal(9, Prepare("subscription-task").StopRound);
        Assert.Equal(9, Prepare("ready-batch").StopRound);
        Assert.Equal(9, Prepare("profile").StopRound);
        Assert.Equal(9, Prepare("refresh").StopRound);
    }

}
