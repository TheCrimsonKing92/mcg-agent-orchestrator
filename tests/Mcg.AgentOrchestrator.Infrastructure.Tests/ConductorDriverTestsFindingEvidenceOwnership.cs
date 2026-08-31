using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

using static ConductorDriverTests;

[Xunit.Collection("IsolatedProcessSpawning")]
public sealed class ConductorDriverTestsFindingEvidenceOwnership
{
    [Xunit.Theory]
    [Xunit.InlineData("operator-owned evidence remains outstanding", true, null)]
    [Xunit.InlineData("tester must rerun the focused test receipt", false, AgentRole.Tester)]
    public void UnspecifiedOwnership_UsesLegacyRouteWithoutFalseSuppression(
        string blockerProse,
        bool expectEscalation,
        AgentRole? expectedRetryRole)
    {
        var (kernel, goal) = SoftwareGoal();
        var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task);
        }

        FailReviewerNeedsWork(
            kernel,
            goal,
            reviewer,
            blockerProse,
            findings:
            [
                EvidenceFindingWithRequest(
                    blockerProse,
                    id: "legacy-owned-request",
                    category: FindingCategory.Unspecified,
                    classes: ["ConductorDriverTests"])
            ]);
        var focusedRuns = 0;
        AgentRole? retriedRole = null;
        string? escalation = null;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            getPreReviewEvidenceContext: _ => NoPreReviewContext("abc1234"),
            runFocusedEvidence: (_, request) =>
            {
                focusedRuns++;
                return new FocusedEvidenceRunResult(request, true, true, "focused request passed", []);
            },
            retryTask: (goalId, taskId, message) =>
            {
                retriedRole = goal.Tasks.Single(task => task.Id == taskId).RequiredRole;
                return kernel.RetryTask(goalId, taskId, message);
            },
            recordFindingEvidenceSuppressed: (goalId, taskId, candidateSha, blockerIds, requestId, owner, reason, identity) =>
                kernel.RecordFindingEvidenceSuppressed(
                    goalId, taskId, candidateSha, blockerIds, requestId, owner, reason, identity),
            writeEscalation: (_, _, message) => escalation = message);

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(0, focusedRuns);
        Assert.Equal(expectedRetryRole, retriedRole);
        Assert.Equal(expectEscalation, escalation is not null);
        Assert.DoesNotContain(goal.Timeline, evt => evt.Kind == ProgressKind.FindingEvidenceSuppressed);
        Assert.DoesNotContain(
            "finding-evidence-suppressed",
            kernel.BuildTaskBrief(goal.Id, developer.Id).Content,
            StringComparison.Ordinal);
    }
}
