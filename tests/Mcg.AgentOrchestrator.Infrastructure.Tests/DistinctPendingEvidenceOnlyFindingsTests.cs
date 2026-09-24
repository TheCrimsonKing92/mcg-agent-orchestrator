using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using static ConductorDriverTests;

[Xunit.Collection("IsolatedProcessSpawning")]
public sealed class DistinctPendingEvidenceOnlyFindingsTests
{
    [Xunit.Fact]
    public void EachPendingFindingBlocksTheOthersRequestAtTheUnchangedCandidate()
    {
        const string candidate = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var (kernel, goal) = SoftwareGoal();
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
            PassVerification(kernel, goal, task);
        FailReviewerNeedsWork(kernel, goal, reviewer, "two distinct pending receipts",
            findings:
            [
                EvidenceFindingWithRequest("first receipt missing", "first-request",
                    FindingCategory.SpecCompliance, classes: ["ConductorDriverTests"]),
                EvidenceFindingWithRequest("second receipt missing", "second-request",
                    FindingCategory.SpecCompliance, classes: ["EvidenceOnlyNoChangeAcceptanceTests"])
            ], reviewedCommit: candidate);

        var focusedRuns = 0;
        var driver = MakeDriver(
            getPreReviewEvidenceContext: _ => NoPreReviewContext(candidate),
            runFocusedEvidence: (_, request) =>
            {
                focusedRuns++;
                return new FocusedEvidenceRunResult(request, true, true, "focused class passed", []);
            },
            retryTask: (goalId, taskId, message) => kernel.RetryTask(goalId, taskId, message),
            recordFindingEvidenceSuppressed: (goalId, taskId, candidateSha, blockerIds, requestId, owner, reason, identity) =>
                kernel.RecordFindingEvidenceSuppressed(goalId, taskId, candidateSha,
                    blockerIds, requestId, owner, reason, identity));

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Xunit.Assert.Equal(0, focusedRuns);
        var suppressions = goal.Timeline.Where(evt =>
            evt.Kind == ProgressKind.FindingEvidenceSuppressed &&
            evt.Message.Contains("blocker_ids=first-request,second-request", StringComparison.Ordinal)).ToArray();
        Xunit.Assert.Equal(2, suppressions.Length);
        Xunit.Assert.NotEqual(suppressions[0].Message, suppressions[1].Message);
    }
}
