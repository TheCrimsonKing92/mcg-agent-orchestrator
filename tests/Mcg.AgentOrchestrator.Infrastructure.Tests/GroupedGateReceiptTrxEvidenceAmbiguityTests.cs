using Mcg.AgentOrchestrator.App.Orchestration;

// Parallel-safe: each test owns a unique temporary root.
public sealed class GroupedGateReceiptTrxEvidenceAmbiguityTests
{
    private const string AcceptanceClass = "Mcg.AgentOrchestrator.Infrastructure.Acceptance.Tests.SharedNameTests";
    private const string CliClass = "Mcg.AgentOrchestrator.Infrastructure.Cli.Tests.SharedNameTests";

    [Theory]
    [InlineData("Passed")]
    [InlineData("Failed")]
    public void SharedNameReportsSortedAmbiguityBeforeFailure(string cliOutcome)
    {
        using var fixture = new ProspectiveEvidenceGateFixture();
        var cli = GroupedGateTrxSamples.Write(fixture.Root, "cli.trx", Row(CliClass, cliOutcome));
        var acceptance = GroupedGateTrxSamples.Write(fixture.Root, "acceptance.trx",
            Row(AcceptanceClass), GroupedGateTrxSamples.Namespaced);

        var evidence = GroupedGateReceiptTrxEvidence.Evaluate(
            ["SharedNameTests", AcceptanceClass, "AcceptanceAttemptExecutionOwnerTests"],
            [cli, acceptance], fixture.Root);

        Assert.Equal(3, evidence.Count);
        Assert.False(evidence[0].Satisfied);
        Assert.Equal($"SharedNameTests (ambiguous: {AcceptanceClass}, {CliClass})", evidence[0].ReportedEntry);
        Assert.Equal([AcceptanceClass, CliClass], evidence[0].AmbiguousClassNames);
        Assert.Equal(cliOutcome == "Passed" ? (2, 0, 0) : (1, 1, 0),
            (evidence[0].Passed, evidence[0].Failed, evidence[0].Superseded));
        Assert.True(evidence[1].Satisfied);
        Assert.Equal(AcceptanceClass, evidence[1].ReportedEntry);
        Assert.True(evidence[2].Satisfied);
        Assert.Equal("AcceptanceAttemptExecutionOwnerTests", evidence[2].ReportedEntry);
    }

    [Fact]
    public void SameClassInTwoFilesCanSupersedeWithoutAmbiguity()
    {
        using var fixture = new ProspectiveEvidenceGateFixture();
        var original = GroupedGateTrxSamples.Write(fixture.Root, "original.trx", Row(AcceptanceClass, "Failed"));
        var rerun = GroupedGateTrxSamples.Write(fixture.Root, "rerun.trx", Row(AcceptanceClass));

        var evidence = Assert.Single(GroupedGateReceiptTrxEvidence.Evaluate(
            ["SharedNameTests"], [original, rerun], fixture.Root));

        Assert.True(evidence.Satisfied);
        Assert.Equal((1, 0, 1), (evidence.Passed, evidence.Failed, evidence.Superseded));
        Assert.Null(evidence.AmbiguousClassNames);
        Assert.Equal("SharedNameTests", evidence.ReportedEntry);
    }

    [Fact]
    public void NamespaceFreeExactMatchDoesNotOverrideShortNameAmbiguity()
    {
        using var fixture = new ProspectiveEvidenceGateFixture();
        var plain = GroupedGateTrxSamples.Write(fixture.Root, "plain.trx", Row("SharedNameTests"));
        var namespaced = GroupedGateTrxSamples.Write(fixture.Root, "namespaced.trx", Row(AcceptanceClass));

        var evidence = Assert.Single(GroupedGateReceiptTrxEvidence.Evaluate(
            ["SharedNameTests"], [plain, namespaced], fixture.Root));

        Assert.False(evidence.Satisfied);
        Assert.Equal($"SharedNameTests (ambiguous: {AcceptanceClass}, SharedNameTests)", evidence.ReportedEntry);
    }

    private static GroupedGateTrxSamples.Row Row(string className, string outcome = "Passed") =>
        new("check", className, "Check", $"{className}.Check", outcome);
}
