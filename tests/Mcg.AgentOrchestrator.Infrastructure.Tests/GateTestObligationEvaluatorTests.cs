using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each fixture owns its stores and TRX files.
public sealed class GateTestObligationEvaluatorTests
{
    [Fact]
    public void SoloSource_SatisfiesNamespacedAndMethodOnlySelectors()
    {
        using var fixture = new ProspectiveEvidenceGateFixture();
        var paths = GroupedGateTrxSamples.WriteIncidentAndRerun(fixture.Root);
        var (id, _, _) = fixture.WriteAttempt([], additionalPaths: paths);

        var result = Evaluate(fixture, GroupedGateTrxSamples.Selectors);

        Assert.Equal(GateTestObligationEvaluator.Outcome.Satisfied, result.Outcome);
        Assert.Equal(id, result.SourceId);
    }

    [Fact]
    public void IncompleteSource_ReportsOnlyMissingSelector()
    {
        using var fixture = new ProspectiveEvidenceGateFixture();
        var path = GroupedGateTrxSamples.Write(fixture.Root, "one.trx", GroupedGateTrxSamples.Namespaced);
        var (id, _, _) = fixture.WriteAttempt([], additionalPaths: [path]);

        var result = Evaluate(fixture, [GroupedGateTrxSamples.Selectors[1], GroupedGateTrxSamples.Selectors[2]]);

        Assert.Equal(GateTestObligationEvaluator.Outcome.SelectorNotFound, result.Outcome);
        Assert.Equal(new[] { GroupedGateTrxSamples.Selectors[2] }, result.Selectors);
        Assert.Equal(id, result.SourceId);
    }

    [Fact]
    public void FailedRow_ReportsFailedSelector()
    {
        using var fixture = new ProspectiveEvidenceGateFixture();
        var path = GroupedGateTrxSamples.Write(fixture.Root, "failed.trx", GroupedGateTrxSamples.Lifecycle);
        var (id, _, _) = fixture.WriteAttempt([], additionalPaths: [path]);

        var result = Evaluate(fixture, [GroupedGateTrxSamples.Selectors[0], "MissingTests"]);

        Assert.Equal(GateTestObligationEvaluator.Outcome.Failed, result.Outcome);
        Assert.Equal(new[] { GroupedGateTrxSamples.Selectors[0] }, result.Selectors);
        Assert.Equal(id, result.SourceId);
    }

    [Fact]
    public void PassingRerunInSameSource_SupersedesFailedRow()
    {
        using var fixture = new ProspectiveEvidenceGateFixture();
        var (id, _, _) = fixture.WriteAttempt([], additionalPaths: GroupedGateTrxSamples.WriteIncidentAndRerun(fixture.Root));

        var result = Evaluate(fixture, [GroupedGateTrxSamples.Selectors[0]]);

        Assert.Equal(GateTestObligationEvaluator.Outcome.Satisfied, result.Outcome);
        Assert.Equal(id, result.SourceId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GroupedSource_SatisfiesWithoutSoloAttempt(bool cohort)
    {
        using var fixture = new ProspectiveEvidenceGateFixture();
        var receipt = ProspectiveEvidenceGateResolverGroupedReceiptTests.Save(
            fixture, GroupedGateTrxSamples.WriteIncidentAndRerun(fixture.Root), cohort: cohort);

        var result = Evaluate(fixture, GroupedGateTrxSamples.Selectors);

        Assert.Equal(GateTestObligationEvaluator.Outcome.Satisfied, result.Outcome);
        Assert.Equal(receipt.ReceiptId, result.SourceId);
    }

    [Theory]
    [InlineData("failed-train")]
    [InlineData("other-train-candidate")]
    [InlineData("other-solo-candidate")]
    [InlineData("failed-solo")]
    [InlineData("unknown-candidate")]
    public void IneligibleSource_LeavesObligationPending(string scenario)
    {
        using var fixture = new ProspectiveEvidenceGateFixture();
        var paths = GroupedGateTrxSamples.WriteIncidentAndRerun(fixture.Root);
        if (scenario.EndsWith("solo-candidate", StringComparison.Ordinal) || scenario == "failed-solo")
            fixture.WriteAttempt([], candidateSha: scenario == "failed-solo" ? null : new string('b', 40),
                outcome: scenario == "failed-solo" ? ConductorParallelAcceptanceAttemptOutcome.Failed
                    : ConductorParallelAcceptanceAttemptOutcome.Passed, additionalPaths: paths);
        else
            ProspectiveEvidenceGateResolverGroupedReceiptTests.Save(fixture, paths,
                passed: scenario != "failed-train",
                candidate: scenario == "other-train-candidate" ? new string('b', 40) : null);
        var kernel = GateTestObligationIntakeTests.Seed(fixture.Kernel, fixture.Goal, GroupedGateTrxSamples.Selectors);

        var result = Assert.Single(GateTestObligationEvaluator.Evaluate(kernel.GetGoal(fixture.Goal.Id),
            fixture.OrchestratorDirectory, scenario == "unknown-candidate" ? null : fixture.CandidateSha));

        Assert.Equal(GateTestObligationEvaluator.Outcome.Pending, result.Outcome);
        Assert.Null(result.SourceId);
        Assert.Equal(GroupedGateTrxSamples.Selectors, result.Selectors);
    }

    [Fact]
    public void SplitSources_DoNotPoolAndNewestSourceNamesMissingSelector()
    {
        using var fixture = new ProspectiveEvidenceGateFixture();
        fixture.WriteAttempt([("AlphaTests.Row", "Passed")], completedAt: DateTimeOffset.UnixEpoch);
        var path = GroupedGateTrxSamples.Write(fixture.Root, "newest.trx", GroupedGateTrxSamples.MethodOnly);
        var receipt = ProspectiveEvidenceGateResolverGroupedReceiptTests.Save(fixture, [path],
            completedAt: DateTimeOffset.UnixEpoch.AddMinutes(1));

        var result = Evaluate(fixture, ["AlphaTests", GroupedGateTrxSamples.Selectors[2]]);

        Assert.Equal(GateTestObligationEvaluator.Outcome.SelectorNotFound, result.Outcome);
        Assert.Equal(new[] { "AlphaTests" }, result.Selectors);
        Assert.Equal(receipt.ReceiptId, result.SourceId);
    }

    [Fact]
    public void OlderCompleteSource_SatisfiesDespiteNewerFailure()
    {
        using var fixture = new ProspectiveEvidenceGateFixture();
        var (id, _, _) = fixture.WriteAttempt([("AlphaTests.Row", "Passed")], completedAt: DateTimeOffset.UnixEpoch);
        fixture.WriteAttempt([("AlphaTests.Row", "Failed")], completedAt: DateTimeOffset.UnixEpoch.AddMinutes(1));

        var result = Evaluate(fixture, ["AlphaTests"]);

        Assert.Equal(GateTestObligationEvaluator.Outcome.Satisfied, result.Outcome);
        Assert.Equal(id, result.SourceId);
    }

    [Fact]
    public void NewestIncompleteSource_DecidesFailureWithoutPooling()
    {
        using var fixture = new ProspectiveEvidenceGateFixture();
        fixture.WriteAttempt([("OtherTests.Row", "Passed")], completedAt: DateTimeOffset.UnixEpoch);
        var (id, _, _) = fixture.WriteAttempt([("AlphaTests.Row", "Failed")],
            completedAt: DateTimeOffset.UnixEpoch.AddMinutes(1));

        var result = Evaluate(fixture, ["AlphaTests"]);

        Assert.Equal(GateTestObligationEvaluator.Outcome.Failed, result.Outcome);
        Assert.Equal(new[] { "AlphaTests" }, result.Selectors);
        Assert.Equal(id, result.SourceId);
    }

    [Fact]
    public void EmptySelectors_RequireAnyPassingSource()
    {
        using var fixture = new ProspectiveEvidenceGateFixture();
        Assert.Equal(GateTestObligationEvaluator.Outcome.Pending, Evaluate(fixture, []).Outcome);
        var (id, _, _) = fixture.WriteAttempt([("OtherTests.Row", "Passed")]);

        var result = Evaluate(fixture, []);

        Assert.Equal(GateTestObligationEvaluator.Outcome.Satisfied, result.Outcome);
        Assert.Equal(id, result.SourceId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingTrx_SkipsEntireSource(bool grouped)
    {
        using var fixture = new ProspectiveEvidenceGateFixture();
        var path = GroupedGateTrxSamples.Write(fixture.Root, "present.trx", GroupedGateTrxSamples.Namespaced);
        string[] paths = [path, Path.Combine(fixture.Root, "missing.trx")];
        if (grouped) ProspectiveEvidenceGateResolverGroupedReceiptTests.Save(fixture, paths);
        else fixture.WriteAttempt([], additionalPaths: paths);

        var result = Evaluate(fixture, [GroupedGateTrxSamples.Selectors[1]]);

        Assert.Equal(GateTestObligationEvaluator.Outcome.Pending, result.Outcome);
        Assert.Null(result.SourceId);
    }

    [Fact]
    public void BriefRevision_IgnoresOlderObligation()
    {
        using var fixture = new ProspectiveEvidenceGateFixture();
        var kernel = GateTestObligationIntakeTests.Seed(fixture.Kernel, fixture.Goal, ["MissingTests"]);
        var goal = kernel.GetGoal(fixture.Goal.Id);
        Assert.Equal(GateTestObligationEvaluator.Outcome.Pending,
            Assert.Single(GateTestObligationEvaluator.Evaluate(goal, fixture.OrchestratorDirectory, fixture.CandidateSha)).Outcome);

        kernel.ReviseGoalBrief(goal.Id, "Revised gate evidence");

        Assert.NotEqual(Assert.Single(goal.GateTestObligations).BriefVersion, goal.AuthoritativeBrief.Version);
        Assert.Empty(GateTestObligationEvaluator.Evaluate(goal, fixture.OrchestratorDirectory, fixture.CandidateSha));
    }

    private static GateTestObligationEvaluator.Result Evaluate(
        ProspectiveEvidenceGateFixture fixture, IReadOnlyList<string> selectors)
    {
        var kernel = GateTestObligationIntakeTests.Seed(fixture.Kernel, fixture.Goal, selectors);
        return Assert.Single(GateTestObligationEvaluator.Evaluate(
            kernel.GetGoal(fixture.Goal.Id), fixture.OrchestratorDirectory, fixture.CandidateSha));
    }
}
