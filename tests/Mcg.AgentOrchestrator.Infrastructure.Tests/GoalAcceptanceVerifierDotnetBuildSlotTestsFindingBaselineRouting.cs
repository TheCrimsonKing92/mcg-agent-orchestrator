using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

using static ConductorDriverTests;

[Xunit.Collection(TestCollections.JobAccounting)]
public sealed class GoalAcceptanceVerifierDotnetBuildSlotTestsFindingBaselineRouting
{
    [Xunit.Fact]
    public async Task MixedCandidateRedRetriesDeveloperWithoutBaselineEscalation()
    {
        var outcome = await RunFindingAsync(zeroBaselineTests: false);

        Assert.Equal(outcome.DeveloperId, outcome.RetriedId);
        Assert.Null(outcome.Escalation);
        Assert.DoesNotContain(outcome.Timeline, message =>
            message.Contains("disposition=baseline-arm-absent", StringComparison.Ordinal));
        Assert.DoesNotContain(outcome.Timeline, message =>
            message.Contains("disposition=candidate-rerun-requested", StringComparison.Ordinal));
    }

    [Xunit.Fact]
    public async Task ZeroBaselineTestsStillEscalateAfterCandidateRerun()
    {
        var outcome = await RunFindingAsync(zeroBaselineTests: true);

        Assert.Null(outcome.RetriedId);
        Assert.Contains("Baseline execution failure", outcome.Escalation, StringComparison.Ordinal);
        Assert.Contains("No worker was dispatched", outcome.Escalation, StringComparison.Ordinal);
        Assert.Single(outcome.Timeline.Where(message =>
            message.Contains("disposition=baseline-arm-absent", StringComparison.Ordinal)));
        Assert.Single(outcome.Timeline.Where(message =>
            message.Contains("disposition=candidate-rerun-requested", StringComparison.Ordinal)));
        Assert.Single(outcome.Timeline.Where(message =>
            message.Contains("disposition=candidate-rerun-red", StringComparison.Ordinal)));
    }

    private static async Task<(TaskId DeveloperId, TaskId? RetriedId, string? Escalation,
        string[] Timeline)> RunFindingAsync(bool zeroBaselineTests)
    {
        using var fixture = FindingBaselineProbeFixture.Create(zeroBaselineTests);
        var dualArm = await fixture.RunAsync(zeroBaselineTests
            ? "Core.Tests: AddedProbeTests; Core.Tests: PreExistingProbeTests; Core.Tests: OutsideProbeTests"
            : "Core.Tests: AddedProbeTests; Core.Tests: PreExistingProbeTests");
        var candidateOnly = dualArm with
        {
            Arms = dualArm.Arms!.Where(arm => arm.Arm == FindingEvidenceArm.Candidate).ToArray()
        };
        var candidateSha = GitCli.Run(fixture.Root, "rev-parse", "HEAD").Output.Trim();
        var (kernel, goal) = SoftwareGoal();
        var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task, hasCommittedChanges: task == developer);
        }

        FailReviewerNeedsWork(kernel, goal, reviewer, "mixed candidate RED",
            findings: [EvidenceFindingWithRequest("Added and existing tests are RED", id: "mixed-red",
                project: "Core.Tests",
                classes: zeroBaselineTests
                    ? ["AddedProbeTests", "PreExistingProbeTests", "OutsideProbeTests"]
                    : ["AddedProbeTests", "PreExistingProbeTests"])]);
        var focusedRuns = 0;
        TaskId? retried = null;
        string? escalation = null;
        var driver = MakeDriver(
            executionDirectory: fixture.Root,
            getLandingFileScopes: _ => zeroBaselineTests
                ? [fixture.AddedTestPath, fixture.FeaturePath, fixture.ExistingTestPath]
                : [fixture.AddedTestPath, fixture.FeaturePath],
            getPreReviewEvidenceContext: _ => NoPreReviewContext(candidateSha),
            runFocusedEvidence: (_, _) => ++focusedRuns == 2 ? dualArm : candidateOnly,
            retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
            {
                retried = taskId;
                return kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind);
            },
            recordFindingEvidenceRequest: (goalId, taskId, message) =>
                kernel.RecordFindingEvidenceRequest(goalId, taskId, message),
            recordFindingEvidenceOutcome: (goalId, taskId, stableId, outcome, receipt) =>
                kernel.RecordFindingEvidenceOutcome(goalId, taskId, stableId, outcome, receipt),
            writeEscalation: (_, _, message) => escalation = message);

        for (var tick = 0; tick < 4 && retried is null && escalation is null; tick++)
        {
            driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);
        }

        return (developer.Id, retried, escalation,
            goal.Timeline.Select(evt => evt.Message).ToArray());
    }
}
