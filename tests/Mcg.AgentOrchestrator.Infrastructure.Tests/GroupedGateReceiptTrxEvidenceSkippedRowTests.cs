using Mcg.AgentOrchestrator.App.Orchestration;

// Parallel-safe: each test owns a unique temporary root.
public sealed class GroupedGateReceiptTrxEvidenceSkippedRowTests
{
    private const string Selector = "HostPreconditionTests";

    [Fact]
    public void ClassSelectorWithPassesAndOneSkipIsSatisfied()
    {
        using var fixture = new ProspectiveEvidenceGateFixture();
        var path = GroupedGateTrxSamples.Write(fixture.Root, "class.trx",
            Row("First", "Passed"), Row("Second", "Passed"), Row("HostCheck", "NotExecuted"));

        var evidence = Assert.Single(GroupedGateReceiptTrxEvidence.Evaluate([Selector], [path], fixture.Root));

        Assert.Equal((2, 0, 1, true), (evidence.Passed, evidence.Failed, evidence.Skipped, evidence.Satisfied));
        Assert.Equal(0, evidence.Superseded);
    }

    [Theory]
    [InlineData("Failed")]
    [InlineData("Error")]
    [InlineData("Timeout")]
    [InlineData("notexecuted")]
    [InlineData("Skipped")]
    public void SkipBesideAFailureStillBlocks(string outcome)
    {
        using var fixture = new ProspectiveEvidenceGateFixture();
        var passed = Row("First", "Passed");
        var skipped = Row("HostCheck", "NotExecuted");
        var path = GroupedGateTrxSamples.Write(fixture.Root, "class.trx", passed, skipped);
        var eligible = Assert.Single(GroupedGateReceiptTrxEvidence.Evaluate([Selector], [path], fixture.Root));
        Assert.Equal((1, 0, 1, true), (eligible.Passed, eligible.Failed, eligible.Skipped, eligible.Satisfied));

        GroupedGateTrxSamples.Write(fixture.Root, "class.trx", passed, skipped, Row("Fault", outcome));
        var evidence = Assert.Single(GroupedGateReceiptTrxEvidence.Evaluate([Selector], [path], fixture.Root));

        Assert.Equal((1, 2, 0, false), (evidence.Passed, evidence.Failed, evidence.Skipped, evidence.Satisfied));
        Assert.Equal(0, evidence.Superseded);
    }

    [Fact]
    public void ObligationWithSkippedMemberIsSatisfied()
    {
        using var fixture = new ProspectiveEvidenceGateFixture();
        var path = GroupedGateTrxSamples.Write(fixture.Root, "class.trx",
            Row("First", "Passed"), Row("Second", "Passed"), Row("HostCheck", "NotExecuted"));
        var (id, _, _) = fixture.WriteAttempt([], additionalPaths: [path]);
        var kernel = GateTestObligationIntakeTests.Seed(fixture.Kernel, fixture.Goal, [Selector]);
        var goal = kernel.GetGoal(fixture.Goal.Id);
        var obligation = Assert.Single(goal.GateTestObligations);
        Assert.Equal(goal.AuthoritativeBrief.Version, obligation.BriefVersion);

        var result = Assert.Single(GateTestObligationEvaluator.Evaluate(
            goal, fixture.OrchestratorDirectory, fixture.CandidateSha));

        Assert.Equal(GateTestObligationEvaluator.Outcome.Satisfied, result.Outcome);
        Assert.Equal(obligation, result.Obligation);
        Assert.Equal(id, result.SourceId);
    }

    [Fact]
    public void CrossFilePassNeutralizesOnlyItsSelectorsUnsupersededSkips()
    {
        using var fixture = new ProspectiveEvidenceGateFixture();
        var skipped = Row("HostCheck", "NotExecuted");
        var original = GroupedGateTrxSamples.Write(fixture.Root, "original.trx", skipped,
            Row("OtherHostCheck", "NotExecuted"),
            new("other", "OtherTests", "Check", "OtherTests.Check", "NotExecuted"));
        var rerun = GroupedGateTrxSamples.Write(fixture.Root, "rerun.trx", skipped with { Outcome = "Passed" });

        var evidence = GroupedGateReceiptTrxEvidence.Evaluate([Selector, "OtherTests"], [original, rerun], fixture.Root);

        Assert.Equal(2, evidence.Count);
        Assert.Equal((1, 0, 1, 1, true),
            (evidence[0].Passed, evidence[0].Failed, evidence[0].Superseded, evidence[0].Skipped, evidence[0].Satisfied));
        Assert.Equal((0, 1, 0, 0, false),
            (evidence[1].Passed, evidence[1].Failed, evidence[1].Superseded, evidence[1].Skipped, evidence[1].Satisfied));
    }

    [Theory]
    [InlineData("Failed")]
    [InlineData("Error")]
    public void SupersededFailureStillPreventsNeutralSkips(string outcome)
    {
        using var fixture = new ProspectiveEvidenceGateFixture();
        var fault = Row("Fault", outcome);
        var original = GroupedGateTrxSamples.Write(fixture.Root, "original.trx",
            fault, Row("HostCheck", "NotExecuted"));
        var rerun = GroupedGateTrxSamples.Write(fixture.Root, "rerun.trx", fault with { Outcome = "Passed" });

        var evidence = Assert.Single(GroupedGateReceiptTrxEvidence.Evaluate([Selector], [original, rerun], fixture.Root));

        Assert.Equal((1, 1, 1, 0, false),
            (evidence.Passed, evidence.Failed, evidence.Superseded, evidence.Skipped, evidence.Satisfied));
    }

    private static GroupedGateTrxSamples.Row Row(string method, string outcome) =>
        new(method, Selector, method, $"{Selector}.{method}", outcome);
}
