using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class AutoReviewRetryConvergenceBriefBuilderTests
{
    [Xunit.Fact(DisplayName = "AutoReviewRetryConvergenceBriefBuilder_builds_deduped_union_from_all_reviewer_tester_rounds")]
    public void BuildConvergenceBriefBuildsDedupedUnionFromAllReviewerTesterRounds()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = GoalLifecycleCommands.CreateAndActivateGoal(
            kernel,
            AgentCatalog.Default().Agents,
            "Review retry convergence");
        var developer = goal.Tasks.Single(t => t.RequiredRole == AgentRole.Developer);
        var tester = goal.Tasks.Single(t => t.RequiredRole == AgentRole.Tester);
        var reviewer = goal.Tasks.Single(t => t.RequiredRole == AgentRole.Reviewer);
        var firstTesterFinding = "P1 src/Foo.cs:42 round 1 leaves the retry feedback path unwired.";
        var latestTesterFinding = "p1 src/Foo.cs:42 retry-feedback path remains unwired.";
        var uniqueReviewerFinding = "P2 tests/FooTests.cs:17 missing assertion for the convergence mandate.";
        var uniqueTesterFinding = "src/Bar.cs:9 still skips the focused receipt quote.";

        kernel.ReportTaskProgress(goal.Id, tester.Id, WorkTaskStatus.Failed, $"WORKER_RESULT reported blocker: {firstTesterFinding}");
        kernel.RetryTask(goal.Id, developer.Id, "auto-review-retry round 1: retry upstream Developer");
        kernel.ReportTaskProgress(goal.Id, reviewer.Id, WorkTaskStatus.Failed, $"Reviewer WORKER_RESULT reported blocker: {uniqueReviewerFinding}");
        kernel.RetryTask(goal.Id, developer.Id, "auto-review-retry round 2: retry upstream Developer");
        kernel.ReportTaskProgress(goal.Id, tester.Id, WorkTaskStatus.Failed, $"WORKER_RESULT reported blocker: {latestTesterFinding}");
        kernel.ReportTaskProgress(goal.Id, tester.Id, WorkTaskStatus.Failed, $"WORKER_RESULT reported blocker: {uniqueTesterFinding}");

        var brief = AutoReviewRetryConvergenceBriefBuilder.BuildConvergenceBrief(
            goal,
            tester,
            latestTesterFinding,
            round: 3,
            outputArtifact: @"C:\tmp\tester.out.log");

        Xunit.Assert.Contains(AutoReviewRetryConvergenceBriefBuilder.AcceptedShapePreamble, brief);
        Xunit.Assert.Contains(latestTesterFinding, brief);
        Xunit.Assert.DoesNotContain(firstTesterFinding, brief);
        Xunit.Assert.Equal(1, CountOccurrences(brief, "retry-feedback path"));
        Xunit.Assert.Contains("seen in rounds 1, 3", brief);
        Xunit.Assert.Contains(uniqueReviewerFinding, brief);
        Xunit.Assert.Contains(uniqueTesterFinding, brief);
        Xunit.Assert.Contains(AutoReviewRetryConvergenceBriefBuilder.RerunMandate, brief);
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }
}
