using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: real parser/writer fixtures use independent temporary files, no process or environment mutation.
public sealed class CohortNormalizedEvidenceTests
{
    [Theory]
    [InlineData("missing-required-check")]
    [InlineData("missing-required-path")]
    [InlineData("unexplained-path")]
    public void AdvisoryArtifacts_CannotReplaceRequiredCoverage(string fault)
    {
        using var fixture = new CohortNormalizedFixture();
        var required = fixture.Normalized();
        var advisory = fixture.Trx() with { Advisory = true };
        var verification = fixture.Verification(required, advisory);
        verification = fault switch
        {
            "missing-required-check" => verification with { RequiredTestCheckNames = [required.Name, advisory.Name] },
            "missing-required-path" => verification with { TestResultPaths = advisory.TestResultPaths },
            _ => verification with { TestResultPaths = [.. verification.TestResultPaths!, fixture.Normalized("orphan").NormalizedTestResultPaths![0]] }
        };
        AssertRejected(verification);
    }

    [Theory]
    [InlineData(false, "source-attempt")]
    [InlineData(true, "")]
    public void TrxCarry_RequiresExplicitAuthorizationAndSourceIdentity(bool authorized, string attempt)
    {
        using var fixture = new CohortNormalizedFixture();
        var trx = fixture.Trx() with { TestResultAttemptId = attempt, TestResultIsExplicitCrossAttemptReuse = authorized };
        var classified = AcceptanceCohortVerificationClassifier.ClassifyCohortVerificationResult(
            fixture.Verification(fixture.Normalized(), trx));
        Assert.Equal(AcceptanceCohortGateOutcome.InfrastructureFailure, classified.Outcome);
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("error")]
    [InlineData("skipped")]
    [InlineData("empty")]
    [InlineData("unrelated")]
    [InlineData("duplicate-source")]
    [InlineData("missing")]
    [InlineData("malformed")]
    public void MultiFileCompanionFaults_CannotPass(string fault)
    {
        using var fixture = new CohortNormalizedFixture();
        var trx = fixture.TrxWithCompanions();
        var path = trx.NormalizedTestResultPaths![1];
        if (fault == "missing") File.Delete(path);
        else if (fault == "malformed") File.WriteAllText(path, "{");
        else
        {
            var id = fault == "unrelated" ? "other" : fault == "duplicate-source" ? "first" : "second";
            NormalizedTestResultWriter.Write(path, "trx", fault == "empty" ? [] : [CohortNormalizedFixture.Record(id, fault)]);
        }
        var classified = AcceptanceCohortVerificationClassifier.ClassifyCohortVerificationResult(
            fixture.Verification(fixture.Normalized(), trx));
        Assert.Equal(fault is "failed" or "error" ? AcceptanceCohortGateOutcome.Failed :
            AcceptanceCohortGateOutcome.InfrastructureFailure, classified.Outcome);
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("error")]
    [InlineData("skipped")]
    [InlineData("empty")]
    public void ActualRecords_OverrideClaimedPassingResult(string status)
    {
        using var fixture = new CohortNormalizedFixture();
        var check = fixture.Normalized(status: status);
        AssertRejected(fixture.Verification(check));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("malformed")]
    [InlineData("duplicate")]
    [InlineData("changed-hash")]
    public void InvalidRequiredArtifact_CannotPass(string fault)
    {
        using var fixture = new CohortNormalizedFixture();
        var check = fixture.Normalized();
        var path = Assert.Single(check.NormalizedTestResultPaths!);
        if (fault == "missing") File.Delete(path);
        else if (fault == "malformed") File.WriteAllText(path, "{");
        else if (fault == "changed-hash") File.AppendAllText(path, " ");
        else
        {
            var json = File.ReadAllText(path);
            var record = System.Text.Json.Nodes.JsonNode.Parse(json)!;
            var tests = record["results"]!["tests"]!.AsArray();
            tests.Add(tests[0]!.DeepClone());
            File.WriteAllText(path, record.ToJsonString());
        }
        // Rebind malformed/duplicate bytes, so this exercises the parser, not just hash rejection.
        if (fault is "malformed" or "duplicate") check = check with { CohortEvidence = check.CohortEvidence! with
            { Artifacts = [CohortTestResultEvidence.Capture(path, AcceptanceCohortCheckEvidence.NormalizedFormat)] } };
        AssertRejected(fixture.Verification(check));
    }

    [Theory]
    [InlineData("exit")]
    [InlineData("aggregate-exit")]
    [InlineData("attempt")]
    [InlineData("run")]
    [InlineData("project")]
    [InlineData("selection")]
    [InlineData("count")]
    [InlineData("check-name")]
    [InlineData("skipped-verification")]
    [InlineData("missing-required-check")]
    [InlineData("missing-declaration")]
    [InlineData("format")]
    [InlineData("allocation")]
    [InlineData("duplicate-check")]
    public void BindingAndCoverageFaults_FailClosed(string fault)
    {
        using var fixture = new CohortNormalizedFixture();
        var check = fixture.Normalized(selected: fault == "selection" ? ["unexecuted"] : null);
        check = fault switch
        {
            "exit" => check with { ExitCode = 7, CohortEvidence = check.CohortEvidence! with { ExitCode = 7 } },
            "attempt" => check with { TestResultAttemptId = "old-attempt" },
            "run" => check with { TestResultRunOrdinal = 2 },
            "project" => check with { TestProjectPath = "other-project" },
            "count" => check with { ExecutedTestCount = 999, CohortEvidence = check.CohortEvidence! with { ExecutedTestCount = 999 } },
            "check-name" => check with { CohortEvidence = check.CohortEvidence! with { CheckName = "other" } },
            "format" => check with { CohortEvidence = check.CohortEvidence! with { Format = "trx" } },
            "allocation" => check with { CohortEvidence = check.CohortEvidence! with { ArtifactRoot = Path.Combine(fixture.Root, "other") } },
            _ => check
        };
        var result = fixture.Verification(check);
        result = fault switch
        {
            "aggregate-exit" => result with { ExitCode = 7 },
            "skipped-verification" => result with { Skipped = true },
            "missing-required-check" => result with { RequiredTestCheckNames = [check.Name, "missing dotnet"] },
            "missing-declaration" => result with { RequiredCrosswalkChecks = [] },
            "duplicate-check" => result with { Checks = [check, check] },
            _ => result
        };
        AssertRejected(result);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NegativeRunOrdinal_CannotSatisfyDeclaredEvidence(bool trx)
    {
        using var fixture = new CohortNormalizedFixture();
        var normalized = fixture.Normalized(runOrdinal: trx ? 0 : -1);
        var verification = trx ? fixture.Verification(normalized, fixture.Trx(runOrdinal: -1)) : fixture.Verification(normalized);
        AssertRejected(verification);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void InvalidTrx_DoesNotFallBackToNormalizedPeer(bool missing)
    {
        using var fixture = new CohortNormalizedFixture();
        var normalized = fixture.Normalized();
        var trx = fixture.Trx();
        if (missing) File.Delete(trx.TestResultPaths![0]);
        else File.WriteAllText(trx.TestResultPaths![0], File.ReadAllText(normalized.NormalizedTestResultPaths![0]));
        AssertRejected(fixture.Verification(normalized, trx));
    }

    [Fact]
    public void ScopedIdsAndExecutedPlusSkipped_AreSupportedWithoutDoubleCountingSupplementalTrx()
    {
        using var fixture = new CohortNormalizedFixture();
        var first = fixture.Normalized("first");
        var second = fixture.Normalized("second");
        var path = first.NormalizedTestResultPaths![0];
        NormalizedTestResultWriter.Write(path, "crosswalk", [CohortNormalizedFixture.Record("same opaque ID", "passed"),
            CohortNormalizedFixture.Record("unselected skip", "skipped")]);
        first = first with { TestResultPaths = fixture.Trx().TestResultPaths, CohortEvidence = first.CohortEvidence! with
            { Artifacts = [CohortTestResultEvidence.Capture(path, AcceptanceCohortCheckEvidence.NormalizedFormat)] } };
        var classified = AcceptanceCohortVerificationClassifier.ClassifyCohortVerificationResultWithPaths(fixture.Verification(first, second));
        Assert.Equal(AcceptanceCohortGateOutcome.Passed, classified.Classification.Outcome);
        Assert.Equal(2, classified.Checks.Sum(check => check.ExecutedTestCount));
        Assert.Equal(3, classified.NormalizedTestResultPaths.Count);
        Assert.Single(classified.Checks[0].SupplementalArtifacts);
        var retained = fixture.Save(fixture.Verification(first, second));
        Assert.True(new CohortAcceptanceStore(fixture.StorePath).TryReadReceipt(retained.Identity.Value)!.HasAuthoritativeLandingEvidence);
        Assert.Equal(2, retained.GateChecks.Sum(check => check.ExecutedTestCount));
    }

    [Fact]
    public void UnreadableArtifact_FailsClosed()
    {
        using var fixture = new CohortNormalizedFixture();
        var check = fixture.Normalized();
        using var locked = File.Open(check.NormalizedTestResultPaths![0], FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        AssertRejected(fixture.Verification(check));
    }

    [Fact]
    public void SupplementalFailure_CannotBeHiddenByAuthoritativePassingRepresentation()
    {
        using var fixture = new CohortNormalizedFixture();
        var check = fixture.Normalized();
        var trx = fixture.Trx();
        var path = trx.TestResultPaths![0];
        File.WriteAllText(path, File.ReadAllText(path).Replace("outcome=\"Passed\"", "outcome=\"Failed\"")
            .Replace("passed=\"1\" failed=\"0\"", "passed=\"0\" failed=\"1\""));
        AssertRejected(fixture.Verification(check with { TestResultPaths = [path] }));
    }

    [Fact]
    public void MergeTrain_RemainsExplicitlyTrxRequired()
    {
        using var fixture = new CohortNormalizedFixture();
        var classified = AcceptanceCohortVerificationClassifier.ClassifyCohortVerificationResultWithPaths(
            fixture.Verification(fixture.Normalized()), allowNormalized: false);
        Assert.Equal(AcceptanceCohortGateOutcome.InfrastructureFailure, classified.Classification.Outcome);
    }

    private static void AssertRejected(AcceptanceVerificationResult result) =>
        Assert.NotEqual(AcceptanceCohortGateOutcome.Passed,
            AcceptanceCohortVerificationClassifier.ClassifyCohortVerificationResult(result).Outcome);
}
