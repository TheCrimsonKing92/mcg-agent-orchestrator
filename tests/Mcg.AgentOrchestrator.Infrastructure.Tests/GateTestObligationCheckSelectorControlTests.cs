using Mcg.AgentOrchestrator.App.Orchestration;
using AcceptanceManifestCheck = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;
using static GateTestObligationCheckOutcomeTests;

// Parallel-safe: every test owns its manifest and test-result sources.
public sealed class GateTestObligationCheckSelectorControlTests
{
    [Theory]
    [InlineData("Passed", "Satisfied")]
    [InlineData("Failed", "Failed")]
    public void TrxClassSelector_KeepsRowGradeWithPassingCheck(
        string rowOutcome, string expected)
    {
        using var fixture = new ProspectiveEvidenceGateFixture();
        var (id, _, _) = fixture.WriteAttempt([("AlphaTests.Check", rowOutcome)]);
        WriteManifest(fixture, TypeCheck());
        WriteResult(fixture, id);

        var result = Evaluate(fixture, "AlphaTests");

        Assert.Equal(expected, result.Outcome.ToString());
        Assert.Equal(id, result.SourceId);
        Assert.Equal(new[] { "AlphaTests" }, result.Selectors);
    }

    [Fact]
    public void CtrfFileSelector_KeepsRowGradeWithPassingCheck()
    {
        using var fixture = new ProspectiveEvidenceGateFixture();
        var path = ProspectiveEvidenceCtrfRowsTests.CopyFixture(fixture.Root);
        var (id, _, _) = fixture.WriteAttempt([], additionalPaths: [path]);
        WriteManifest(fixture, TypeCheck());
        WriteResult(fixture, id);

        var result = Evaluate(fixture, ProspectiveEvidenceCtrfRowsTests.EngineFile);

        Assert.Equal(GateTestObligationEvaluator.Outcome.Satisfied, result.Outcome);
        Assert.Equal(id, result.SourceId);
        Assert.Equal(new[] { ProspectiveEvidenceCtrfRowsTests.EngineFile }, result.Selectors);
    }

    [Fact]
    public void UndeclaredSelector_RemainsSelectorNotFoundWithPassingCheck()
    {
        using var fixture = new ProspectiveEvidenceGateFixture();
        var (id, _, _) = fixture.WriteAttempt([("AlphaTests.Check", "Passed")]);
        WriteManifest(fixture, TypeCheck());
        WriteResult(fixture, id);

        var result = Evaluate(fixture, "eslint .");

        Assert.Equal(GateTestObligationEvaluator.Outcome.SelectorNotFound, result.Outcome);
        Assert.Equal(id, result.SourceId);
        Assert.Equal(new[] { "eslint ." }, result.Selectors);
    }

    [Theory]
    [InlineData("Passed", "Satisfied")]
    [InlineData("Failed", "Failed")]
    [InlineData(null, "SelectorNotFound")]
    public void TestCheckNameCollision_PreservesTestRowsOverCheckPass(
        string? rowOutcome, string expected)
    {
        using var fixture = new ProspectiveEvidenceGateFixture();
        var (id, _, _) = fixture.WriteAttempt(rowOutcome is null ? [] : [("AlphaTests.Check", rowOutcome)]);
        WriteManifest(fixture,
            new AcceptanceManifestCheck { Name = "AlphaTests", Type = "dotnet-test" },
            new AcceptanceManifestCheck { Name = "AlphaTests", Type = "command", Command = "echo" });
        WriteResult(fixture, id);

        var result = Evaluate(fixture, "AlphaTests");

        Assert.Equal(expected, result.Outcome.ToString());
        Assert.Equal(id, result.SourceId);
        Assert.Equal(new[] { "AlphaTests" }, result.Selectors);
    }

    [Fact]
    public void PendingCheckWithFailedTest_PreservesFailedTestSelector()
    {
        using var fixture = new ProspectiveEvidenceGateFixture();
        var (id, _, _) = fixture.WriteAttempt([("AlphaTests.Check", "Failed")]);
        WriteManifest(fixture, TypeCheck());

        var result = Evaluate(fixture, Selector, "AlphaTests");

        Assert.Equal(GateTestObligationEvaluator.Outcome.Failed, result.Outcome);
        Assert.Equal(id, result.SourceId);
        Assert.Equal(new[] { "AlphaTests" }, result.Selectors);
    }
}
