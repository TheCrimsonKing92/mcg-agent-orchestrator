using Mcg.AgentOrchestrator.Infrastructure;
using static AssemblyCleanupTrxFixture;

public sealed class AssemblyCleanupOnlyCompletionDecisionTests
{
    private const string Marker = "[Test Assembly Cleanup Failure (Tests.Passes)]";

    [Fact]
    public void ObservedReceipt_OnlyCleanupFailures_UsesApparatusPredicate()
    {
        using var fixture = new AssemblyCleanupTrxFixture();
        var rows = Enumerable.Range(0, 222).Select(index => Result($"Tests.Passes{index}", "Passed"))
            .Concat(Enumerable.Range(0, 222).Select(index =>
                Result($"[Test Assembly Cleanup Failure (Tests.Passes{index})]", "Failed")));
        var path = fixture.WriteReceipt(rows);

        var decision = Decide(path);

        Assert.False(decision.Passed);
        Assert.Equal("assembly-cleanup-failure", decision.FailedPredicate);
        Assert.True(AcceptanceFailureClassifications.IsEnvironmentalApparatus(decision.FailedPredicate));
        Assert.Equal(2, decision.ExitCode);
        Assert.Equal(444, decision.ExecutedTestCount);
    }

    [Fact]
    public void CleanupReceipt_NoPassingRows_KeepsFailingTrx()
    {
        using var fixture = new AssemblyCleanupTrxFixture();
        var path = fixture.WriteReceipt([Result(Marker, "Failed")]);

        Assert.Equal("failing-trx", Decide(path).FailedPredicate);
    }

    [Theory]
    [InlineData("Failed")]
    [InlineData("Error")]
    [InlineData("Timeout")]
    [InlineData("Aborted")]
    [InlineData("Unknown")]
    [InlineData("")]
    public void MixedReceipt_RealNonPassingRow_KeepsFailingTrx(string outcome)
    {
        using var fixture = new AssemblyCleanupTrxFixture();
        var path = fixture.WriteReceipt(
            [Result("Tests.Passes", "Passed"), Result(Marker, "Failed"), Result("Tests.Real", outcome)]);

        Assert.Equal("failing-trx", Decide(path).FailedPredicate);
    }

    [Theory]
    [InlineData("NotExecuted")]
    [InlineData("Skipped")]
    public void CleanupReceipt_UnexecutedRow_DoesNotBlockApparatus(string outcome)
    {
        using var fixture = new AssemblyCleanupTrxFixture();
        var path = fixture.WriteReceipt(
            [Result("Tests.Passes", "Passed"), Result(Marker, "Failed"), Result("Tests.NotRun", outcome)]);

        Assert.Equal("assembly-cleanup-failure", Decide(path).FailedPredicate);
    }

    [Theory]
    [InlineData("  [Test Assembly Cleanup Failure (Tests.Passes)]  ", "assembly-cleanup-failure")]
    [InlineData("[test assembly cleanup failure (Tests.Passes)]", "failing-trx")]
    [InlineData("Tests.Real[Test Assembly Cleanup Failure (Tests.Passes)]", "failing-trx")]
    public void CleanupMarker_RawName_RequiresTrimmedOrdinalPrefix(string name, string expected)
    {
        using var fixture = new AssemblyCleanupTrxFixture();
        var path = fixture.WriteReceipt([Result("Tests.Passes", "Passed"), Result(name, "Failed")]);

        Assert.Equal(expected, Decide(path).FailedPredicate);
    }

    [Theory]
    [InlineData(false, "assembly-cleanup-failure")]
    [InlineData(true, "failing-trx")]
    public void MultipleReceipts_AllRows_ContributeToVerdict(bool realFailure, string expected)
    {
        using var fixture = new AssemblyCleanupTrxFixture();
        var first = fixture.WriteReceipt([Result("Tests.Passes", "Passed")]);
        var second = fixture.WriteReceipt([Result(Marker, "Failed")]);
        var paths = new List<string> { first, second };
        if (realFailure) paths.Add(fixture.WriteReceipt([Result("Tests.Real", "Failed", "real failure")]));

        var decision = GoalAcceptanceVerifier.DecideTestShardCompletionFromTrxForTests(
            new GoalAcceptanceVerifier.CommandResult(2, ""), paths);

        Assert.False(decision.Passed);
        Assert.Equal(expected, decision.FailedPredicate);
    }

    [Fact]
    public void CleanupReceipt_IncompleteExecution_KeepsEarlierPredicate()
    {
        using var fixture = new AssemblyCleanupTrxFixture();
        var path = fixture.WriteReceipt([Result("Tests.Passes", "Passed"), Result(Marker, "Failed")], executed: 1);

        Assert.Equal("incomplete-execution", Decide(path).FailedPredicate);
    }

    [Fact]
    public void CleanupReceipt_InterruptedHost_KeepsTimedOutPredicate()
    {
        using var fixture = new AssemblyCleanupTrxFixture();
        var path = fixture.WriteReceipt([Result("Tests.Passes", "Passed"), Result(Marker, "Failed")]);
        var decision = GoalAcceptanceVerifier.DecideTestShardCompletionFromTrxForTests(
            new GoalAcceptanceVerifier.CommandResult(2, "", TimedOut: true), [path]);

        Assert.Equal("timed-out", decision.FailedPredicate);
    }

    [Theory]
    [InlineData(false, "failing-trx")]
    [InlineData(true, "malformed-trx")]
    public void CleanupReceipt_AdditionalUnreadableReceipt_DoesNotClassifyApparatus(bool malformed, string expected)
    {
        using var fixture = new AssemblyCleanupTrxFixture();
        var path = fixture.WriteReceipt([Result("Tests.Passes", "Passed"), Result(Marker, "Failed")]);
        var additional = Path.Combine(fixture.Root, "unreadable.trx");
        if (malformed) File.WriteAllText(additional, "<TestRun>");

        var decision = GoalAcceptanceVerifier.DecideTestShardCompletionFromTrxForTests(
            new GoalAcceptanceVerifier.CommandResult(2, ""), [path, additional]);

        Assert.Equal(expected, decision.FailedPredicate);
    }

    private static AcceptanceShardCompletionDecision Decide(string path) =>
        GoalAcceptanceVerifier.DecideTestShardCompletionFromTrxForTests(
            new GoalAcceptanceVerifier.CommandResult(2, ""), [path]);
}
