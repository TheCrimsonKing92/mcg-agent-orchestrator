using System.Runtime.CompilerServices;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;

// Parallel-safe: each fixture owns its stores and result files; the source fixture is read-only.
public sealed class GateTestObligationCtrfSuiteSelectorTests
{
    [Theory]
    [InlineData("PZISStringUtils.createSearchPattern", 2, "Satisfied")]
    [InlineData("PZISStringUtils", 3, "Satisfied")]
    [InlineData("PZISCollectionUtils.Set.size", 1, "Satisfied")]
    [InlineData("PZISNoSuch.thing", 0, "SelectorNotFound")]
    public void BustedReceipt_DottedSelector_GradesMatchingRows(
        string selector, int passed, string expected)
    {
        using var fixture = new ProspectiveEvidenceGateFixture();
        var path = CopyFixture(fixture.Root);
        var (source, _, _) = fixture.WriteAttempt([], additionalPaths: [path]);
        var kernel = GateTestObligationIntakeTests.Seed(fixture.Kernel, fixture.Goal, [selector]);

        var evidence = Assert.Single(GroupedGateReceiptTrxEvidence.Evaluate([selector], [path], fixture.Root));
        var result = Assert.Single(GateTestObligationEvaluator.Evaluate(
            kernel.GetGoal(fixture.Goal.Id), fixture.OrchestratorDirectory, fixture.CandidateSha));

        Assert.Equal((passed, 0, passed > 0), (evidence.Passed, evidence.Failed, evidence.Satisfied));
        Assert.Equal(expected, result.Outcome.ToString());
        Assert.Equal(source, result.SourceId);
        Assert.Equal([selector], result.Selectors);
    }

    [Fact]
    public void Suite_WithPassedAndFailedRows_GradesFailed()
    {
        using var fixture = new ProspectiveEvidenceGateFixture();
        const string selector = "PZISCollectionUtils.Set.remove";
        var path = Path.Combine(fixture.Root, "mixed.ctrf.json");
        File.WriteAllText(path, """
            {"results":{"tests":[
              {"testId":"spec/c_spec.lua::PZISCollectionUtils.Set remove drops","name":"drops","status":"passed"},
              {"testId":"spec/c_spec.lua::PZISCollectionUtils.Set remove keeps others","name":"keeps others","status":"failed"}
            ]}}
            """);

        var evidence = GroupedGateReceiptTrxEvidence.Evaluate([selector], [path], fixture.Root);
        var item = Assert.Single(evidence);
        var result = GateTestObligationEvaluator.Grade(new GateTestObligation("suite", 1, [selector], null,
            "Mixed suite", DateTimeOffset.UnixEpoch), "mixed-source", evidence);

        Assert.Equal((1, 1, false), (item.Passed, item.Failed, item.Satisfied));
        Assert.Equal(GateTestObligationEvaluator.Outcome.Failed, result.Outcome);
        Assert.Equal([selector], result.Selectors);
    }

    internal static string CopyFixture(string directory, [CallerFilePath] string sourceFile = "")
    {
        var sourceDirectory = VerifiedRepositoryRoot.TryGetVerifiedRoot(out var root)
            ? Path.Combine(root, "tests", "Mcg.AgentOrchestrator.Infrastructure.Tests")
            : Path.GetDirectoryName(sourceFile)!;
        var destination = Path.Combine(directory, "busted-gate.ctrf.json");
        File.Copy(Path.Combine(sourceDirectory, "Fixtures", "GateCtrf", "busted-gate.ctrf.json"), destination);
        return destination;
    }
}
