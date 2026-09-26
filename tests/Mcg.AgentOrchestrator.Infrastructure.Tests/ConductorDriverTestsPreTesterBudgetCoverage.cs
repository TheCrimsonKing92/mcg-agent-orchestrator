using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

using static ConductorDriverTests;

[Xunit.Collection("IsolatedProcessSpawning")]
public sealed class ConductorDriverTestsPreTesterBudgetCoverage
{
    private const string CandidateSha = "abc1234";

    [Theory]
    [InlineData("CoveredTests", "Infrastructure.Tests", "green", false)]
    [InlineData("CoveredTests", "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj", "green", false)]
    [InlineData("UncoveredTests", "Infrastructure.Tests", "green", true)]
    [InlineData("CoveredTests", "Infrastructure.Tests", "red", true)]
    public void PreTesterBudgetOnlyCapsSelectionsCoveredByItsReceipt(
        string requestedClass, string project, string outcome, bool shouldRun)
    {
        var (kernel, goal) = SoftwareGoal();
        var tester = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
            PassVerification(kernel, goal, task);
        kernel.RecordFindingEvidenceRun(goal.Id, tester.Id,
            PreTesterEvidenceIndexLines.FormatMarker(new PreTesterEvidenceEntry(
                outcome, CandidateSha, "pre-tester-receipt",
                ["Infrastructure.Tests:CoveredTests"], ["UncoveredTests"], null, [])));
        FailReviewerNeedsWork(kernel, goal, reviewer, "focused evidence requested",
            findings: [EvidenceFindingWithRequest("focused evidence requested",
                category: FindingCategory.TestEvidence, project: project,
                classes: [requestedClass])]);

        var runs = 0;
        var preTesterAttachments = new List<FindingEvidenceReceipt>();
        var driver = MakeDriver(
            getPreReviewEvidenceContext: _ => NoPreReviewContext(CandidateSha),
            runFocusedEvidence: (_, request) =>
            {
                runs++;
                return new FocusedEvidenceRunResult(request, true, true,
                    "focused evidence executed", []);
            },
            retryTaskWithCause: (goalId, taskId, message, round, cause) =>
                kernel.RetryTask(goalId, taskId, message, retryRoundKind: round, retryCause: cause),
            recordFindingEvidenceRequest: (goalId, taskId, message) =>
                kernel.RecordFindingEvidenceRequest(goalId, taskId, message),
            recordFindingEvidenceRun: (goalId, taskId, message) =>
                kernel.RecordFindingEvidenceRun(goalId, taskId, message),
            recordFindingEvidenceOutcome: (goalId, taskId, stableId, outcome, receipt) =>
            {
                if (outcome.DecisionReason == "pre-tester-covered")
                    preTesterAttachments.Add(Assert.IsType<FindingEvidenceReceipt>(receipt));
                kernel.RecordFindingEvidenceOutcome(goalId, taskId, stableId, outcome, receipt);
            });

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(shouldRun ? 1 : 0, runs);
        if (shouldRun)
            Assert.Empty(preTesterAttachments);
        else
        {
            var attached = Assert.Single(preTesterAttachments);
            Assert.Equal(CandidateSha, attached.CandidateSha);
            Assert.Equal(requestedClass, Assert.Single(attached.Request.Selections).TestClass);
        }
    }

    [Fact]
    public void CoveredReviewerRequestStillDefersToWritableBlocker()
    {
        var (kernel, goal) = SoftwareGoal();
        var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
        var tester = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
            PassVerification(kernel, goal, task);
        kernel.RecordFindingEvidenceRun(goal.Id, tester.Id,
            PreTesterEvidenceIndexLines.FormatMarker(new PreTesterEvidenceEntry(
                "green", CandidateSha, "pre-tester-receipt",
                ["Infrastructure.Tests:CoveredTests"], [], null, [])));
        FailReviewerNeedsWork(kernel, goal, reviewer, "focused evidence requested; source defect",
            findings:
            [
                EvidenceFindingWithRequest("focused evidence requested", classes: ["CoveredTests"]),
                new ReviewFinding("source-defect", ReviewFindingState.Open,
                    new ReviewFindingLocation("src/Test.cs", "Run"), "source defect",
                    FindingSeverity.Blocking, FindingCategory.Correctness)
            ]);
        TaskId? retriedTaskId = null;
        var attachments = 0;
        var driver = MakeDriver(
            getPreReviewEvidenceContext: _ => NoPreReviewContext(CandidateSha),
            retryTask: (goalId, taskId, message) =>
            {
                retriedTaskId = taskId;
                return kernel.RetryTask(goalId, taskId, message);
            },
            recordFindingEvidenceOutcome: (goalId, taskId, stableId, outcome, receipt) =>
            {
                if (outcome.DecisionReason == "pre-tester-covered") attachments++;
                kernel.RecordFindingEvidenceOutcome(goalId, taskId, stableId, outcome, receipt);
            },
            recordFindingEvidenceSuppressed: (goalId, taskId, sha, blockerIds, requestId, owner, reason, identity) =>
                kernel.RecordFindingEvidenceSuppressed(
                    goalId, taskId, sha, blockerIds, requestId, owner, reason, identity));

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(developer.Id, retriedTaskId);
        Assert.Equal(0, attachments);
        Assert.Contains(goal.Timeline, evt => evt.Kind == ProgressKind.FindingEvidenceSuppressed);
    }
}
