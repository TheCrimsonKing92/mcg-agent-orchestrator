using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection("IsolatedProcessSpawning")]
public sealed class ConductorDriverTestsSharedApparatusInvalidation
{
    [Xunit.Fact]
    public void TypedSharedApparatusInvalidation_HoldsCandidateWithoutReopeningWorkers()
    {
        var (kernel, goal) = ConductorDriverTests.SoftwareGoal("Shared apparatus invalidation");
        foreach (var task in goal.Tasks)
        {
            ConductorDriverTests.PassVerification(kernel, goal, task);
        }

        var retryCalls = 0;
        var failure = new AcceptanceCheckResult(
            "infrastructure tests: Alpha",
            false,
            1,
            "typed shared apparatus receipt",
            FailureClassification: AcceptanceFailureClassifications.SharedGateApparatusInvalidated,
            ExecutedTestCount: 1);
        var acceptance = new AcceptanceVerificationSummary(
            false,
            [failure],
            FailedChecks: [failure.Name],
            BranchHeadSha: "candidate-before-loss",
            MainHeadSha: "main-before-loss");
        var driver = ConductorDriverTests.MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            runAcceptanceSummary: _ => acceptance,
            retryTask: (goalId, taskId, message) =>
            {
                retryCalls++;
                return kernel.RetryTask(goalId, taskId, message, RetryCause.Unknown);
            },
            recordCriterionRetryFeedback: kernel.RecordCriterionRetryFeedback,
            recordAcceptanceFailure: (heldGoal, checks, branch, main, attributions, attestation) =>
                kernel.RecordAcceptanceFailure(
                    heldGoal.Id,
                    checks,
                    branch,
                    main,
                    attributions,
                    attestation),
            resolveAcceptanceHeads: _ => ("candidate-before-loss", "main-before-loss"));

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        var held = Xunit.Assert.IsType<ConductorAdvanceOutcome.Held>(result.Outcome);
        Xunit.Assert.Equal(GoalLifecycleState.Verified, held.State);
        Xunit.Assert.Contains("no worker was reopened", held.Reason, StringComparison.Ordinal);
        Xunit.Assert.Equal(0, retryCalls);
        Xunit.Assert.Equal(4, goal.Tasks.Count);
        Xunit.Assert.All(goal.Tasks, task =>
        {
            Xunit.Assert.Equal(WorkTaskStatus.Completed, task.Status);
            Xunit.Assert.Equal(0, task.CriterionRetryCount);
        });
        Xunit.Assert.Equal("candidate-before-loss", goal.LatestAcceptanceFailure?.BranchHeadSha);
        Xunit.Assert.Equal("main-before-loss", goal.LatestAcceptanceFailure?.MainHeadSha);
    }
}
