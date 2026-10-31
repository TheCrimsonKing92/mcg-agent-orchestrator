using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.App.Orchestration;

// Parallel-safe: each replay owns all report and manifest paths.
public sealed class DeclaredTestGateObligationReplayTests
{
    [Theory]
    [InlineData("conduct-tail::version")]
    [InlineData(DeclaredTestReplayFixture.Version)]
    [InlineData(DeclaredTestReplayFixture.Usage)]
    [InlineData("conduct-tail::version.version_flag_prints_package_version")]
    [InlineData("conduct-tail.tests::no_arguments_prints_usage_and_exits_2")]
    public void ReportSelectors_SatisfyObligation(string selector)
    {
        using var fixture = new DeclaredTestReplayFixture();
        var source = Assert.Single(ProspectiveEvidenceGateResolver.ReadAttempts(fixture.Gate.OrchestratorDirectory,
            fixture.Gate.Goal.Id, fixture.Gate.CandidateSha));
        Assert.Equal(new[] { fixture.Path }, source.Paths);
        Assert.Contains(source.Checks!.Declared, DeclaredTestReportRows.IsDeclaredCheck);
        Assert.Equal(2, fixture.Read().Rows.Count);

        var result = fixture.Evaluate(selector);

        Assert.Equal(GateTestObligationEvaluator.Outcome.Satisfied, result.Outcome);
        Assert.Equal(DeclaredTestReplayFixture.SourceId, result.SourceId);
    }

    [Fact]
    public void RecordedDottedPair_SatisfiesPersistedObligation()
    {
        using var fixture = new DeclaredTestReplayFixture();
        var result = fixture.Evaluate("conduct-tail::version.version_flag_prints_package_version",
            "conduct-tail.tests::no_arguments_prints_usage_and_exits_2");
        Assert.Equal(GateTestObligationEvaluator.Outcome.Satisfied, result.Outcome);
    }

    [Fact]
    public void ExistingRowMatcher_ReproducesModuleAndDottedMisses()
    {
        using var fixture = new DeclaredTestReplayFixture();
        var evidence = GroupedGateReceiptTrxEvidence.Evaluate([
            "conduct-tail::version", "conduct-tail::version.version_flag_prints_package_version",
            "conduct-tail.tests::no_arguments_prints_usage_and_exits_2"], [fixture.Path], fixture.Gate.Root);
        Assert.All(evidence, item => Assert.Equal(0, item.Passed + item.Failed + item.Skipped + item.Superseded));
        Assert.All(GroupedGateReceiptTrxEvidence.Evaluate([DeclaredTestReplayFixture.Version,
            DeclaredTestReplayFixture.Usage], [fixture.Path], fixture.Gate.Root), item => Assert.True(item.Satisfied));
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("skipped")]
    public void ModuleDescendants_UseExistingFailureAndSkipPolicy(string status)
    {
        using var fixture = new DeclaredTestReplayFixture();
        var document = JsonNode.Parse(File.ReadAllText(fixture.Path))!;
        var tests = document["results"]!["tests"]!.AsArray();
        tests.Add(new JsonObject { ["testId"] = "conduct-tail::version::other", ["name"] = "other",
            ["suite"] = new JsonArray("conduct-tail::version"), ["status"] = status });
        // Skipped descendants retain the existing grading policy when another descendant passes.
        File.WriteAllText(fixture.Path, document.ToJsonString());
        var result = fixture.Evaluate("conduct-tail::version");
        Assert.Equal(status == "failed" ? GateTestObligationEvaluator.Outcome.Failed
            : GateTestObligationEvaluator.Outcome.Satisfied, result.Outcome);
    }

    [Fact]
    public void UnrelatedReport_CannotSatisfyDeclaredTestObligation()
    {
        using var fixture = new DeclaredTestReplayFixture();
        var document = JsonNode.Parse(File.ReadAllText(fixture.Path))!;
        document["results"]!["tool"]!["name"] = "vitest";
        File.WriteAllText(fixture.Path, document.ToJsonString());
        Assert.Equal(GateTestObligationEvaluator.Outcome.SelectorNotFound,
            fixture.Evaluate(DeclaredTestReplayFixture.Version).Outcome);
    }

    [Fact]
    public void MissingReportPath_DoesNotExposePartialPassingRows()
    {
        using var fixture = new DeclaredTestReplayFixture();
        var report = DeclaredTestReportRows.Read([fixture.Path, "missing.ctrf.json"], fixture.Gate.Root);
        Assert.True(report.Applies);
        Assert.Empty(report.Rows);
    }
}
