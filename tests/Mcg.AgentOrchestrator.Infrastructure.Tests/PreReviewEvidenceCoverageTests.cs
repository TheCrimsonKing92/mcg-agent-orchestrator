using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

using static ConductorDriverTests;

[Xunit.Collection("IsolatedProcessSpawning")]
public sealed class PreReviewEvidenceCoverageTests
{
    [Xunit.Fact]
    public void SplitProjectChecksCoverOneSelectedItemAndAllowReviewerDispatch()
    {
        const string selection = "Infrastructure.Tests: (FullyQualifiedName~CoreSplitTests|FullyQualifiedName~InfrastructureSplitTests)";
        var context = Context(selection);
        var evidence = Evidence(context,
            Check("reviewer focused evidence: Core.Tests FullyQualifiedName~CoreSplitTests"),
            Check("reviewer focused evidence: Infrastructure.Tests FullyQualifiedName~InfrastructureSplitTests"));

        Assert.True(PreReviewEvidenceReceipts.ValidateCoverage(context, evidence, out var failure), failure);
        Assert.Equal(string.Empty, failure);

        var (kernel, goal) = SoftwareGoal();
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.TakeWhile(task => task.Id != reviewer.Id))
        {
            PassVerification(kernel, goal, task);
        }

        var dispatches = 0;
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            getPreReviewEvidenceContext: _ => context,
            runFocusedEvidence: (_, _) => evidence,
            recordPreReviewEvidence: (goalId, taskId, receipt) =>
                kernel.RecordPreReviewEvidence(goalId, taskId, receipt),
            dispatchAndStart: _ =>
            {
                dispatches++;
                return DispatchStartOutcome.Started();
            });

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(1, dispatches);
        Assert.Equal(PreReviewEvidenceDisposition.Green, reviewer.PreReviewEvidenceReceipt?.Disposition);
        Assert.All(reviewer.PreReviewEvidenceReceipt!.Checks,
            check => Assert.Equal(selection, check.Command));
    }

    [Xunit.Fact]
    public void MissingSelectedClassNamesItemAndClass()
    {
        const string selection = "Infrastructure.Tests: FullyQualifiedName~RanTests|FullyQualifiedName~MissingTests";
        var context = Context(selection);
        var evidence = Evidence(context, Check("reviewer focused evidence: Infrastructure.Tests FullyQualifiedName~RanTests"));

        Assert.False(PreReviewEvidenceReceipts.ValidateCoverage(context, evidence, out var failure));
        Assert.Contains(selection, failure, StringComparison.Ordinal);
        Assert.Contains("MissingTests", failure, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void TokenlessCheckDoesNotCoverSelectedClass()
    {
        const string selection = "Infrastructure.Tests: FullyQualifiedName~SelectedTests";
        var context = Context(selection);
        var evidence = Evidence(context, Check("reviewer focused evidence: Infrastructure.Tests"));

        Assert.False(PreReviewEvidenceReceipts.ValidateCoverage(context, evidence, out var failure));
        Assert.Contains(selection, failure, StringComparison.Ordinal);
        Assert.Contains("SelectedTests", failure, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void FailedChecksContributeCoverageAndExtraneousChecksAreAdvisory()
    {
        const string selection = "Infrastructure.Tests: FullyQualifiedName~SelectedTests";
        var context = Context(selection);
        var evidence = Evidence(context,
            new AcceptanceCheckResult("reviewer focused evidence: Infrastructure.Tests FullyQualifiedName~SelectedTests", false, 1, null),
            Check("reviewer focused evidence: Core.Tests FullyQualifiedName~OtherTests"));

        Assert.True(PreReviewEvidenceReceipts.ValidateCoverage(context, evidence, out var failure), failure);

        var (kernel, goal) = SoftwareGoal();
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        var receipt = PreReviewEvidenceReceipts.Record(
            (_, _, _) => { }, goal, reviewer, context, 1, PreReviewEvidenceDisposition.Red,
            evidence.Checks, [], null);
        Assert.Contains(receipt.Advisories ?? [], advisory =>
            advisory.Contains("OtherTests", StringComparison.Ordinal));
    }

    private static PreReviewEvidenceContext Context(string selection) =>
        new("coverage-sha", [selection], selection, "Focused class coverage.", false, false);

    private static FocusedEvidenceRunResult Evidence(
        PreReviewEvidenceContext context,
        params AcceptanceCheckResult[] checks) =>
        new(context.FocusedRequest!, true, true, "focused evidence", checks);

    private static AcceptanceCheckResult Check(string name) => new(name, true, 0, null);
}
