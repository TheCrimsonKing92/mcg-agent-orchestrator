using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using static ConductorDriverTests;

// Parallel-safe: each scenario owns its in-memory kernel; execution and dispatch are fakes.
public sealed class InheritedRedRoutingScenarioTests
{
    private const string CandidateSha = "abc1234";
    private const string Alpha = "Fixture.AlphaTests.FailsOnCandidate";

    [Fact]
    public void DisjointBaselineRedRoutesDeveloperThroughAdvance()
    {
        var baselineFailures = BaselineFailures();
        var (goal, developer, result, retries, dispatches, escalations) = Advance(baselineFailures);

        Assert.IsType<ConductorAdvanceOutcome.Executed>(result.Outcome);
        var retry = Assert.Single(retries);
        Assert.Equal(developer.Id, retry.Task);
        Assert.StartsWith("ACTIONABLE_CANDIDATE_RED", retry.Message, StringComparison.Ordinal);
        Assert.Contains($"failing_tests={Alpha}; baseline_failures={string.Join(',', baselineFailures)}.",
            retry.Message, StringComparison.Ordinal);
        Assert.Equal(1, dispatches);
        Assert.Empty(escalations);
        Assert.DoesNotContain(goal.Timeline, item =>
            item.Message.Contains("inherited from a RED Baseline arm", StringComparison.Ordinal));
    }

    [Fact]
    public void SameTestsBaselineRedEscalatesThroughAdvance()
    {
        var baselineFailures = BaselineFailures().Append(Alpha).ToArray();
        var (_, _, result, retries, dispatches, escalations) = Advance(baselineFailures);

        Assert.IsType<ConductorAdvanceOutcome.Escalated>(result.Outcome);
        Assert.Empty(retries);
        Assert.Equal(0, dispatches);
        var escalation = Assert.Single(escalations);
        Assert.Contains("is inherited from a RED Baseline arm", escalation, StringComparison.Ordinal);
        Assert.Contains($"candidate_failures={Alpha}; baseline_failures={string.Join(',', baselineFailures)}; " +
                        "candidate_only_failures=none", escalation, StringComparison.Ordinal);
    }

    private static string[] BaselineFailures() => Enumerable.Range(1, 6)
        .Select(row => $"Fixture.BetaTests.FailsOnBaseline(row: {row})").ToArray();

    private static (Goal Goal, TaskSpec Developer, ConductorAdvanceResult Result,
        List<(TaskId Task, string Message)> Retries, int Dispatches, List<string> Escalations)
        Advance(string[] baselineFailures)
    {
        var (kernel, goal) = SoftwareGoal();
        var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task, hasCommittedChanges: task == developer);
        }
        FailReviewerNeedsWork(kernel, goal, reviewer, "Candidate test needs evidence",
            findings: [EvidenceFindingWithRequest("Developer-owned fixture is RED", id: "fixture-red",
                classes: ["AlphaTests"])]);

        var retries = new List<(TaskId Task, string Message)>();
        var escalations = new List<string>();
        var dispatches = 0;
        var focusedRuns = 0;
        var driver = MakeDriver(
            getPreReviewEvidenceContext: _ => NoPreReviewContext(CandidateSha),
            runFocusedEvidence: (_, request) =>
            {
                focusedRuns++;
                var red = CandidateRedFindingEvidence(request, CandidateSha, Alpha);
                var baseline = red.Arms!.Single(arm => arm.Arm == FindingEvidenceArm.Baseline);
                var check = baseline.Checks.Single() with
                {
                    Passed = false, ExitCode = 1, FailingTestIdentities = baselineFailures
                };
                return red with
                {
                    Arms = red.Arms.Select(arm => arm.Arm == FindingEvidenceArm.Baseline
                        ? arm with { Disposition = FindingEvidenceArmDisposition.Red, Passed = false, Checks = [check] }
                        : arm).ToArray()
                };
            },
            dispatchAndStart: _ => { dispatches++; return DispatchStartOutcome.Started(); },
            retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
            {
                retries.Add((taskId, message));
                return kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind);
            },
            writeEscalation: (_, _, message) => escalations.Add(message),
            recordFindingEvidenceRequest: (goalId, taskId, message) =>
                kernel.RecordFindingEvidenceRequest(goalId, taskId, message),
            recordFindingEvidenceRun: (goalId, taskId, message) =>
                kernel.RecordFindingEvidenceRun(goalId, taskId, message),
            recordFindingEvidenceOutcome: (goalId, taskId, stableId, outcome, receipt) =>
                kernel.RecordFindingEvidenceOutcome(goalId, taskId, stableId, outcome, receipt));

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);
        Assert.Equal(1, focusedRuns);
        var receipt = Assert.Single(reviewer.VerificationHistory.Last().FindingEvidenceReceipts!);
        Assert.Equal(new[] { Alpha }, receipt.Arms.Single(arm => arm.Arm == FindingEvidenceArm.Candidate).FailingTestIdentities);
        Assert.Equal(baselineFailures, receipt.Arms.Single(arm => arm.Arm == FindingEvidenceArm.Baseline).FailingTestIdentities);
        return (goal, developer, result, retries, dispatches, escalations);
    }
}
