using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: all repositories, attempt metadata and TRX files belong to the current fixture.
public sealed class GateTestObligationRenameFollowTests
{
    [Theory]
    [InlineData("OldNameTests")]
    [InlineData("OldNameTests.Check")]
    [InlineData("Fixture.OldNameTests.Check")]
    public void ProvenRename_SatisfiesClassAndMethodSelectors(string selector)
    {
        using var repo = RenamedRepository();
        using var fixture = new ProspectiveEvidenceGateFixture();
        var goal = Seed(fixture, [selector]);
        var candidate = TestClassRenameEvidenceTests.Git(repo.Root, "rev-parse", "HEAD");
        var rowClass = selector.StartsWith("Fixture.", StringComparison.Ordinal) ? "Fixture.NewNameTests" : "NewNameTests";
        var (id, _, _) = fixture.WriteAttempt(
            [($"{rowClass}.Check", "Passed"), ($"{rowClass}.Second", "Passed")], candidateSha: candidate);
        Assert.Equal(GateTestObligationEvaluator.Outcome.SelectorNotFound,
            Assert.Single(GateTestObligationEvaluator.Evaluate(goal, fixture.OrchestratorDirectory, candidate)).Outcome);

        var result = Assert.Single(GateTestObligationEvaluator.Evaluate(goal, fixture.OrchestratorDirectory, candidate,
            new(repo.Root, "main")));

        Assert.Equal(GateTestObligationEvaluator.Outcome.Satisfied, result.Outcome);
        Assert.Equal(id, result.SourceId);
        var rename = Assert.Single(result.RenameFollows!);
        Assert.Equal("OldNameTests", rename.OldClass);
        Assert.Equal("NewNameTests", rename.NewClass);
        Assert.Equal(new[] { selector }, result.Selectors);
        Assert.Equal($"GATE_TEST_OBLIGATION_RENAME_FOLLOWED id={Assert.Single(goal.GateTestObligations).Id[..8]} " +
            $"old=OldNameTests new=NewNameTests old_path={TestClassRenameEvidenceTests.OldPath} " +
            $"new_path={TestClassRenameEvidenceTests.NewPath} similarity={rename.Similarity} evidence=commit {candidate} source={id}",
            result.RenameProvenance);
    }

    [Fact]
    public void RepeatedClassUse_RecordsOneFollow()
    {
        using var repo = RenamedRepository();
        using var fixture = new ProspectiveEvidenceGateFixture();
        var goal = Seed(fixture, ["OldNameTests.Check", "OldNameTests.Second"]);
        var candidate = TestClassRenameEvidenceTests.Git(repo.Root, "rev-parse", "HEAD");
        fixture.WriteAttempt([("NewNameTests.Check", "Passed"), ("NewNameTests.Second", "Passed")], candidateSha: candidate);

        var result = Assert.Single(GateTestObligationEvaluator.Evaluate(goal, fixture.OrchestratorDirectory, candidate,
            new(repo.Root, "main")));

        Assert.Equal(GateTestObligationEvaluator.Outcome.Satisfied, result.Outcome);
        Assert.Single(result.RenameFollows!);
    }

    [Fact]
    public void UnrelatedReplacement_PreservesMissingSelector()
    {
        using var repo = new TestClassRenameEvidenceTests.Repository();
        TestClassRenameEvidenceTests.ReplaceWithUnrelated(repo.Root);
        using var fixture = new ProspectiveEvidenceGateFixture();
        var goal = Seed(fixture, ["OldNameTests"]);
        var candidate = TestClassRenameEvidenceTests.Git(repo.Root, "rev-parse", "HEAD");
        fixture.WriteAttempt([("OtherWorkTests.Check", "Passed")], candidateSha: candidate);

        var result = Assert.Single(GateTestObligationEvaluator.Evaluate(goal, fixture.OrchestratorDirectory, candidate,
            new(repo.Root, "main")));

        Assert.Equal(GateTestObligationEvaluator.Outcome.SelectorNotFound, result.Outcome);
        Assert.Equal(new[] { "OldNameTests" }, result.Selectors);
        Assert.Empty(result.RenameFollows ?? []);
        Assert.Equal("", result.RenameProvenance);
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("partial")]
    [InlineData("null-scope")]
    [InlineData("segment-substring")]
    [InlineData("ambiguous-segments")]
    public void UnsuccessfulFollow_ReturnsBaselineGrading(string scenario)
    {
        using var repo = RenamedRepository();
        using var fixture = new ProspectiveEvidenceGateFixture();
        string[] selectors = scenario switch
        {
            "partial" => ["OldNameTests", "MissingTests"],
            "segment-substring" => ["PrefixOldNameTests"],
            "ambiguous-segments" => ["OldNameTests.OldNameTests.Check"],
            _ => ["OldNameTests"]
        };
        var goal = Seed(fixture, selectors);
        var candidate = TestClassRenameEvidenceTests.Git(repo.Root, "rev-parse", "HEAD");
        fixture.WriteAttempt([("NewNameTests.Check", scenario == "failed" ? "Failed" : "Passed")], candidateSha: candidate);
        var baseline = Assert.Single(GateTestObligationEvaluator.Evaluate(goal, fixture.OrchestratorDirectory, candidate));

        var result = Assert.Single(GateTestObligationEvaluator.Evaluate(goal, fixture.OrchestratorDirectory, candidate,
            scenario == "null-scope" ? null : new(repo.Root, "main")));

        Assert.Equal(GateTestObligationEvaluator.Outcome.SelectorNotFound, baseline.Outcome);
        AssertSameGrading(baseline, result);
    }

    [Fact]
    public void SplitSources_CannotPoolRewrittenAndUnchangedSelectors()
    {
        using var repo = RenamedRepository();
        using var fixture = new ProspectiveEvidenceGateFixture();
        var goal = Seed(fixture, ["OldNameTests", "ExistingTests"]);
        var candidate = TestClassRenameEvidenceTests.Git(repo.Root, "rev-parse", "HEAD");
        fixture.WriteAttempt([("NewNameTests.Check", "Passed")], candidateSha: candidate,
            completedAt: DateTimeOffset.UnixEpoch.AddMinutes(1));
        fixture.WriteAttempt([("ExistingTests.Check", "Passed")], candidateSha: candidate,
            completedAt: DateTimeOffset.UnixEpoch.AddMinutes(2));
        var baseline = Assert.Single(GateTestObligationEvaluator.Evaluate(goal, fixture.OrchestratorDirectory, candidate));

        var result = Assert.Single(GateTestObligationEvaluator.Evaluate(goal, fixture.OrchestratorDirectory, candidate,
            new(repo.Root, "main")));

        AssertSameGrading(baseline, result);
    }

    [Fact]
    public void OlderCompleteSource_CanSatisfyWholeObligation()
    {
        using var repo = RenamedRepository();
        using var fixture = new ProspectiveEvidenceGateFixture();
        var goal = Seed(fixture, ["OldNameTests", "ExistingTests"]);
        var candidate = TestClassRenameEvidenceTests.Git(repo.Root, "rev-parse", "HEAD");
        var (id, _, _) = fixture.WriteAttempt([("NewNameTests.Check", "Passed"), ("ExistingTests.Check", "Passed")],
            candidateSha: candidate, completedAt: DateTimeOffset.UnixEpoch.AddMinutes(1));
        fixture.WriteAttempt([("ExistingTests.Check", "Passed")], candidateSha: candidate,
            completedAt: DateTimeOffset.UnixEpoch.AddMinutes(2));

        var result = Assert.Single(GateTestObligationEvaluator.Evaluate(goal, fixture.OrchestratorDirectory, candidate,
            new(repo.Root, "main")));

        Assert.Equal(GateTestObligationEvaluator.Outcome.Satisfied, result.Outcome);
        Assert.Equal(id, result.SourceId);
        Assert.Single(result.RenameFollows!);
    }

    private static Goal Seed(ProspectiveEvidenceGateFixture fixture, IReadOnlyList<string> selectors) =>
        GateTestObligationIntakeTests.Seed(fixture.Kernel, fixture.Goal, selectors).GetGoal(fixture.Goal.Id);

    private static TestClassRenameEvidenceTests.Repository RenamedRepository()
    {
        var repo = new TestClassRenameEvidenceTests.Repository();
        TestClassRenameEvidenceTests.Rename(repo.Root, TestClassRenameEvidenceTests.OldPath,
            TestClassRenameEvidenceTests.NewPath, "NewNameTests");
        return repo;
    }

    private static void AssertSameGrading(GateTestObligationEvaluator.Result baseline, GateTestObligationEvaluator.Result actual)
    {
        Assert.Equal(baseline.Obligation, actual.Obligation);
        Assert.Equal(baseline.Outcome, actual.Outcome);
        Assert.Equal(baseline.Selectors, actual.Selectors);
        Assert.Equal(baseline.SourceId, actual.SourceId);
        Assert.Equal(baseline.WaiverReason, actual.WaiverReason);
        Assert.Equal(baseline.RenameFollows, actual.RenameFollows);
        Assert.Equal(baseline.RenameProvenance, actual.RenameProvenance);
    }
}
