using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using System.Text.Json;

using static ConductorDriverTests;

[Xunit.Collection("IsolatedProcessSpawning")]
public sealed class ConductorDriverTestsPassingFindingEvidence
{
    [Xunit.Fact]
    public void TesterPassingRequestRoutesTesterWithFindingBoundReceipt()
    {
        var (kernel, goal) = SoftwareGoal();
        var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
        var tester = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
        PassVerification(kernel, goal, developer);
        DispatchTask(kernel, goal, tester, "test");
        var finding = EvidenceFindingWithRequest(
            "Conductor routing needs an executed receipt.",
            id: "tester-evidence",
            category: FindingCategory.TestEvidence,
            project: "Mcg.AgentOrchestrator.Infrastructure.Tests",
            classes: ["ConductorDriverTests"]);
        var output = string.Join(
            Environment.NewLine,
            "WORKER_RESULT:",
            "files: none",
            "commands: test",
            "tests: fail - receipt missing",
            "blockers: exact-blocker - receipt missing",
            $"findings: {JsonSerializer.Serialize(new[] { finding })}",
            "touched_anchors: []",
            "model_fit: test/test - adequate - fixture - fixture",
            "skills: none",
            "confidence: high",
            "END_WORKER_RESULT");
        kernel.RecordDispatchExecutionResult(
            goal.Id,
            tester.Id,
            new TaskVerificationRecord(
                "test", "C:\\tmp", 1, output, "", DateTimeOffset.UtcNow, WorkerResultPresent: true));

        var focusedRuns = 0;
        var retries = new List<(TaskId TaskId, RetryCause Cause, string Message)>();
        var driver = MakeDriver(
            getPreReviewEvidenceContext: _ => NoPreReviewContext("abc1234"),
            runFocusedEvidence: (_, request) =>
            {
                focusedRuns++;
                return new FocusedEvidenceRunResult(request, true, true, "tester requested evidence passed", []);
            },
            retryTaskWithCause: (goalId, taskId, message, roundKind, cause) =>
            {
                retries.Add((taskId, cause, message));
                return kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind, retryCause: cause);
            },
            recordFindingEvidenceRequest: (goalId, taskId, message) =>
                kernel.RecordFindingEvidenceRequest(goalId, taskId, message),
            recordFindingEvidenceRun: (goalId, taskId, message) =>
                kernel.RecordFindingEvidenceRun(goalId, taskId, message),
            recordFindingEvidenceOutcome: (goalId, taskId, stableId, outcome, receipt) =>
                kernel.RecordFindingEvidenceOutcome(goalId, taskId, stableId, outcome, receipt));

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(1, focusedRuns);
        Assert.IsType<ConductorAdvanceOutcome.Executed>(result.Outcome);
        var retry = Assert.Single(retries);
        Assert.Equal(tester.Id, retry.TaskId);
        Assert.Equal(RetryCause.CriterionEvidenceOwnerMismatch, retry.Cause);
        Assert.Equal(WorkTaskStatus.Assigned, tester.Status);
        var receipt = Assert.Single(tester.VerificationHistory.Last().FindingEvidenceReceipts!);
        Assert.True(receipt.Accepted && receipt.Passed);
        Assert.Equal("abc1234", receipt.CandidateSha);
        Assert.Contains(receipt.ReceiptId, retry.Message, StringComparison.Ordinal);
        Assert.Contains("tester-evidence", retry.Message, StringComparison.Ordinal);
        Assert.Contains("abc1234", retry.Message, StringComparison.Ordinal);
        Assert.Contains("route=tester", retry.Message, StringComparison.Ordinal);
        Assert.Contains(FindingReceiptClosureDiagnosis.VerificationNotSucceeded, retry.Message, StringComparison.Ordinal);
        Assert.Equal(0, FindingEvidenceExecutionClassifier.CountEvidenceDeliveryRetries(
            goal.Timeline, tester.Id, "abc1234", "tester-evidence"));
        Assert.DoesNotContain(goal.Timeline, evt =>
            evt.Message.Contains("retry cap reached", StringComparison.OrdinalIgnoreCase));
        var testerBrief = kernel.BuildTaskBrief(goal.Id, tester.Id).Content;
        Assert.Contains("evidence_receipt:", testerBrief, StringComparison.Ordinal);
        Assert.Contains("tester requested evidence passed", testerBrief, StringComparison.Ordinal);
        var nonRequesterBrief = kernel.BuildTaskBrief(goal.Id, developer.Id).Content;
        Assert.Contains("evidence_index:", nonRequesterBrief, StringComparison.Ordinal);
        Assert.DoesNotContain("evidence_receipt:", nonRequesterBrief, StringComparison.Ordinal);
        Assert.DoesNotContain("tester requested evidence passed", nonRequesterBrief, StringComparison.Ordinal);
        // The point-of-decision fields on that line: a passing receipt reads as measured only at the
        // exact candidate it was taken on. An unknown candidate, or a later one, stays unmeasured
        // even though the same honoured receipt is attached to the finding.
        Assert.Equal("state=candidate-unknown; candidate_sha=unavailable", EvidenceIndexState(nonRequesterBrief));
        Assert.Equal(
            "state=executed-on-candidate; candidate_sha=abc1234",
            EvidenceIndexState(kernel.BuildTaskBrief(goal.Id, developer.Id, targetHeadCommit: "abc1234").Content));
        Assert.Equal(
            "state=pending-execution; candidate_sha=def5678",
            EvidenceIndexState(kernel.BuildTaskBrief(goal.Id, developer.Id, targetHeadCommit: "def5678").Content));
        Assert.Contains(goal.Timeline, evt =>
            evt.TaskId == tester.Id &&
            evt.Kind == ProgressKind.FindingEvidenceRequestRecorded &&
            evt.Message.Contains("role=Tester", StringComparison.Ordinal) &&
            evt.Message.Contains("finding_id=tester-evidence", StringComparison.Ordinal));
        Assert.Contains(goal.Timeline, evt =>
            evt.TaskId == tester.Id &&
            evt.Kind == ProgressKind.FindingEvidenceRunRecorded &&
            evt.Message.Contains("role=Tester", StringComparison.Ordinal) &&
            evt.Message.Contains("finding_id=tester-evidence", StringComparison.Ordinal));
        var recorded = tester.VerificationHistory.Last().MergedReviewFindings!.Single(item => item.StableId == "tester-evidence");
        Assert.True(recorded.EvidenceOutcome?.Honoured);
        Assert.Equal(ReviewFindingState.Open, recorded.State);
    }

    // The trailing `state=...; candidate_sha=...` of the single evidence_index line in a brief.
    private static string EvidenceIndexState(string brief)
    {
        var line = Assert.Single(
            brief.Split(Environment.NewLine),
            candidate => candidate.Contains("evidence_index:", StringComparison.Ordinal));
        var start = line.IndexOf("state=", StringComparison.Ordinal);
        Assert.True(start >= 0, $"evidence_index line carried no state=: {line}");
        return line[start..];
    }
}
