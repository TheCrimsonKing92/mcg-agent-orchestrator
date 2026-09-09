using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

using static ConductorDriverTests;

[Xunit.Collection("IsolatedProcessSpawning")]
public sealed class ConductorDriverTestsPreReviewReceiptReuse
{
    [Xunit.Theory]
    [Xunit.InlineData("empty")]
    [Xunit.InlineData("wrong-command")]
    [Xunit.InlineData("nonzero-exit")]
    [Xunit.InlineData("unknown-exit")]
    [Xunit.InlineData("failed-identity")]
    [Xunit.InlineData("wrong-count")]
    public void MalformedGreenReceiptCannotSuppressExecution(string shape)
    {
        var (kernel, goal) = SoftwareGoal();
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.TakeWhile(task => task.Id != reviewer.Id))
            PassVerification(kernel, goal, task);
        const string selection = "Infrastructure.Tests: FullyQualifiedName~GoalWorktreeTests";
        var checks = shape == "empty" ? Array.Empty<PreReviewEvidenceCheckReceipt>() :
            [new PreReviewEvidenceCheckReceipt("control", shape == "wrong-command" ? "different" : selection,
                true, shape == "unknown-exit" ? null : shape == "nonzero-exit" ? 1 : 0)];
        kernel.RecordPreReviewEvidence(goal.Id, reviewer.Id, new PreReviewEvidenceReceipt(
            goal.Id.Value, 1, "same-sha", [selection], PreReviewEvidenceDisposition.Green,
            shape == "wrong-count" ? 5 : checks.Length, 0, checks,
            shape == "failed-identity" ? ["Control.Failed"] : [], "malformed fixture", "fixture://bad", DateTimeOffset.UtcNow));
        kernel = AgentOrchestratorKernel.FromSnapshot(kernel.ExportSnapshot());
        goal = kernel.GetGoal(goal.Id);
        var executions = 0;
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            getPreReviewEvidenceContext: _ => new PreReviewEvidenceContext("same-sha", [selection], selection, "control", false, false),
            runFocusedEvidence: (_, request) => { executions++; return PassingPreReviewEvidence(request); },
            recordPreReviewEvidence: (goalId, taskId, receipt) => kernel.RecordPreReviewEvidence(goalId, taskId, receipt));
        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);
        Assert.Equal(1, executions);
    }

    [Xunit.Fact]
    public void FreshIdenticalGreenAfterRedBecomesAuthoritative()
    {
        var (kernel, goal) = SoftwareGoal();
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        const string selection = "Infrastructure.Tests: FullyQualifiedName~GoalWorktreeTests";
        var green = new PreReviewEvidenceReceipt(goal.Id.Value, 1, "same-sha", [selection], PreReviewEvidenceDisposition.Green,
            1, 0, [new PreReviewEvidenceCheckReceipt("control", selection, true, 0)], [], "control", "fixture://green", DateTimeOffset.UtcNow);
        kernel.RecordPreReviewEvidence(goal.Id, reviewer.Id, green);
        kernel.RecordPreReviewEvidence(goal.Id, reviewer.Id, green with { Disposition = PreReviewEvidenceDisposition.Red,
            FailedCheckCount = 1, PassedCheckCount = 0, Checks = [new("control", selection, false, 1)] });
        kernel.RecordPreReviewEvidence(goal.Id, reviewer.Id, green with { RecordedAt = green.RecordedAt.AddSeconds(1) });
        Assert.Equal(3, reviewer.PreReviewEvidenceHistory.Count);
        Assert.Equal(PreReviewEvidenceDisposition.Green, reviewer.PreReviewEvidenceReceipt!.Disposition);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_pre_review_reuses_current_candidate_constituent_green_coverage")]
    public void ConductorDriverPreReviewReusesCurrentCandidateConstituentGreenCoverage()
    {
        var (kernel, goal) = SoftwareGoal();
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.TakeWhile(task => task.Id != reviewer.Id))
        {
            PassVerification(kernel, goal, task);
        }

        const string first = "Infrastructure.Tests: FullyQualifiedName~GoalWorktreeTests";
        const string second = "Infrastructure.Tests: FullyQualifiedName~SqliteTooling";
        PreReviewEvidenceReceipt Green(string selection, string pointer) => new(
            goal.Id.Value,
            1,
            "same-sha",
            [selection],
            PreReviewEvidenceDisposition.Green,
            PassedCheckCount: 1,
            FailedCheckCount: 0,
            Checks: [new PreReviewEvidenceCheckReceipt(selection, selection, true, 0)],
            FailingTestIdentities: [],
            MappingReason: "seed",
            EvidencePointer: pointer,
            RecordedAt: DateTimeOffset.UtcNow);
        kernel.RecordPreReviewEvidence(goal.Id, reviewer.Id, Green(first, "C:\\receipts\\first"));
        kernel.RecordPreReviewEvidence(goal.Id, reviewer.Id, Green(second, "C:\\receipts\\second"));

        var evidenceRuns = 0;
        var dispatches = 0;
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            getPreReviewEvidenceContext: _ => new PreReviewEvidenceContext(
                "same-sha",
                [first, second],
                $"{first}; {second}",
                "combined focused request",
                NoApplicableTests: false,
                MappingNeedsInput: false),
            runFocusedEvidence: (_, _) =>
            {
                evidenceRuns++;
                throw new InvalidOperationException("covered evidence must not execute again");
            },
            recordPreReviewEvidence: (goalId, taskId, receipt) =>
                kernel.RecordPreReviewEvidence(goalId, taskId, receipt),
            dispatchAndStart: _ =>
            {
                dispatches++;
                return DispatchStartOutcome.Started();
            });

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(0, evidenceRuns);
        Assert.Equal(1, dispatches);
        Assert.Equal([first, second], reviewer.PreReviewEvidenceReceipt?.SelectedFocusedTests);
        Assert.Contains("reused-current-candidate", reviewer.PreReviewEvidenceReceipt?.Advisories ?? []);
        Assert.Equal(2, reviewer.PreReviewEvidenceAttemptCount);
        Assert.Equal(3, reviewer.PreReviewEvidenceHistory.Count);
        var restored = AgentOrchestratorKernel.FromSnapshot(kernel.ExportSnapshot());
        var restoredReviewer = restored.GetTask(goal.Id, reviewer.Id);
        Assert.Equal(3, restoredReviewer.PreReviewEvidenceHistory.Count);
        Assert.Equal(2, restoredReviewer.PreReviewEvidenceAttemptCount);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_pre_review_newer_red_at_same_candidate_does_not_reuse_older_green")]
    public void ConductorDriverPreReviewNewerRedAtSameCandidateDoesNotReuseOlderGreen()
    {
        var (kernel, goal) = SoftwareGoal();
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.TakeWhile(task => task.Id != reviewer.Id))
        {
            PassVerification(kernel, goal, task);
        }

        const string selection = "Infrastructure.Tests: FullyQualifiedName~GoalWorktreeTests";
        kernel.RecordPreReviewEvidence(goal.Id, reviewer.Id, new PreReviewEvidenceReceipt(
            goal.Id.Value,
            1,
            "same-sha",
            [selection],
            PreReviewEvidenceDisposition.Green,
            PassedCheckCount: 1,
            FailedCheckCount: 0,
            Checks: [new PreReviewEvidenceCheckReceipt(selection, selection, true, 0)],
            FailingTestIdentities: [],
            MappingReason: "seed green",
            EvidencePointer: "C:\\receipts\\green",
            RecordedAt: DateTimeOffset.UtcNow.AddMinutes(-1)));
        kernel.RecordPreReviewEvidence(goal.Id, reviewer.Id, new PreReviewEvidenceReceipt(
            goal.Id.Value,
            2,
            "same-sha",
            [selection],
            PreReviewEvidenceDisposition.Red,
            PassedCheckCount: 0,
            FailedCheckCount: 1,
            Checks: [new PreReviewEvidenceCheckReceipt(selection, selection, false, 1)],
            FailingTestIdentities: ["GoalWorktreeTests.RedirectedProcessTimeoutNamesTheLastReportedPhase"],
            MappingReason: "candidate red",
            EvidencePointer: "C:\\receipts\\red",
            RecordedAt: DateTimeOffset.UtcNow));

        var evidenceRuns = 0;
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            getPreReviewEvidenceContext: _ => new PreReviewEvidenceContext(
                "same-sha", [selection], selection, "same focused request", false, false),
            runFocusedEvidence: (_, request) =>
            {
                evidenceRuns++;
                return PassingPreReviewEvidence(request);
            },
            recordPreReviewEvidence: (goalId, taskId, receipt) =>
                kernel.RecordPreReviewEvidence(goalId, taskId, receipt));

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(1, evidenceRuns);
        Assert.Equal(PreReviewEvidenceDisposition.Green, reviewer.PreReviewEvidenceReceipt?.Disposition);
        Assert.DoesNotContain("reused-current-candidate", reviewer.PreReviewEvidenceReceipt?.Advisories ?? []);
    }

}
