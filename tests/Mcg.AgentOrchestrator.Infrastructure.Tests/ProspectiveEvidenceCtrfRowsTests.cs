using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;

// Parallel-safe: each test owns its temporary result files; the checked-in fixture is read-only.
public sealed class ProspectiveEvidenceCtrfRowsTests
{
    internal const string EngineFile = "src/game/engine.test.ts";
    internal const string UiFile = "src/ui/a.test.ts";
    private const string UiId = UiFile + "::suite > does a thing";

    [Fact]
    public void Evaluate_FileAndFullId_CountsOnlyTheirPassingRows()
    {
        using var fixture = new ProspectiveEvidenceGateFixture();
        var path = CopyFixture(fixture.Root);

        var evidence = GroupedGateReceiptTrxEvidence.Evaluate([EngineFile, UiId], [path], fixture.Root);

        Assert.Collection(evidence,
            item => Assert.Equal((EngineFile, 3, 0, 0, 0, true),
                (item.Selector, item.Passed, item.Failed, item.Superseded, item.Skipped, item.Satisfied)),
            item => Assert.Equal((UiId, 1, 0, 0, 0, true),
                (item.Selector, item.Passed, item.Failed, item.Superseded, item.Skipped, item.Satisfied)));
        var row = Assert.Single(ProspectiveEvidenceTrxRows.Read(path).Where(row => row.FullIdentity == UiId));
        Assert.Equal(("does a thing", "Passed", path), (row.ShortIdentity, row.Outcome, row.Path));
    }

    [Fact]
    public void Grade_FileWithOneFailure_ReportsFailedDespitePassingRow()
    {
        using var fixture = new ProspectiveEvidenceGateFixture();
        const string selector = "src/game/broken.test.ts";
        var evidence = GroupedGateReceiptTrxEvidence.Evaluate([selector], [CopyFixture(fixture.Root)], fixture.Root);

        var item = Assert.Single(evidence);
        Assert.Equal((1, 1, false), (item.Passed, item.Failed, item.Satisfied));
        var result = Grade(selector, evidence);
        Assert.Equal(GateTestObligationEvaluator.Outcome.Failed, result.Outcome);
        Assert.Equal(new[] { selector }, result.Selectors);
    }

    [Fact]
    public void Evaluate_SkippedOnlyFile_RemainsUnsatisfied()
    {
        using var fixture = new ProspectiveEvidenceGateFixture();
        var item = Assert.Single(GroupedGateReceiptTrxEvidence.Evaluate(
            ["src/game/skipped.test.ts"], [CopyFixture(fixture.Root)], fixture.Root));

        Assert.Equal(0, item.Passed);
        Assert.False(item.Satisfied);
    }

    [Fact]
    public void Grade_AbsentFile_ReportsSelectorNotFound()
    {
        using var fixture = new ProspectiveEvidenceGateFixture();
        const string selector = "src/game/absent.test.ts";
        var evidence = GroupedGateReceiptTrxEvidence.Evaluate([selector], [CopyFixture(fixture.Root)], fixture.Root);

        var result = Grade(selector, evidence);

        Assert.Equal(GateTestObligationEvaluator.Outcome.SelectorNotFound, result.Outcome);
        Assert.Equal(new[] { selector }, result.Selectors);
        AssertInsufficient(evidence);
    }

    [Theory]
    [InlineData("passed", "Passed")]
    [InlineData("failed", "Failed")]
    [InlineData("skipped", "NotExecuted")]
    [InlineData("pending", "NotExecuted")]
    [InlineData("other", "NotExecuted")]
    [InlineData("error", "NotExecuted")]
    [InlineData("Passed", "NotExecuted")]
    [InlineData("", "NotExecuted")]
    public void Read_StatusString_MapsOnlyLowercasePassAndFail(string status, string outcome)
    {
        using var fixture = new ProspectiveEvidenceGateFixture();
        var path = Path.Combine(fixture.Root, "status.ctrf.json");
        File.WriteAllText(path, Report("file.test.ts::suite > case", status));

        var row = Assert.Single(ProspectiveEvidenceTrxRows.Read(path));

        Assert.Equal(("file.test.ts::suite > case", "case", outcome, path),
            (row.FullIdentity, row.ShortIdentity, row.Outcome, row.Path));
    }

    [Theory]
    [InlineData("result.ctrf.json", false)]
    [InlineData("result.CTRF.JSON", false)]
    [InlineData("result.json", false)]
    [InlineData("result.trx", true)]
    public void Read_SuffixOrContentDetection_AcceptsWhitespaceAndBom(string name, bool bom)
    {
        using var fixture = new ProspectiveEvidenceGateFixture();
        var path = Path.Combine(fixture.Root, name);
        File.WriteAllText(path, " \r\n\t" + File.ReadAllText(CopyFixture(fixture.Root)), new UTF8Encoding(bom));

        var item = Assert.Single(GroupedGateReceiptTrxEvidence.Evaluate([EngineFile], [path], fixture.Root));

        Assert.Equal((3, 0, true), (item.Passed, item.Failed, item.Satisfied));
    }

    [Theory]
    [InlineData("src/game/engine.test.ts", "src/game/engine.test.ts::suite::case", true)]
    [InlineData("src\\game\\engine.test.ts", "src/game/engine.test.ts::case", true)]
    [InlineData("src/game/engine.test.ts", "src\\game\\engine.test.ts::case", true)]
    [InlineData("SRC/game/engine.test.ts", "src/game/engine.test.ts::case", false)]
    [InlineData("./src/game/engine.test.ts", "src/game/engine.test.ts::case", false)]
    [InlineData("src/game/engine.test.ts ", "src/game/engine.test.ts::case", false)]
    public void Evaluate_FileComponent_NormalizesOnlySlashes(string selector, string id, bool matches)
    {
        using var fixture = new ProspectiveEvidenceGateFixture();
        var path = Path.Combine(fixture.Root, "paths.ctrf.json");
        File.WriteAllText(path, Report(id, "passed"));

        var item = Assert.Single(GroupedGateReceiptTrxEvidence.Evaluate([selector], [path], fixture.Root));

        Assert.Equal(matches ? 1 : 0, item.Passed);
        Assert.Equal(matches, item.Satisfied);
    }

    [Fact]
    public void Read_UndelimitedId_UsesExactIdentityWithoutSuiteFallback()
    {
        using var fixture = new ProspectiveEvidenceGateFixture();
        var path = Path.Combine(fixture.Root, "identity.ctrf.json");
        File.WriteAllText(path, """
            {"results":{"tests":[{"testId":"standalone","name":"case","status":"passed",
              "suite":["src/suite.test.ts"],"filePath":"src/suite.test.ts"}]}}
            """);
        var row = Assert.Single(ProspectiveEvidenceTrxRows.Read(path));

        Assert.Single(ProspectiveEvidenceTrxRows.Match("standalone", [row]));
        Assert.Empty(ProspectiveEvidenceTrxRows.Match("src/suite.test.ts", [row]));
        Assert.Null(row.FileIdentity);
    }

    [Fact]
    public void Evaluate_PassAndSkippedRows_PreservesCountsWithoutDeduplication()
    {
        using var fixture = new ProspectiveEvidenceGateFixture();
        var path = Path.Combine(fixture.Root, "duplicates.ctrf.json");
        File.WriteAllText(path, """
            {"results":{"tests":[
              {"testId":"file.test.ts::pass","name":"pass","status":"passed"},
              {"testId":"file.test.ts::pass","name":"pass","status":"passed"},
              {"testId":"file.test.ts::skip","name":"skip","status":"skipped"}]}}
            """);

        var item = Assert.Single(GroupedGateReceiptTrxEvidence.Evaluate(["file.test.ts"], [path], fixture.Root));

        Assert.Equal((2, 0, 0, 1, true),
            (item.Passed, item.Failed, item.Superseded, item.Skipped, item.Satisfied));
    }

    [Theory]
    [InlineData("{\"results\":{\"tests\":[")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("{\"results\":null}")]
    [InlineData("{\"results\":{}}")]
    [InlineData("{\"results\":{\"tests\":{}}}")]
    public void Evaluate_MalformedEnvelope_InvalidatesMixedReceipt(string json)
    {
        using var fixture = new ProspectiveEvidenceGateFixture();
        AssertMalformed(fixture, json);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("3")]
    [InlineData("{\"name\":\"case\",\"status\":\"failed\"}")]
    [InlineData("{\"testId\":\"\",\"name\":\"case\",\"status\":\"failed\"}")]
    [InlineData("{\"testId\":3,\"name\":\"case\",\"status\":\"failed\"}")]
    [InlineData("{\"testId\":\"file::case\",\"status\":\"failed\"}")]
    [InlineData("{\"testId\":\"file::case\",\"name\":null,\"status\":\"failed\"}")]
    [InlineData("{\"testId\":\"file::case\",\"name\":\"case\"}")]
    [InlineData("{\"testId\":\"file::case\",\"name\":\"case\",\"status\":3}")]
    public void Evaluate_MalformedLaterRow_InvalidatesEarlierPassingRows(string row)
    {
        using var fixture = new ProspectiveEvidenceGateFixture();
        var valid = "{\"testId\":\"" + EngineFile + "::pass\",\"name\":\"pass\",\"status\":\"passed\"}";
        AssertMalformed(fixture, "{\"results\":{\"tests\":[" + valid + "," + row + "]}}");
    }

    [Fact]
    public void Evaluate_MissingCtrf_InvalidatesMixedReceipt()
    {
        using var fixture = new ProspectiveEvidenceGateFixture();
        AssertInsufficient(GroupedGateReceiptTrxEvidence.Evaluate([EngineFile, UiId],
            [CopyFixture(fixture.Root), Path.Combine(fixture.Root, "missing.ctrf.json")], fixture.Root));
    }

    [Fact]
    public void Read_CtrfSuffixContainingValidTrx_DoesNotFallBack()
    {
        using var fixture = new ProspectiveEvidenceGateFixture();
        var path = GroupedGateTrxSamples.Write(fixture.Root, "xml.ctrf.json", GroupedGateTrxSamples.Namespaced);

        AssertInsufficient(GroupedGateReceiptTrxEvidence.Evaluate(
            [GroupedGateTrxSamples.Selectors[1]], [path], fixture.Root));
    }

    [Fact]
    public void Evaluate_EmptyTestsArray_ProducesNoEvidence()
    {
        using var fixture = new ProspectiveEvidenceGateFixture();
        var path = Path.Combine(fixture.Root, "empty.ctrf.json");
        File.WriteAllText(path, "{\"results\":{\"tests\":[]}}");

        Assert.Empty(ProspectiveEvidenceTrxRows.Read(path));
        AssertInsufficient(GroupedGateReceiptTrxEvidence.Evaluate([EngineFile], [path], fixture.Root));
    }

    internal static string CopyFixture(string directory, [CallerFilePath] string sourceFile = "")
    {
        var sourceDirectory = VerifiedRepositoryRoot.TryGetVerifiedRoot(out var root)
            ? Path.Combine(root, "tests", "Mcg.AgentOrchestrator.Infrastructure.Tests")
            : Path.GetDirectoryName(sourceFile)!;
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, "vitest-gate.ctrf.json");
        File.Copy(Path.Combine(sourceDirectory, "Fixtures", "GateCtrf", "vitest-gate.ctrf.json"), destination);
        return destination;
    }

    private static string Report(string id, string status) => JsonSerializer.Serialize(new
    {
        results = new { tests = new[] { new { testId = id, name = "case", status } } }
    });

    private static GateTestObligationEvaluator.Result Grade(string selector,
        IReadOnlyList<GroupedGateReceiptTrxEvidence.SelectorEvidence> evidence) =>
        GateTestObligationEvaluator.Grade(new GateTestObligation("ctrf", 1, [selector], null,
            "CTRF selector", DateTimeOffset.UnixEpoch), "ctrf-source", evidence);

    private static void AssertMalformed(ProspectiveEvidenceGateFixture fixture, string json)
    {
        var valid = CopyFixture(fixture.Root);
        var trx = GroupedGateTrxSamples.Write(fixture.Root, "valid.trx", GroupedGateTrxSamples.Namespaced);
        foreach (var name in new[] { "malformed.ctrf.json", "sniffed.json" })
        {
            var path = Path.Combine(fixture.Root, name);
            File.WriteAllText(path, " \r\n" + json);
            var evidence = GroupedGateReceiptTrxEvidence.Evaluate(
                [EngineFile, UiId, GroupedGateTrxSamples.Selectors[1]], [valid, trx, path], fixture.Root);
            Assert.Equal(3, evidence.Count);
            AssertInsufficient(evidence);
        }
    }

    private static void AssertInsufficient(IReadOnlyList<GroupedGateReceiptTrxEvidence.SelectorEvidence> evidence) =>
        Assert.All(evidence, item => Assert.Equal((0, 0, 0, 0, false),
            (item.Passed, item.Failed, item.Superseded, item.Skipped, item.Satisfied)));
}
