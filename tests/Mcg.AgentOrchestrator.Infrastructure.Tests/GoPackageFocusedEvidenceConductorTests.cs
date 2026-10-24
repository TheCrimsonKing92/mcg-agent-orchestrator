using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using static ConductorDriverTests;

// Parallel-safe: this harness uses in-memory goals and stubbed execution only.
public sealed class GoPackageFocusedEvidenceConductorTests
{
    [Fact]
    public void GoPackageRequestIsHonoured()
    {
        var (requests, outcome) = RunRequest(
            AcceptanceGateEngineSettings.Parse(GoPackageFocusedEvidenceTests.Manifest), "TestMissingColumn");

        Assert.Equal("internal/drift:TestMissingColumn", Assert.Single(requests));
        Assert.True(outcome.Honoured);
        Assert.NotEqual(FindingEvidenceNotHonouredReason.UnsupportedProject, outcome.Reason);
        Assert.NotEqual(FindingEvidenceNotHonouredReason.UnparseableSelection, outcome.Reason);
    }

    [Fact]
    public void UnscopableSyntaxRunsAndDotnetSettingsStillRefuse()
    {
        var (requests, outcome) = RunRequest(
            AcceptanceGateEngineSettings.Parse(GoPackageFocusedEvidenceTests.Manifest), "TestMissingColumn/empty_input");

        Assert.Equal("internal/drift:TestMissingColumn/empty_input", Assert.Single(requests));
        Assert.True(outcome.Honoured);
        Assert.NotEqual(FindingEvidenceNotHonouredReason.UnparseableSelection, outcome.Reason);

        var dotnet = AcceptanceGateEngineSettings.Parse("""
            {"checks":[{"type":"dotnet-test","project":"tests/Sample.Tests/Sample.Tests.csproj"}]}
            """);
        var (refusedRequests, refused) = RunRequest(dotnet, "TestMissingColumn");
        Assert.Empty(refusedRequests);
        Assert.False(refused.Honoured);
        Assert.Equal(FindingEvidenceNotHonouredReason.UnsupportedProject, refused.Reason);
    }

    private static (IReadOnlyList<string> Requests, FindingEvidenceOutcome Outcome) RunRequest(
        AcceptanceGateEngineSettings settings, string testClass)
    {
        var (kernel, goal) = SoftwareGoal();
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
            PassVerification(kernel, goal, task);
        var finding = EvidenceFindingWithRequest("Go package needs test evidence.",
            id: "go-package", project: "internal/drift", classes: [testClass]);
        FailReviewerNeedsWork(kernel, goal, reviewer, "Go package request", findings: [finding]);
        var requests = new List<string>();
        var driver = MakeDriver(
            getPreReviewEvidenceContext: _ => NoPreReviewContext("abc1234"),
            getFindingEvidenceEngineSettings: _ => settings,
            runFocusedEvidence: (_, request) =>
            {
                requests.Add(request);
                return new FocusedEvidenceRunResult(request, true, true, "Go focused check passed", []);
            },
            retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
                kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind),
            recordFindingEvidenceOutcome: (goalId, taskId, stableId, outcome, receipt) =>
                kernel.RecordFindingEvidenceOutcome(goalId, taskId, stableId, outcome, receipt));

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        var outcome = reviewer.VerificationHistory.Last().MergedReviewFindings!
            .Single(item => item.StableId == "go-package").EvidenceOutcome;
        Assert.NotNull(outcome);
        return (requests, outcome);
    }
}
