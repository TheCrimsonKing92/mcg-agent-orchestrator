using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using static AssemblyCleanupTrxFixture;

public sealed class AssemblyCleanupFailureCauseEvidenceTests
{
    private const string First = "assembly-temp-cleanup-holder pid=41 path=first";
    private const string Second = "  assembly-temp-cleanup-holder pid=42 path=second";
    private const string NoDiagnostics =
        "assembly-temp-cleanup: no cleanup diagnostic lines were captured on the test host error stream.";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CleanupCheck_HolderLines_AttachesOnlyCleanupEvidence(bool includeDecision)
    {
        using var fixture = new AssemblyCleanupTrxFixture();
        var check = CleanupCheck(fixture) with { ProcessStderr = $"unrelated before\n{First}\nunrelated between\n{Second}" };
        if (includeDecision)
            check = check with { CompletionDecision = GoalAcceptanceVerifier.DecideTestShardCompletionFromTrxForTests(
                new GoalAcceptanceVerifier.CommandResult(2, ""), check.TestResultPaths!) };

        var classified = GoalAcceptanceVerifier.AttachFailureCauseEvidence(check);

        Assert.Equal("assembly-cleanup-failure", classified.FailureClassification);
        var cause = Assert.IsType<AcceptanceFailureCauseEvidence>(classified.FailureCauseEvidence);
        Assert.Equal(AcceptanceFailureCause.EnvironmentalApparatus, cause.Cause);
        Assert.Equal("assembly-cleanup-failure", cause.SourceClassification);
        Assert.Equal(check.Name, cause.CheckName);
        Assert.Contains(First, cause.Evidence, StringComparison.Ordinal);
        Assert.Contains(Second, cause.Evidence, StringComparison.Ordinal);
        Assert.DoesNotContain("unrelated", cause.Evidence, StringComparison.Ordinal);
        Assert.True(cause.Evidence.IndexOf(First, StringComparison.Ordinal) < cause.Evidence.IndexOf(Second, StringComparison.Ordinal));
    }

    [Fact]
    public void CleanupCheck_MissingDiagnostics_ReportsOneExplanatoryLine()
    {
        using var fixture = new AssemblyCleanupTrxFixture();
        var check = CleanupCheck(fixture) with { ProcessStderr = "unrelated" };

        var cause = Assert.IsType<AcceptanceFailureCauseEvidence>(
            GoalAcceptanceVerifier.AttachFailureCauseEvidence(check).FailureCauseEvidence);

        Assert.Equal(NoDiagnostics,
            Assert.Single(cause.Evidence.Split(Environment.NewLine).Skip(1)));
    }

    [Theory]
    [InlineData("Assembly-temp-cleanup-holder pid=43")]
    [InlineData("noise assembly-temp-cleanup-holder pid=43")]
    public void CleanupCheck_NonPrefixDiagnostics_AreExcluded(string unrelated)
    {
        using var fixture = new AssemblyCleanupTrxFixture();
        var check = CleanupCheck(fixture) with { ProcessStderr = $"{unrelated}\n{First}" };

        var cause = Assert.IsType<AcceptanceFailureCauseEvidence>(
            GoalAcceptanceVerifier.AttachFailureCauseEvidence(check).FailureCauseEvidence);

        Assert.Contains(First, cause.Evidence, StringComparison.Ordinal);
        Assert.DoesNotContain(unrelated, cause.Evidence, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void CleanupCheck_AbsentMemoryCapture_ReadsStderrFile(string? stderr)
    {
        using var fixture = new AssemblyCleanupTrxFixture();
        var check = CleanupCheck(fixture);
        var path = Path.Combine(fixture.Root, "host.err");
        File.WriteAllText(path, $"noise\n{First}\n{Second}");

        var cause = Assert.IsType<AcceptanceFailureCauseEvidence>(GoalAcceptanceVerifier.AttachFailureCauseEvidence(
            check with { ProcessStderr = stderr, ProcessStderrPath = path }).FailureCauseEvidence);

        Assert.Contains(First, cause.Evidence, StringComparison.Ordinal);
        Assert.Contains(Second, cause.Evidence, StringComparison.Ordinal);
        Assert.DoesNotContain("noise", cause.Evidence, StringComparison.Ordinal);
    }

    [Fact]
    public void CleanupCheck_MemoryCapture_PrefersMemoryOverFile()
    {
        using var fixture = new AssemblyCleanupTrxFixture();
        var check = CleanupCheck(fixture);
        var path = Path.Combine(fixture.Root, "host.err");
        File.WriteAllText(path, Second);

        var cause = Assert.IsType<AcceptanceFailureCauseEvidence>(GoalAcceptanceVerifier.AttachFailureCauseEvidence(
            check with { ProcessStderr = First, ProcessStderrPath = path }).FailureCauseEvidence);

        Assert.Contains(First, cause.Evidence, StringComparison.Ordinal);
        Assert.DoesNotContain(Second, cause.Evidence, StringComparison.Ordinal);
    }

    [Fact]
    public void CleanupCheck_ExcessDiagnostics_RetainsFirstTwentyInOrder()
    {
        using var fixture = new AssemblyCleanupTrxFixture();
        var lines = Enumerable.Range(1, 23).Select(index => $"assembly-temp-cleanup owner={index}").ToArray();
        var check = CleanupCheck(fixture) with { ProcessStderr = string.Join('\n', lines) };

        var cause = Assert.IsType<AcceptanceFailureCauseEvidence>(
            GoalAcceptanceVerifier.AttachFailureCauseEvidence(check).FailureCauseEvidence);
        var diagnostics = cause.Evidence.Split(Environment.NewLine).Skip(1).ToArray();

        Assert.Equal(lines.Take(20), diagnostics.Take(20));
        Assert.Equal(21, diagnostics.Length);
        Assert.Equal("3 more assembly-temp-cleanup lines omitted.", diagnostics[20]);
    }

    [Fact]
    public void CleanupCheck_UnreadableCapture_ReportsUnavailableDiagnostics()
    {
        using var fixture = new AssemblyCleanupTrxFixture();
        var check = CleanupCheck(fixture) with { ProcessStderrPath = Path.Combine(fixture.Root, "absent.err") };

        var cause = Assert.IsType<AcceptanceFailureCauseEvidence>(
            GoalAcceptanceVerifier.AttachFailureCauseEvidence(check).FailureCauseEvidence);

        Assert.Contains(NoDiagnostics, cause.Evidence, StringComparison.Ordinal);
    }

    [Fact]
    public void CleanupCheck_ExistingClassification_PreservesItsCause()
    {
        using var fixture = new AssemblyCleanupTrxFixture();
        var check = CleanupCheck(fixture) with
        {
            FailureClassification = AcceptanceFailureClassifications.GateEnvironmentInterference,
            FailureCauseEvidence = new AcceptanceFailureCauseEvidence(AcceptanceFailureCause.EnvironmentalApparatus,
                "existing receipt", "infrastructure tests: Remainder", AcceptanceFailureClassifications.GateEnvironmentInterference)
        };

        var classified = GoalAcceptanceVerifier.AttachFailureCauseEvidence(check);

        Assert.Equal(check.FailureClassification, classified.FailureClassification);
        Assert.Equal(check.FailureCauseEvidence, classified.FailureCauseEvidence);
    }

    [Fact]
    public void CleanupCheck_EarlierCompletionFailure_DoesNotReclassify()
    {
        using var fixture = new AssemblyCleanupTrxFixture();
        var check = CleanupCheck(fixture) with
        {
            CompletionDecision = new AcceptanceShardCompletionDecision(false, "timed-out", true, 2, 2, 2, "Failed")
        };

        var classified = GoalAcceptanceVerifier.AttachFailureCauseEvidence(check);

        Assert.Null(classified.FailureClassification);
        Assert.Null(classified.FailureCauseEvidence);
    }

    [Theory]
    [InlineData("assembly-cleanup-failure", true)]
    [InlineData("missing-trx", false)]
    public void SharedApparatusVerdict_OriginalDecision_OnlyCleanupGetsDiagnostics(string predicate, bool expected)
    {
        using var fixture = new AssemblyCleanupTrxFixture();
        var check = CleanupCheck(fixture) with
        {
            FailureClassification = AcceptanceFailureClassifications.SharedGateApparatusInvalidated,
            CompletionDecision = new AcceptanceShardCompletionDecision(false, predicate, false, 2, 2, 2, "Failed"),
            ProcessStderr = First
        };

        var classified = GoalAcceptanceVerifier.AttachFailureCauseEvidence(check);

        Assert.Equal(check.FailureClassification, classified.FailureClassification);
        if (expected)
        {
            var cause = Assert.IsType<AcceptanceFailureCauseEvidence>(classified.FailureCauseEvidence);
            Assert.Equal(AcceptanceFailureCause.EnvironmentalApparatus, cause.Cause);
            Assert.Equal(check.FailureClassification, cause.SourceClassification);
            Assert.Contains(First, cause.Evidence, StringComparison.Ordinal);
        }
        else Assert.Null(classified.FailureCauseEvidence);
    }

    private static AcceptanceCheckResult CleanupCheck(AssemblyCleanupTrxFixture fixture) => new(
        "infrastructure tests: Remainder", false, 2, null,
        TestResultPaths: [fixture.WriteReceipt([Result("Tests.Passes", "Passed"),
            Result("[Test Assembly Cleanup Failure (Tests.Passes)]", "Failed")])]);
}
