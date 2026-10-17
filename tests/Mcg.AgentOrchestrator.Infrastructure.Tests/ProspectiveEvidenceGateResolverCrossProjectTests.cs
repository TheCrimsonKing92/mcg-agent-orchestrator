using Mcg.AgentOrchestrator.App.Orchestration;

// Parallel-safe: each fixture owns its kernel and a unique temporary root.
public sealed class ProspectiveEvidenceGateResolverCrossProjectTests
{
    private const string ShortClass = "AcceptanceAttemptExecutionOwnerTests";
    private const string AcceptanceClass = "Mcg.AgentOrchestrator.Infrastructure.Acceptance.Tests." + ShortClass;
    private const string AcceptanceShared = "Mcg.AgentOrchestrator.Infrastructure.Acceptance.Tests.SharedNameTests";
    private const string CliShared = "Mcg.AgentOrchestrator.Infrastructure.Cli.Tests.SharedNameTests";

    [Theory]
    [InlineData(ShortClass)]
    [InlineData(AcceptanceClass)]
    [InlineData(ShortClass + ".Method3")]
    public void NamespacedClassInSecondTrxResolvesWithAllAttemptPaths(string selector)
    {
        using var fixture = new ProspectiveEvidenceGateFixture();
        var wait = fixture.CreateWait([selector]);
        var extra = GroupedGateTrxSamples.Write(fixture.Root, "acceptance.trx", EightRows(AcceptanceClass));
        var (id, trx, _) = fixture.WriteAttempt([("UnrelatedTests.Check", "Passed")], additionalPaths: [extra]);

        var result = fixture.Resolve();

        Assert.Equal([wait.Id], result.ResolvedRequestIds);
        Assert.Empty(result.UnresolvedWaits);
        Assert.True(wait.IsCompleted);
        Assert.Equal($"Satisfied by gate attempt {id}; TRX {trx}, {extra}; 1 named tests passed.", wait.Answer);
        Assert.Empty(fixture.Kernel.GetPendingHumanInput(fixture.Goal.Id));
    }

    [Theory]
    [InlineData("other-class")]
    [InlineData("one-failed")]
    [InlineData("longer-class")]
    public void MissingFailedOrLongerClassLeavesBareSelectorPending(string scenario)
    {
        using var fixture = new ProspectiveEvidenceGateFixture();
        var wait = fixture.CreateWait([ShortClass]);
        var rows = scenario switch
        {
            "other-class" => EightRows("Mcg.AgentOrchestrator.Infrastructure.Cli.Tests.OtherTests"),
            "one-failed" => EightRows(AcceptanceClass, failedMethod: 8),
            "longer-class" => new[] { Row(AcceptanceClass + "Extra", "Check") },
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        };
        var extra = GroupedGateTrxSamples.Write(fixture.Root, "acceptance.trx", rows);
        fixture.WriteAttempt([("UnrelatedTests.Check", "Passed")], additionalPaths: [extra]);
        var passingSelector = scenario switch
        {
            "other-class" => "OtherTests",
            "one-failed" => ShortClass + ".Method1",
            "longer-class" => ShortClass + "Extra",
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        };
        var passingWait = fixture.CreateWait([passingSelector]);

        var result = fixture.Resolve();

        Assert.Equal([passingWait.Id], result.ResolvedRequestIds);
        Assert.True(passingWait.IsCompleted);
        var pending = Assert.Single(result.UnresolvedWaits);
        Assert.Equal(wait.Id, pending.RequestId);
        Assert.Equal([ShortClass], pending.MissingOrFailedTestNames);
        Assert.Null(pending.SubmissionFailure);
        Assert.False(wait.IsCompleted);
        Assert.Null(wait.Answer);
        Assert.Equal([wait.Id], fixture.Kernel.GetPendingHumanInput(fixture.Goal.Id).Select(item => item.Id));
    }

    [Fact]
    public void SharedShortNameIsAmbiguousWhileQualifiedWaitResolves()
    {
        using var fixture = new ProspectiveEvidenceGateFixture();
        var shortWait = fixture.CreateWait(["SharedNameTests"]);
        var qualifiedWait = fixture.CreateWait([AcceptanceShared]);
        var extra = GroupedGateTrxSamples.Write(fixture.Root, "cli.trx", Row(CliShared, "Check"));
        var (id, trx, _) = fixture.WriteAttempt([], additionalPaths: [extra]);
        GroupedGateTrxSamples.Write(Path.GetDirectoryName(trx)!, Path.GetFileName(trx), Row(AcceptanceShared, "Check"));

        var result = fixture.Resolve();

        Assert.Equal([qualifiedWait.Id], result.ResolvedRequestIds);
        var pending = Assert.Single(result.UnresolvedWaits);
        Assert.Equal(shortWait.Id, pending.RequestId);
        Assert.Equal([$"SharedNameTests (ambiguous: {AcceptanceShared}, {CliShared})"], pending.MissingOrFailedTestNames);
        Assert.Null(pending.SubmissionFailure);
        Assert.False(shortWait.IsCompleted);
        Assert.Null(shortWait.Answer);
        Assert.True(qualifiedWait.IsCompleted);
        Assert.Equal($"Satisfied by gate attempt {id}; TRX {trx}, {extra}; 1 named tests passed.", qualifiedWait.Answer);
        Assert.Equal([shortWait.Id], fixture.Kernel.GetPendingHumanInput(fixture.Goal.Id).Select(item => item.Id));
    }

    private static GroupedGateTrxSamples.Row[] EightRows(string className, int? failedMethod = null) =>
        Enumerable.Range(1, 8).Select(index =>
            Row(className, $"Method{index}", index == failedMethod ? "Failed" : "Passed")).ToArray();

    private static GroupedGateTrxSamples.Row Row(string className, string method, string outcome = "Passed") =>
        new(method, className, method, $"{className}.{method}", outcome);
}
