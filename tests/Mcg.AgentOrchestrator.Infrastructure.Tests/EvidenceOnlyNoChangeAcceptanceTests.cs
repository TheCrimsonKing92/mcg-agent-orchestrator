using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using static ConductorDriverTests;

[Xunit.Collection("IsolatedProcessSpawning")]
public sealed class EvidenceOnlyNoChangeAcceptanceTests
{
    private const string Candidate = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Xunit.Theory]
    [Xunit.InlineData(FindingCategory.SpecCompliance)]
    [Xunit.InlineData(FindingCategory.Correctness)]
    public void ReviewerRequestAtUnchangedCandidateCompletesDeveloperRoundExactlyOnce(
        FindingCategory category)
    {
        var (kernel, goal, developer, reviewer) = SeedRetry(
            EvidenceFindingWithRequest("Run the focused class at this candidate.",
                id: "focused-class", category: category,
                project: "Core.Tests",
                classes: ["DispatchOutcomeClassifyTests"]));
        var verification = RejectedRound(developer, "deferred", "none", true);

        kernel.RecordDispatchExecutionResult(goal.Id, developer.Id, verification);
        kernel.RecordDispatchExecutionResult(goal.Id, developer.Id, verification);

        Xunit.Assert.Equal(WorkTaskStatus.Completed, developer.Status);
        Xunit.Assert.Equal(Candidate, developer.LastDispatch!.ResultCommit);
        Xunit.Assert.Equal(ReviewFindingState.Open,
            reviewer.LastVerification!.MergedReviewFindings!.Single().State);
        var note = Xunit.Assert.Single(goal.Timeline.Where(evt =>
            evt.Kind == ProgressKind.TaskNote &&
            evt.Message.StartsWith("EVIDENCE_ONLY_ROUND_ACCEPTED ", StringComparison.Ordinal)));
        Xunit.Assert.Contains("finding_ids=focused-class", note.Message, StringComparison.Ordinal);
        Xunit.Assert.Contains($"candidate={Candidate}", note.Message, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void RoundEmittedRequestCoversRetryTargetAndIsRecorded()
    {
        var (kernel, goal, developer, _) = SeedRetry(new ReviewFinding(
            "detach-class-execution", ReviewFindingState.Open,
            new ReviewFindingLocation("tests/Dispatch.cs", "Detach"),
            "The focused class has no execution receipt.",
            FindingSeverity.Blocking, FindingCategory.SpecCompliance));
        var ownFinding = EvidenceFindingWithRequest("Run the missing class.",
            id: "detach-class-execution", category: FindingCategory.TestEvidence,
            project: "Core.Tests",
            classes: ["DispatchOutcomeClassifyTests"]);

        kernel.RecordDispatchExecutionResult(goal.Id, developer.Id,
            RejectedRound(developer, "deferred", "none", false, ownFinding));

        Xunit.Assert.Equal(WorkTaskStatus.Completed, developer.Status);
        var request = Xunit.Assert.Single(goal.Timeline.Where(evt =>
            evt.Kind == ProgressKind.FindingEvidenceRequestRecorded && evt.TaskId == developer.Id));
        Xunit.Assert.Contains("finding_id=detach-class-execution", request.Message, StringComparison.Ordinal);
        Xunit.Assert.Contains($"candidate_sha={Candidate}", request.Message, StringComparison.Ordinal);
    }

    [Xunit.Theory]
    [Xunit.InlineData("fail", "none", 0, "none", false)]
    [Xunit.InlineData("inconclusive - no result", "none", 0, "none", false)]
    [Xunit.InlineData("deferred", "exact-blocker - source change needed", 0, "none", false)]
    [Xunit.InlineData("deferred", "none", 1, "none", false)]
    [Xunit.InlineData("deferred", "none", 0, "src/Changed.cs", false)]
    [Xunit.InlineData("deferred", "none", 0, "none", true)]
    public void NonEvidenceOnlyRoundsKeepOriginalRejection(
        string tests, string blockers, int commits, string changedPaths, bool movedHead)
    {
        var (kernel, goal, developer, _) = SeedRetry(
            EvidenceFindingWithRequest("Run the focused class.", "focused-class",
                FindingCategory.SpecCompliance, project: "Core.Tests",
                classes: ["DispatchOutcomeClassifyTests"]));
        var verification = RejectedRound(developer, tests, blockers, true,
            commits: commits, changedPaths: changedPaths,
            head: movedHead ? new string('b', 40) : Candidate);

        kernel.RecordDispatchExecutionResult(goal.Id, developer.Id, verification);

        Xunit.Assert.Equal(WorkTaskStatus.Failed, developer.Status);
        Xunit.Assert.DoesNotContain(goal.Timeline, evt =>
            evt.Message.StartsWith("EVIDENCE_ONLY_ROUND_ACCEPTED ", StringComparison.Ordinal));
        Xunit.Assert.Contains(goal.Timeline, evt => evt.Kind == ProgressKind.TaskFailed &&
            evt.Message.Contains("reason=no-change-evidence", StringComparison.Ordinal));
    }

    [Xunit.Fact]
    public void RetryTargetWithoutTypedRequestStaysRejected()
    {
        var (kernel, goal, developer, _) = SeedRetry(new ReviewFinding(
            "source-defect", ReviewFindingState.Open,
            new ReviewFindingLocation("src/Thing.cs", "Run"),
            "Source defect remains.", FindingSeverity.Blocking, FindingCategory.Correctness));

        kernel.RecordDispatchExecutionResult(goal.Id, developer.Id,
            RejectedRound(developer, "deferred", "none", true));

        Xunit.Assert.Equal(WorkTaskStatus.Failed, developer.Status);
        Xunit.Assert.DoesNotContain(goal.Timeline, evt =>
            evt.Message.StartsWith("EVIDENCE_ONLY_ROUND_ACCEPTED ", StringComparison.Ordinal));
    }

    [Xunit.Fact]
    public void EmptyRetryTargetSetStaysRejected()
    {
        var (kernel, goal) = SoftwareGoal();
        var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
        kernel.RecordTaskDispatch(goal.Id, developer.Id,
            new TaskDispatchRecord("codex-cli", "developer-retry", "C:\\tmp",
                DateTimeOffset.UtcNow, BaseCommit: Candidate));

        kernel.RecordDispatchExecutionResult(goal.Id, developer.Id,
            RejectedRound(developer, "deferred", "none", true));

        Xunit.Assert.Equal(WorkTaskStatus.Failed, developer.Status);
        Xunit.Assert.DoesNotContain(goal.Timeline, evt =>
            evt.Message.StartsWith("EVIDENCE_ONLY_ROUND_ACCEPTED ", StringComparison.Ordinal));
    }

    [Xunit.Fact]
    public void ReviewerOwnPendingRequestRunsAtReviewedCandidate()
    {
        var (kernel, goal) = SoftwareGoal();
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task);
        }
        FailReviewerNeedsWork(kernel, goal, reviewer, "focused receipt missing",
            findings: [EvidenceFindingWithRequest("focused receipt missing", "self-request",
                FindingCategory.SpecCompliance, classes: ["ConductorDriverTests"])],
            reviewedCommit: Candidate);
        var focusedRuns = 0;
        var driver = MakeDriver(
            getPreReviewEvidenceContext: _ => NoPreReviewContext(Candidate),
            runFocusedEvidence: (_, request) =>
            {
                focusedRuns++;
                return new FocusedEvidenceRunResult(request, true, true, "focused class passed", []);
            },
            retryTask: (goalId, taskId, message) => kernel.RetryTask(goalId, taskId, message),
            recordFindingEvidenceRequest: (goalId, taskId, message) =>
                kernel.RecordFindingEvidenceRequest(goalId, taskId, message),
            recordFindingEvidenceOutcome: (goalId, taskId, stableId, outcome, receipt) =>
                kernel.RecordFindingEvidenceOutcome(goalId, taskId, stableId, outcome, receipt),
            recordFindingEvidenceSuppressed: (goalId, taskId, candidateSha, blockerIds, requestId, owner, reason, identity) =>
                kernel.RecordFindingEvidenceSuppressed(
                    goalId, taskId, candidateSha, blockerIds, requestId, owner, reason, identity));

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Xunit.Assert.Equal(1, focusedRuns);
        Xunit.Assert.DoesNotContain(goal.Timeline, evt =>
            evt.Kind == ProgressKind.FindingEvidenceSuppressed &&
            evt.Message.Contains("unresolved-writable-blockers-on-unchanged-candidate", StringComparison.Ordinal));
        var finding = reviewer.VerificationHistory.Last().MergedReviewFindings!.Single();
        Xunit.Assert.Equal(ReviewFindingState.Open, finding.State);
        Xunit.Assert.NotNull(finding.EvidenceOutcome?.ReceiptId);
    }

    [Xunit.Fact]
    public void OtherWritableFindingStillSuppressesTheRequest()
    {
        var (kernel, goal) = SoftwareGoal();
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task);
        }
        FailReviewerNeedsWork(kernel, goal, reviewer, "focused receipt and source repair needed",
            findings:
            [
                EvidenceFindingWithRequest("focused receipt missing", "self-request",
                    FindingCategory.SpecCompliance, classes: ["ConductorDriverTests"]),
                new ReviewFinding("source-defect", ReviewFindingState.Open,
                    new ReviewFindingLocation("src/Thing.cs", "Run"), "Source repair needed.",
                    FindingSeverity.Blocking, FindingCategory.Correctness)
            ], reviewedCommit: Candidate);
        var focusedRuns = 0;
        var driver = MakeDriver(
            getPreReviewEvidenceContext: _ => NoPreReviewContext(Candidate),
            runFocusedEvidence: (_, request) =>
            {
                focusedRuns++;
                return new FocusedEvidenceRunResult(request, true, true, "focused class passed", []);
            },
            retryTask: (goalId, taskId, message) => kernel.RetryTask(goalId, taskId, message),
            recordFindingEvidenceSuppressed: (goalId, taskId, candidateSha, blockerIds, requestId, owner, reason, identity) =>
                kernel.RecordFindingEvidenceSuppressed(
                    goalId, taskId, candidateSha, blockerIds, requestId, owner, reason, identity));

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Xunit.Assert.Equal(0, focusedRuns);
        Xunit.Assert.Contains(goal.Timeline, evt =>
            evt.Kind == ProgressKind.FindingEvidenceSuppressed &&
            evt.Message.Contains("blocker_ids=self-request,source-defect", StringComparison.Ordinal));
    }

    private static (AgentOrchestratorKernel Kernel, Goal Goal, TaskSpec Developer, TaskSpec Reviewer)
        SeedRetry(ReviewFinding finding)
    {
        var (kernel, goal) = SoftwareGoal();
        var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        FailReviewerNeedsWork(kernel, goal, reviewer, finding.Description,
            findings: [finding], reviewedCommit: Candidate);
        kernel.RecordTaskDispatch(goal.Id, developer.Id,
            new TaskDispatchRecord("codex-cli", "developer-retry", "C:\\tmp",
                DateTimeOffset.UtcNow.AddSeconds(1), BaseCommit: Candidate));
        return (kernel, goal, developer, reviewer);
    }

    private static TaskVerificationRecord RejectedRound(
        TaskSpec developer, string tests, string blockers, bool recognized,
        ReviewFinding? ownFinding = null, int commits = 0,
        string changedPaths = "none", string head = Candidate)
    {
        var stdout = string.Join(Environment.NewLine,
            "WORKER_RESULT:", "files: none", "commands: none",
            $"tests: {tests}", $"blockers: {blockers}",
            $"findings: {JsonSerializer.Serialize(ownFinding is null ? Array.Empty<ReviewFinding>() : new[] { ownFinding })}",
            "touched_anchors: []", "commit: none", "END_WORKER_RESULT");
        var stderr = string.Join(Environment.NewLine,
            DispatchRejectionDiagnosticMarker.Format(recognized, commits, changedPaths),
            $"Developer/Tester dispatch did not produce required relevant file-change evidence. branch=goal/test; head={head}; worktree=clean; commits_after_dispatch={commits}; changed_paths={changedPaths}.",
            DispatchFailureDiagnosticMarker.Format(DispatchFailureDiagnosticMarker.RequiredFileChangeEvidenceMissing));
        return new TaskVerificationRecord(developer.LastDispatch!.Command,
            developer.LastDispatch.WorkingDirectory, 1, stdout, stderr,
            DateTimeOffset.UtcNow.AddSeconds(2), WorkerResultPresent: true);
    }
}
