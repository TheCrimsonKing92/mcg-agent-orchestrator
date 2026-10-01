using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;
using static AssemblyCleanupTrxFixture;

public sealed class AssemblyCleanupRetryFeedbackEvidenceTests
{
    private const string First = "assembly-temp-cleanup-holder pid=41 path=first";
    private const string Second = "assembly-temp-cleanup-holder pid=42 path=second";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MixedLane_RealFailure_KeepsVerdictAndAppendsHolderLines(bool includeDecision)
    {
        using var fixture = new AssemblyCleanupTrxFixture();
        var path = fixture.WriteReceipt([Result("Tests.Passes", "Passed"),
            Result("[Test Assembly Cleanup Failure (Tests.Passes)]", "Failed"),
            Result("Tests.RealFailure", "Failed", "real assertion failed")]);
        var decision = GoalAcceptanceVerifier.DecideTestShardCompletionFromTrxForTests(
            new GoalAcceptanceVerifier.CommandResult(2, ""), [path]);
        var check = new AcceptanceCheckResult("infrastructure tests: Remainder", false, 2, null,
            TestResultPaths: [path], CompletionDecision: includeDecision ? decision : null,
            ProcessStderr: $"unrelated\n{First}\nunrelated\n{Second}");

        var classified = GoalAcceptanceVerifier.AttachFailureCauseEvidence(check);
        var feedback = ConductorDriver.FormatCriterionRetryFeedbackForTests([classified]);

        Assert.False(decision.Passed);
        Assert.Equal("failing-trx", decision.FailedPredicate);
        Assert.Null(classified.FailureClassification);
        Assert.Null(classified.FailureCauseEvidence);
        Assert.Contains(feedback, line => line.Contains("[FAIL] Tests.RealFailure (Failed)", StringComparison.Ordinal) &&
            line.Contains("real assertion failed", StringComparison.Ordinal));
        var lastTrxEntry = Array.FindLastIndex(feedback, line => line.StartsWith("[FAIL]", StringComparison.Ordinal));
        Assert.True(lastTrxEntry >= 0);
        Assert.Equal(First, feedback[lastTrxEntry + 1]);
        Assert.Equal(Second, feedback[lastTrxEntry + 2]);
        Assert.DoesNotContain(feedback, line => line.Contains("unrelated", StringComparison.Ordinal));
    }

    [Fact]
    public void MixedLane_NoDiagnostics_AppendsOneExplanatoryLine()
    {
        using var fixture = new AssemblyCleanupTrxFixture();
        var check = MixedCheck(fixture);

        var feedback = ConductorDriver.FormatCriterionRetryFeedbackForTests([check]);

        Assert.Equal("assembly-temp-cleanup: no cleanup diagnostic lines were captured on the test host error stream.",
            Assert.Single(feedback.Where(line => line.StartsWith("assembly-temp-cleanup", StringComparison.Ordinal))));
    }

    [Fact]
    public void MixedLane_StderrPath_AppendsCapturedFileLines()
    {
        using var fixture = new AssemblyCleanupTrxFixture();
        var check = MixedCheck(fixture);
        var path = Path.Combine(fixture.Root, "host.err");
        File.WriteAllText(path, $"noise\n{First}\n{Second}");

        var feedback = ConductorDriver.FormatCriterionRetryFeedbackForTests(
            [check with { ProcessStderrPath = path }]);

        Assert.Contains(First, feedback);
        Assert.Contains(Second, feedback);
        Assert.DoesNotContain(feedback, line => line.Contains("noise", StringComparison.Ordinal));
    }

    [Fact]
    public void MixedLane_TrxBudgetExhausted_AppendsBoundedDiagnostics()
    {
        using var fixture = new AssemblyCleanupTrxFixture();
        var path = fixture.WriteReceipt(new[] { Result("Tests.Passes", "Passed"),
            Result("Tests.RealFailure", "Failed", "real assertion failed") }
            .Concat(Enumerable.Range(0, 35).Select(index =>
                Result($"[Test Assembly Cleanup Failure (Tests.Passes{index})]", "Failed"))));
        var lines = Enumerable.Range(1, 25).Select(index => $"assembly-temp-cleanup-holder pid={index}").ToArray();
        var check = new AcceptanceCheckResult("infrastructure tests: Remainder", false, 2, null,
            TestResultPaths: [path], ProcessStderr: string.Join('\n', lines));

        var feedback = ConductorDriver.FormatCriterionRetryFeedbackForTests([check]);

        Assert.Equal(lines.Take(20), feedback.Where(line => line.StartsWith("assembly-temp-cleanup-holder", StringComparison.Ordinal)));
        var omittedFailures = Array.FindIndex(feedback, line => line.Contains("more failures omitted", StringComparison.Ordinal));
        Assert.True(omittedFailures >= 0);
        Assert.Equal(lines[0], feedback[omittedFailures + 1]);
        Assert.Contains("5 more assembly-temp-cleanup lines omitted.", feedback);
    }

    [Fact]
    public void MultiplePartitions_Diagnostics_AppearBeforeNextPartition()
    {
        using var fixture = new AssemblyCleanupTrxFixture();
        var first = MixedCheck(fixture) with { ProcessStderr = First };
        var second = MixedCheck(fixture) with { Name = "infrastructure tests: Second", ProcessStderr = Second };

        var feedback = ConductorDriver.FormatCriterionRetryFeedbackForTests([first, second]);
        var firstHolder = Array.IndexOf(feedback, First);
        var secondReceipt = Array.FindIndex(feedback, line =>
            line.StartsWith("TRX receipt for partition \"infrastructure tests: Second\"", StringComparison.Ordinal));

        Assert.True(firstHolder >= 0);
        Assert.True(firstHolder < secondReceipt);
        Assert.True(secondReceipt < Array.IndexOf(feedback, Second));
    }

    [Fact]
    public void RealFailure_NoCleanupRows_DoesNotAppendCleanupDiagnostics()
    {
        using var fixture = new AssemblyCleanupTrxFixture();
        var path = fixture.WriteReceipt([Result("Tests.RealFailure", "Failed", "real assertion failed")]);
        var check = new AcceptanceCheckResult("infrastructure tests: Remainder", false, 2, null,
            TestResultPaths: [path], ProcessStderr: First);

        var feedback = ConductorDriver.FormatCriterionRetryFeedbackForTests([check]);

        Assert.Contains(feedback, line => line.Contains("real assertion failed", StringComparison.Ordinal));
        Assert.DoesNotContain(feedback, line => line.StartsWith("assembly-temp-cleanup", StringComparison.Ordinal));
    }

    private static AcceptanceCheckResult MixedCheck(AssemblyCleanupTrxFixture fixture) => new(
        "infrastructure tests: Remainder", false, 2, null,
        TestResultPaths: [fixture.WriteReceipt([Result("Tests.Passes", "Passed"),
            Result("Tests.RealFailure", "Failed", "real assertion failed"),
            Result("[Test Assembly Cleanup Failure (Tests.Passes)]", "Failed")])]);
}
