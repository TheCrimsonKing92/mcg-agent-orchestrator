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
    [InlineData("CoveredTests", false)]
    [InlineData("UncoveredTests", true)]
    public void PreTesterBudgetOnlyCapsSelectionsCoveredByItsReceipt(string requestedClass, bool shouldRun)
    {
        var (kernel, goal) = SoftwareGoal();
        var tester = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
            PassVerification(kernel, goal, task);
        kernel.RecordFindingEvidenceRun(goal.Id, tester.Id,
            PreTesterEvidenceIndexLines.FormatMarker(new PreTesterEvidenceEntry(
                "green", CandidateSha, "pre-tester-receipt",
                ["Infrastructure.Tests:CoveredTests"], ["UncoveredTests"], null, [])));
        FailReviewerNeedsWork(kernel, goal, reviewer, "focused evidence requested",
            findings: [EvidenceFindingWithRequest("focused evidence requested",
                category: FindingCategory.TestEvidence, classes: [requestedClass])]);

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
}
