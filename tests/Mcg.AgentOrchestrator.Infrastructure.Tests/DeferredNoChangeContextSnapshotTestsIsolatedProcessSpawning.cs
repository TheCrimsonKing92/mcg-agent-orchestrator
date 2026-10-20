using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

[Collection("IsolatedProcessSpawning")]
public sealed class DeferredNoChangeContextSnapshotTestsIsolatedProcessSpawning
{
    [Fact]
    public async Task CapturedPackageIsStableAndLaterCompletionAppearsOnlyInNextPreparation()
    {
        using var fixture = new DeferredNoChangeContextFixture();
        var before = fixture.Package();
        var beforeSummary = DeferredNoChangeContextFixture.Summary(before).GetRawText();
        Assert.DoesNotContain(before.Artifacts, artifact => artifact.Identity.Value.StartsWith("goal/deferred-no-change-receipts/", StringComparison.Ordinal));
        fixture.Complete();
        Assert.Equal(beforeSummary, DeferredNoChangeContextFixture.Summary(before).GetRawText());
        var captured = fixture.Capture();
        var first = fixture.Package(context: captured);
        await fixture.Reload();
        var second = fixture.Package();
        Assert.Equal(first.Artifacts.Select(artifact => (artifact.Identity, artifact.ContentHash)),
            second.Artifacts.Select(artifact => (artifact.Identity, artifact.ContentHash)));
        var body = Assert.Single(first.Artifacts, artifact => artifact.Identity.Value.StartsWith("goal/deferred-no-change-receipts/", StringComparison.Ordinal));
        var immutableBytes = body.AuthoritativeBytes!;
        File.WriteAllText(fixture.Locator.ResultPath!, "{}");
        var replay = fixture.Package(context: captured);
        Assert.Equal(immutableBytes, Assert.Single(replay.Artifacts, artifact => artifact.Identity == body.Identity).AuthoritativeBytes);
        Assert.Equal("Unavailable", DeferredNoChangeContextFixture.Summary(fixture.Package())
            .GetProperty("Receipts")[0].GetProperty("Availability").GetString());
        Assert.Equal("Green", DeferredNoChangeContextFixture.Summary(first).GetProperty("Receipts")[0].GetProperty("Availability").GetString());
    }

    [Fact]
    public void CoverageMatrixKeepsOutstandingSelectionsAndNegativeControlSeparateFromGreenAndHistoricalRed()
    {
        using var fixture = new DeferredNoChangeContextFixture();
        fixture.Complete();
        var findings = new[]
        {
            Finding("covered", "DeferredAlphaTests"),
            Finding("additional", "AdditionalTests"),
            Finding("negative", "DeferredAlphaTests", FindingEvidenceNegativeControl.RevertSrc)
        };
        var historical = new FindingEvidenceReceipt("old-red", new string('e', 40),
            new([new("Infrastructure.Tests", "DeferredAlphaTests")]), true, false, "historical failure");
        fixture.Kernel.RecordTaskVerification(fixture.Goal.Id, fixture.TesterA.Id,
            new("review", fixture.Worktree, 1, "open evidence requests", "", DateTimeOffset.UtcNow,
                ReviewedCommit: fixture.Candidate, MergedReviewFindings: findings, FindingEvidenceReceipts: [historical]));
        var findingBytes = JsonSerializer.SerializeToUtf8Bytes(fixture.TesterA.VerificationHistory);
        var baseline = fixture.Package(context: new(fixture.Goal.Id.Value, fixture.Candidate, []));

        var package = fixture.Package();

        var summary = DeferredNoChangeContextFixture.Summary(package);
        Assert.True(summary.GetProperty("Receipts")[0].GetProperty("CurrentGreenProof").GetBoolean());
        var coverageIdentity = summary.GetProperty("Coverage").GetProperty("LogicalIdentity").GetString();
        var matrix = JsonSerializer.Deserialize<JsonElement>(Assert.Single(package.Artifacts, artifact => artifact.Identity.Value == coverageIdentity).AuthoritativeBytes!);
        var requirements = matrix.GetProperty("Requirements").EnumerateArray().ToDictionary(row => row.GetProperty("StableId").GetString()!);
        Assert.Equal("Infrastructure.Tests:DeferredAlphaTests", Assert.Single(requirements["covered"].GetProperty("CoveredSelections").EnumerateArray()).GetString());
        Assert.Equal("Infrastructure.Tests:AdditionalTests", Assert.Single(requirements["additional"].GetProperty("OutstandingSelections").EnumerateArray()).GetString());
        Assert.True(requirements["negative"].GetProperty("NegativeControlRequired").GetBoolean());
        Assert.False(requirements["negative"].GetProperty("NegativeControlExecuted").GetBoolean());
        Assert.Equal(findingBytes, JsonSerializer.SerializeToUtf8Bytes(fixture.TesterA.VerificationHistory));
        foreach (var original in baseline.Artifacts.Where(artifact => artifact.Identity.Value != "context/manifest.v1.json"))
            Assert.Equal(original.ContentHash, Assert.Single(package.Artifacts, artifact => artifact.Identity == original.Identity).ContentHash);
    }

    [Theory]
    [InlineData(AgentRole.Tester, true)]
    [InlineData(AgentRole.Reviewer, true)]
    [InlineData(AgentRole.Developer, false)]
    [InlineData(AgentRole.Planner, false)]
    [InlineData(AgentRole.Researcher, false)]
    public void EvidenceVisibilityFollowsReviewRolesWithoutRequiringFindingHistory(AgentRole role, bool visible)
    {
        using var fixture = new DeferredNoChangeContextFixture();
        fixture.Complete();
        var task = new TaskSpec(TaskId.New(), "Inspect evidence", role);
        var package = fixture.Package(task);
        Assert.Equal(visible, package.Artifacts.Any(artifact => artifact.Identity.Value == "goal/deferred-no-change-evidence.json"));
        Assert.DoesNotContain(package.Artifacts, artifact => artifact.Identity.Value == "goal/review-finding-history.json");
    }

    [Fact]
    public void CurrentCandidateComesFromPreparationAndOtherCandidateIsHistorical()
    {
        using var fixture = new DeferredNoChangeContextFixture();
        fixture.Complete();
        var context = fixture.Capture(new string('e', 40));
        Assert.All(context.Receipts, receipt => Assert.False(receipt.IsCurrent));
        Assert.Equal(DeferredNoChangeEvidenceAvailability.Green, Assert.Single(context.Receipts).Availability);
        Assert.Throws<WorkerContextPreparationException>(() => fixture.Package(context: context));
        var historical = fixture.Package(context: context, candidateSha: new string('e', 40));
        var summary = DeferredNoChangeContextFixture.Summary(historical);
        Assert.Equal("historical", summary.GetProperty("Receipts")[0].GetProperty("Currentness").GetString());
        Assert.False(summary.GetProperty("Receipts")[0].GetProperty("CurrentGreenProof").GetBoolean());
        Assert.Equal(ContextDeliveryMode.HistoricalFile, Assert.Single(historical.Artifacts,
            artifact => artifact.Identity.Value.StartsWith("goal/deferred-no-change-receipts/", StringComparison.Ordinal)).DeliveryMode);
    }

    [Fact]
    public void InlineSummaryIsBoundedAndFullCoverageRemainsRegistered()
    {
        using var fixture = new DeferredNoChangeContextFixture();
        fixture.Complete();
        var findings = Enumerable.Range(0, 80).Select(index => Finding($"request-{index:D3}", $"Additional{index:D3}Tests")).ToArray();
        fixture.Kernel.RecordTaskVerification(fixture.Goal.Id, fixture.TesterA.Id,
            new("review", fixture.Worktree, 1, "requests", "", DateTimeOffset.UtcNow, ReviewedCommit: fixture.Candidate, MergedReviewFindings: findings));
        var package = fixture.Package();
        var summary = DeferredNoChangeContextFixture.Summary(package);
        Assert.Equal(12, summary.GetProperty("OutstandingRequirements").GetArrayLength());
        Assert.Equal(68, summary.GetProperty("OmittedRequirements").GetInt32());
        var coverage = Assert.Single(package.Artifacts, artifact => artifact.Identity.Value == summary.GetProperty("Coverage").GetProperty("LogicalIdentity").GetString());
        Assert.Equal(ContextDeliveryMode.OnDemandFile, coverage.DeliveryMode);
        Assert.Equal(80, JsonSerializer.Deserialize<JsonElement>(coverage.AuthoritativeBytes!).GetProperty("Requirements").GetArrayLength());
        Assert.Equal(coverage.ContentHash, WorkerContextArtifact.Hash(File.ReadAllBytes(Path.Combine(fixture.Worktree, coverage.MandatoryRelativePath!))));
    }

    [Fact]
    public void CompactRepairKeepsExistingExclusionWhileNormalTesterReceivesEvidence()
    {
        using var fixture = new DeferredNoChangeContextFixture();
        fixture.Complete();
        var finding = Finding("repair", "DeferredAlphaTests");
        fixture.Kernel.RecordTaskVerification(fixture.Goal.Id, fixture.TesterA.Id,
            new("review", fixture.Worktree, 1, "malformed structured output", "", DateTimeOffset.UtcNow,
                ReviewedCommit: fixture.Candidate, MergedReviewFindings: [finding], ReviewFindingTouchedAnchors: [finding.Location],
                ReviewFindingContractViolation: new("schema-invalid", "missing fields"),
                FullStandardOutput: "malformed structured output", FullStandardError: "", CompletionVerdictRule: "substantive-needs-work"));
        var normal = fixture.Package();
        Assert.NotNull(DeferredNoChangeContextFixture.Summary(normal));
        fixture.Kernel.RetryTask(fixture.Goal.Id, fixture.TesterA.Id, "Repair structured output only.", retryRoundKind: RetryRoundKind.Mechanical);

        var compact = fixture.Package();

        Assert.Equal(ReviewFindingHistoryProjectionMode.ContractRepair, compact.ReviewFindingProjection!.Mode);
        Assert.DoesNotContain(compact.Artifacts, artifact => artifact.Identity.Value.StartsWith("goal/deferred-no-change", StringComparison.Ordinal));
        Assert.Contains(compact.Artifacts, artifact => artifact.Identity.Value == "task/review-contract-repair-envelope.json");
        var nextTester = fixture.AddTester();
        Assert.True(DeferredNoChangeContextFixture.Summary(fixture.Package(nextTester)).GetProperty("Receipts")[0].GetProperty("CurrentGreenProof").GetBoolean());
    }

    private static ReviewFinding Finding(string id, string selection, FindingEvidenceNegativeControl? negative = null) =>
        new(id, ReviewFindingState.Open, new("tests/DeferredAlphaTests.cs", "DeferredAlphaTests"), "Execution still required",
            // Match the canonical ProjectLabel recorded by the real deferred completion path.
            Category: FindingCategory.TestCoverage, EvidenceRequest: new([new("Infrastructure.Tests", selection)], negative));
}
