using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using static ConductorDriverTests;

// Parallel-safe: in-memory goals and injected evidence execution; no process/global state.
public sealed class CrosswalkFocusedEvidenceConductorTests
{
    [Fact]
    public void FileRequest_CrosswalkSettings_IsHonouredAndCanonicalized()
    {
        var settings = AcceptanceGateEngineSettings.Parse(CrosswalkFocusedEvidenceTests.Manifest);
        var (requests, outcome) = RunRequest(settings, ".", CrosswalkFocusedEvidenceTests.File);

        Assert.True(CrosswalkFocusedEvidence.Applies(settings));
        Assert.False(GoPackageFocusedEvidence.Applies(settings));
        Assert.Equal("crosswalk selection:" + CrosswalkFocusedEvidenceTests.File,
            Assert.Single(requests));
        Assert.True(outcome.Honoured);
        Assert.NotEqual(FindingEvidenceNotHonouredReason.UnparseableSelection, outcome.Reason);
        Assert.NotEqual(FindingEvidenceNotHonouredReason.UnsupportedProject, outcome.Reason);
    }

    [Fact]
    public void FileRequest_DotnetSettings_RefusesWithExistingSyntaxReason()
    {
        var settings = AcceptanceGateEngineSettings.Parse("""
            {"checks":[{"type":"dotnet-test","project":"tests/Sample.Tests/Sample.Tests.csproj"}]}
            """);
        var (requests, outcome) = RunRequest(settings, ".", CrosswalkFocusedEvidenceTests.File);

        Assert.False(CrosswalkFocusedEvidence.Applies(settings));
        Assert.Empty(requests);
        Assert.False(outcome.Honoured);
        Assert.Equal(FindingEvidenceNotHonouredReason.UnparseableSelection, outcome.Reason);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GoRequest_WithOrWithoutCrosswalk_KeepsGoRouting(bool withCrosswalk)
    {
        var manifest = """
            {"checks":[
              {"name":"crosswalk selection","type":"crosswalk-test","project":"."},
              {"name":"go test","type":"command","command":"go","arguments":["test","./..."]}
            ]}
            """;
        var settings = AcceptanceGateEngineSettings.Parse(withCrosswalk
            ? manifest : GoPackageFocusedEvidenceTests.Manifest);
        var (requests, outcome) = RunRequest(settings, "internal/drift", "TestMissingColumn");

        Assert.True(GoPackageFocusedEvidence.Applies(settings));
        Assert.False(CrosswalkFocusedEvidence.Applies(settings));
        Assert.Equal("internal/drift:TestMissingColumn", Assert.Single(requests));
        Assert.True(outcome.Honoured);
        var root = InfrastructureTestSupport.CreateTempDirectory();
        try
        {
            Assert.True(GoalAcceptanceVerifier.TryBuildFocusedEvidenceChecks(requests[0], settings,
                root, out var checks, out var coverage, out var rejection), rejection.Detail);
            Assert.True(GoPackageFocusedEvidence.IsEvidenceCheck(Assert.Single(checks)));
            Assert.False(CrosswalkFocusedEvidence.IsEvidenceCheck(checks[0]));
            Assert.Equal("go-declared-check-fallback", coverage.ExecutionReason);
            Assert.Equal("go", checks[0].Command);
            var expectedArguments = withCrosswalk ? new[] { "test", "-v", "./..." }
                : new[] { "test", "-v", "-count=1", "./..." };
            Assert.Equal(expectedArguments, checks[0].Arguments);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static (IReadOnlyList<string> Requests, FindingEvidenceOutcome Outcome) RunRequest(
        AcceptanceGateEngineSettings settings, string project, string testClass)
    {
        var (kernel, goal) = SoftwareGoal();
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
            PassVerification(kernel, goal, task);
        var finding = EvidenceFindingWithRequest("Selected tests need execution evidence.",
            id: "crosswalk-focused", project: project, classes: [testClass]);
        FailReviewerNeedsWork(kernel, goal, reviewer, "Focused evidence request", findings: [finding]);
        var requests = new List<string>();
        var driver = MakeDriver(
            getPreReviewEvidenceContext: _ => NoPreReviewContext("abc1234"),
            getFindingEvidenceEngineSettings: _ => settings,
            runFocusedEvidence: (_, request) =>
            {
                requests.Add(request);
                return new FocusedEvidenceRunResult(request, true, true, "Focused check passed", []);
            },
            retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
                kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind),
            recordFindingEvidenceOutcome: (goalId, taskId, stableId, outcome, receipt) =>
                kernel.RecordFindingEvidenceOutcome(goalId, taskId, stableId, outcome, receipt));

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        var outcome = reviewer.VerificationHistory.Last().MergedReviewFindings!
            .Single(item => item.StableId == "crosswalk-focused").EvidenceOutcome;
        Assert.NotNull(outcome);
        return (requests, outcome);
    }
}
