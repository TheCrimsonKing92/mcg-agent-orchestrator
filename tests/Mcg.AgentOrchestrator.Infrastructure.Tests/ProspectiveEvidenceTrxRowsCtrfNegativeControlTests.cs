using Mcg.AgentOrchestrator.App.Orchestration;

// Parallel-safe: the existing TRX sample writers use only this test's unique temporary directory.
public sealed class ProspectiveEvidenceTrxRowsCtrfNegativeControlTests
{
    [Theory]
    [InlineData("AcceptanceAttemptExecutionOwnerTests", 1, 0)]
    [InlineData("CompletionBuildEvidenceLifecycleTests.TerminalBuildOutcomePreservesTypedReasonAndDiagnostics", 1, 1)]
    [InlineData("internal/color.TestAutoModeHonorsNoColor", 1, 0)]
    [InlineData("TestAutoModeHonorsNoColor", 1, 0)]
    public void Evaluate_ExistingTrxSelectors_PreservesCounts(string selector, int passed, int superseded)
    {
        using var fixture = new ProspectiveEvidenceGateFixture();
        var paths = GroupedGateTrxSamples.WriteIncidentAndRerun(fixture.Root);
        var go = ProspectiveEvidenceTrxRowsSelectorFormTests.Evidence.WriteColorReceipt(fixture.Root);

        var item = Assert.Single(GroupedGateReceiptTrxEvidence.Evaluate([selector], [.. paths, go], fixture.Root));

        Assert.Equal((selector, passed, 0, superseded, 0, true),
            (item.Selector, item.Passed, item.Failed, item.Superseded, item.Skipped, item.Satisfied));
        Assert.Null(item.AmbiguousClassNames);
    }

    [Fact]
    public void Read_ExistingGoTrx_PreservesIdentityAndSourceFields()
    {
        using var fixture = new ProspectiveEvidenceGateFixture();
        var path = ProspectiveEvidenceTrxRowsSelectorFormTests.Evidence.WriteColorReceipt(fixture.Root);

        var row = Assert.Single(ProspectiveEvidenceTrxRows.Read(path));

        const string method = "TestAutoModeHonorsNoColor";
        const string package = "github.com/TheCrimsonKing92/schema-drift/internal/color";
        Assert.Equal(package + "." + method, row.FullIdentity);
        Assert.Equal("color." + method, row.ShortIdentity);
        Assert.Equal("internal/color." + method, row.AliasIdentity);
        Assert.Equal(method, row.BareIdentity);
        Assert.Equal("internal/color", row.CodeBase);
        Assert.Equal(package, row.ClassName);
        Assert.Equal(("Passed", path), (row.Outcome, row.Path));
        Assert.Null(row.FileIdentity);
    }

    [Fact]
    public void Evaluate_TrxIdentityContainingDelimiter_DoesNotGainFileMatching()
    {
        using var fixture = new ProspectiveEvidenceGateFixture();
        const string id = "src/x.test.ts::case";
        var (_, path, _) = fixture.WriteAttempt([(id, "Passed")]);

        var rows = ProspectiveEvidenceTrxRows.Read(path).ToArray();

        Assert.Equal(id, Assert.Single(rows).FullIdentity);
        Assert.Null(rows[0].FileIdentity);
        Assert.Single(ProspectiveEvidenceTrxRows.Match(id, rows));
        Assert.Empty(ProspectiveEvidenceTrxRows.Match("src/x.test.ts", rows));
    }
}
