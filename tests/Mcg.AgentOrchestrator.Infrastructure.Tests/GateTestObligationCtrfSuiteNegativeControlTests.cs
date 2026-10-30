using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each fixture owns its stores and result files; selector resolution is pure.
public sealed class GateTestObligationCtrfSuiteNegativeControlTests
{
    [Theory]
    [InlineData("spec/pzis_string_utils_spec.lua::PZISStringUtils createSearchPattern escapes Lua pattern characters", 1)]
    [InlineData("spec/pzis_string_utils_spec.lua::PZISStringUtils absent", 0)]
    [InlineData("spec/pzis_string_utils_spec.lua::PZISStringUtils", 0)]
    [InlineData("spec/pzis_string_utils_spec.lua", 3)]
    [InlineData("spec/pzis_absent_spec.lua", 0)]
    public void BustedReceipt_ExactAndFileSelectors_PreserveGrades(string selector, int passed)
    {
        using var fixture = new ProspectiveEvidenceGateFixture();
        AssertGrade(fixture, GateTestObligationCtrfSuiteSelectorTests.CopyFixture(fixture.Root), selector,
            passed, 0, passed > 0 ? GateTestObligationEvaluator.Outcome.Satisfied
                : GateTestObligationEvaluator.Outcome.SelectorNotFound);
    }

    [Theory]
    [InlineData("src/game/engine.test.ts", 3, 0, "Satisfied")]
    [InlineData("src/game/broken.test.ts", 1, 1, "Failed")]
    [InlineData("src/game/broken.test.ts::suite > breaks", 0, 1, "Failed")]
    [InlineData("src/game/absent.test.ts", 0, 0, "SelectorNotFound")]
    public void VitestReceipt_ExactAndFileSelectors_PreserveGrades(
        string selector, int passed, int failed, string expected)
    {
        using var fixture = new ProspectiveEvidenceGateFixture();
        AssertGrade(fixture, ProspectiveEvidenceCtrfRowsTests.CopyFixture(fixture.Root), selector, passed, failed,
            Enum.Parse<GateTestObligationEvaluator.Outcome>(expected));
    }

    [Theory]
    [InlineData("AcceptanceAttemptExecutionOwnerTests", 1, 0, "Satisfied")]
    [InlineData("CompletionBuildEvidenceLifecycleTests", 0, 1, "Failed")]
    [InlineData("MissingTests", 0, 0, "SelectorNotFound")]
    public void TrxReceipt_ClassSelectors_PreserveGrades(
        string selector, int passed, int failed, string expected)
    {
        using var fixture = new ProspectiveEvidenceGateFixture();
        var path = GroupedGateTrxSamples.Write(fixture.Root, "classes.trx",
            GroupedGateTrxSamples.Namespaced, GroupedGateTrxSamples.Lifecycle);

        AssertGrade(fixture, path, selector, passed, failed, Enum.Parse<GateTestObligationEvaluator.Outcome>(expected));
    }

    [Theory]
    [InlineData("spec/absent.lua", "spec/absent lua case")]
    [InlineData("absent.lua", "absent lua case")]
    [InlineData("spec/example.lua::Suite.method", "Suite method case")]
    public void FileAndExactIdSelectors_NeverUseSuiteFallback(string selector, string name)
    {
        using var fixture = new ProspectiveEvidenceGateFixture();
        var path = Path.Combine(fixture.Root, "routing.ctrf.json");
        File.WriteAllText(path, SuiteReport(name));

        AssertGrade(fixture, path, selector, 0, 0, GateTestObligationEvaluator.Outcome.SelectorNotFound);
    }

    [Fact]
    public void FocusedEvidence_UnmatchedDottedSelector_PreservesRefusal()
    {
        const string selector = "PZISNoSuch.thing";
        var resolution = CrosswalkSelectorResolution.Resolve([selector],
            ["spec/b_spec.lua::PZISStringUtils createSearchPattern escapes"]);

        Assert.Empty(resolution.Selected);
        Assert.Equal([selector], resolution.Refused);
        Assert.Equal("selector matched no discovered test: PZISNoSuch.thing; discovered test files: spec/b_spec.lua",
            CrosswalkSelectorResolution.RefusalDetail(selector, resolution.DiscoveredFiles));
    }

    private static string SuiteReport(string name) =>
        System.Text.Json.JsonSerializer.Serialize(new
        {
            results = new { tests = new[] { new { testId = "spec/example.lua::" + name, name = "case", status = "passed" } } }
        });

    private static void AssertGrade(ProspectiveEvidenceGateFixture fixture, string path, string selector,
        int passed, int failed, GateTestObligationEvaluator.Outcome expected)
    {
        var evidence = GroupedGateReceiptTrxEvidence.Evaluate([selector], [path], fixture.Root);
        var item = Assert.Single(evidence);
        var result = GateTestObligationEvaluator.Grade(new GateTestObligation("control", 1, [selector], null,
            "Selector control", DateTimeOffset.UnixEpoch), "control-source", evidence);

        Assert.Equal((passed, failed, passed > 0 && failed == 0), (item.Passed, item.Failed, item.Satisfied));
        Assert.Equal(expected, result.Outcome);
        Assert.Equal([selector], result.Selectors);
    }
}
