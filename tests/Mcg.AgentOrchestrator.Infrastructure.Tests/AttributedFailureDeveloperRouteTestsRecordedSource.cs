using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using static ConductorDriverTests;
using static AttributedFailureDeveloperRouteTests;
using static AttributedFailureAutoRouteScenarioTests;

// Every fixture owns its kernel and temporary paths; no shared process or repository state.
public sealed class AttributedFailureDeveloperRouteTestsRecordedSource
{
    [Fact]
    public void RecordedTrainEvidenceParses()
    {
        var summary = Summary("recorded");

        var failure = Assert.IsType<AttributedFailure>(
            AttributedFailureDeveloperRoute.ReadAcceptanceFailure(summary));

        Assert.Equal("train-recorded", failure.Source);
        Assert.Equal("merge-train-receipt-abc", failure.ReceiptId);
        Assert.Equal(summary.BranchHeadSha, failure.CandidateSha);
        Assert.Equal(new[] { "A.Tests", "B.Tests" }, failure.FailingTests);
    }

    [Fact]
    public void RecordedTrainFailureRoutesTheDeveloper()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = CompletedGoal(kernel);
        var root = ConductorDriverTests.CreateTempDirectory();
        try
        {
            ApplyTrain(kernel, goal, root, "recorded");
            Assert.Equal(GoalStatus.AcceptanceFailed, goal.Status);
            Assert.Equal(Candidate, goal.LatestAcceptanceFailure!.BranchHeadSha);
            var harness = new RouteHarness(kernel);
            var driver = harness.Configure();

            driver.BeginTick(kernel, 1);
            var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

            Assert.False(result.WasEscalated);
            AssertRoute(goal, goal.Tasks.Single(), harness, "train-red", Candidate, ["AlphaTests", "ZebraTests"]);
            Assert.Contains("source=train-recorded", Assert.Single(MarkerRetries(goal)).Message);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void UnknownSourceStillDeclines()
    {
        Assert.NotNull(AttributedFailureDeveloperRoute.ReadAcceptanceFailure(Summary("recorded")));
        Assert.Null(AttributedFailureDeveloperRoute.ReadAcceptanceFailure(Summary("guessed")));
    }

    private static AcceptanceFailureSummary Summary(string source)
    {
        string[] classes = ["A.Tests", "B.Tests"];
        // Match MergeTrainAttributedFailure.Apply's persisted text, including the class separator.
        var evidence = $"merge train RED attributed ({source}) receipt merge-train-receipt-abc: {string.Join(", ", classes)}";
        return new(DateTimeOffset.UtcNow, classes, Candidate, Main,
            classes.Select(name => new AcceptanceCheckAttribution(name, AcceptanceFailureOrigin.Introduced, evidence)).ToArray());
    }
}
