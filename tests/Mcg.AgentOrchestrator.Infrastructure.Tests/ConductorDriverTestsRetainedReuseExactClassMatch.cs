using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

using static ConductorDriverTests;

[Xunit.Collection("IsolatedProcessSpawning")]
public sealed class ConductorDriverTestsRetainedReuseExactClassMatch
{
    [Xunit.Theory]
    [Xunit.InlineData("DeferredAlphaTestsFirst", 2)]
    [Xunit.InlineData("DeferredAlphaTests", 1)]
    public void RetainedReceiptRequiresExactExecutedClass(string executedClass, int expectedRuns)
    {
        const string candidateSha = "abc1234";
        var artifactRoot = InfrastructureTestSupport.CreateTempDirectory();
        try
        {
            var (kernel, goal) = SoftwareGoal();
            var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
            foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
                PassVerification(kernel, goal, task);

            var finding = EvidenceFindingWithRequest(
                "The first finding round needs focused evidence.",
                id: "exact-class-reuse", classes: ["DeferredAlphaTests"]);
            FailReviewerNeedsWork(kernel, goal, reviewer, "first finding round", findings: [finding]);
            var focusedRuns = 0;
            var requests = new List<string>();
            var driver = MakeDriver(
                getPreReviewEvidenceContext: _ => NoPreReviewContext(candidateSha),
                runFocusedEvidence: (_, request) =>
                {
                    focusedRuns++;
                    requests.Add(request);
                    return ConductorDriverTestsFindingEvidenceReuse.RetainedEvidenceWithExecutedClasses(
                        artifactRoot, request, candidateSha, [executedClass]);
                },
                dispatchAndStart: _ => DispatchStartOutcome.Started(),
                retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
                    kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind),
                recordFindingEvidenceRequest: (goalId, taskId, message) =>
                    kernel.RecordFindingEvidenceRequest(goalId, taskId, message),
                recordFindingEvidenceOutcome: (goalId, taskId, stableId, outcome, receipt) =>
                    kernel.RecordFindingEvidenceOutcome(goalId, taskId, stableId, outcome, receipt));

            driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);
            var repeatedFinding = finding with
            {
                Description = "A changed finding round still needs focused evidence."
            };
            FailReviewerNeedsWork(kernel, goal, reviewer, "changed finding round", findings: [repeatedFinding]);

            driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

            Xunit.Assert.Equal(expectedRuns, focusedRuns);
            Xunit.Assert.All(requests, request =>
                Xunit.Assert.Equal("Infrastructure.Tests:DeferredAlphaTests", request));
            if (expectedRuns == 1)
                Xunit.Assert.Contains(goal.Timeline, evt =>
                    evt.Message.Contains("finding-evidence disposition=reused-covered-green", StringComparison.Ordinal));
            else
                Xunit.Assert.DoesNotContain(goal.Timeline, evt =>
                    evt.Message.Contains("finding-evidence disposition=reused-covered-green", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(artifactRoot, recursive: true);
        }
    }
}
