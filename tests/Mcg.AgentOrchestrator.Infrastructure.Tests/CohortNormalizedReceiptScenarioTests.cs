using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: every scenario owns a temporary database, artifact tree and injected clock.
public sealed class CohortNormalizedReceiptScenarioTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AdvisoryArtifacts_DoNotRejectRequiredEvidenceOrEnterCustody(bool normalized)
    {
        using var fixture = new CohortNormalizedFixture();
        var required = fixture.Normalized();
        var advisory = (normalized ? fixture.Normalized("advisory") : fixture.Trx()) with
            { Advisory = true, Passed = false, ExitCode = 7 };
        var verification = fixture.Verification(required, advisory);
        Assert.Equal(2, verification.TestResultPaths!.Count);
        Assert.Equal(new[] { required.Name }, verification.RequiredTestCheckNames);
        var receipt = fixture.Save(verification);
        Assert.Equal(required.Name, Assert.Single(receipt.GateChecks).CheckName);
        Assert.Single(receipt.GateTestResultPaths);
        Directory.Delete(fixture.SourceRoot, recursive: true);
        var reopened = new CohortAcceptanceStore(fixture.StorePath).TryReadReceipt(receipt.Identity.Value)!;
        Assert.True(reopened.HasAuthoritativeLandingEvidence);
        Assert.Equal(1, Assert.Single(reopened.GateChecks).ExecutedTestCount);
    }

    [Fact]
    public void VerifiedPartitionCarry_PreservesSourceLineageThroughCustodyAndReadback()
    {
        using var fixture = new CohortNormalizedFixture();
        var lane = new GoalAcceptanceVerifier.AcceptanceManifestCheck
        {
            Name = "infrastructure tests: alpha", Type = "dotnet-test", Runner = "vstest",
            Project = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
            Arguments = ["--filter", "FullyQualifiedName~AlphaTests"]
        };
        var options = new AcceptancePartitionVerdictCacheOptions(null, fixture.Root, [lane], 5, true,
            _ => "combined-tree", _ => "main-sha", _ => "combined-commit", () => "source-attempt",
            () => "manifest-a", () => true, ResolveClosureHash: _ => null,
            ResolveChangedFiles: (_, _, _) => [], ResolveDisjointLandingFacts: _ => null,
            MemberGoalIds: fixture.Identity.Members.Select(member => member.GoalId.Value).ToArray(), EmitDiagnostic: _ => { });
        var first = Assert.IsType<AcceptancePartitionVerdictCache>(AcceptancePartitionVerdictCache.Create(options));
        Assert.Null(first.TryReuse(lane));
        first.RecordExecution(lane, fixture.Trx(runOrdinal: 0) with { Name = lane.Name, TestResultAttemptId = "source-attempt" });
        Assert.NotNull(first.CompleteAttempt());
        var next = Assert.IsType<AcceptancePartitionVerdictCache>(AcceptancePartitionVerdictCache.Create(
            options with { ResolveAttemptId = () => "attempt" }));
        var reused = Assert.IsType<AcceptanceCheckResult>(next.TryReuse(lane));
        Assert.True(reused.TestResultIsExplicitCrossAttemptReuse);
        Assert.Equal("source-attempt", reused.TestResultAttemptId);
        Assert.Contains("reuse_rule=pair", reused.ResultSummary);
        var required = fixture.Normalized();
        var receipt = fixture.Save(fixture.Verification(required, reused));
        Directory.Delete(fixture.SourceRoot, recursive: true);
        var reopened = new CohortAcceptanceStore(fixture.StorePath).TryReadReceipt(receipt.Identity.Value)!;
        Assert.True(reopened.HasAuthoritativeLandingEvidence);
        var carried = Assert.Single(reopened.GateChecks, check => check.CheckName == lane.Name);
        Assert.Equal("source-attempt", carried.AttemptId);
        Assert.Equal(0, carried.RunOrdinal);
        Assert.Equal(1, carried.ExecutedTestCount);
        Assert.Equal(2, reopened.GateChecks.Sum(check => check.ExecutedTestCount));
    }

    [Fact]
    public void MultiFileTrxCompanions_RetainAssociationsWithoutDoubleCounting()
    {
        using var fixture = new CohortNormalizedFixture();
        var trx = fixture.TrxWithCompanions();
        var receipt = fixture.Save(fixture.Verification(fixture.Normalized(), trx));
        Assert.Equal(3, receipt.GateChecks.Sum(check => check.ExecutedTestCount));
        var retained = Assert.Single(receipt.GateChecks, check => check.Format == AcceptanceCohortCheckEvidence.TrxFormat);
        Assert.Equal(2, retained.Artifacts.Count);
        Assert.Equal(2, retained.SupplementalArtifacts.Count);
        Assert.All(retained.SupplementalArtifacts, artifact =>
            Assert.Single(new NormalizedTestResultSource().Read(artifact.Path).Records));
        Directory.Delete(fixture.SourceRoot, recursive: true);
        Sweep(fixture);
        var reopened = new CohortAcceptanceStore(fixture.StorePath).TryReadReceipt(receipt.Identity.Value)!;
        Assert.True(reopened.HasAuthoritativeLandingEvidence);
        Assert.Equal(3, CohortTestResultEvidence.Assess(reopened.GateChecks).ExecutedTestCount);
        Assert.Equal(receipt.GateEvidenceArtifacts, reopened.GateEvidenceArtifacts);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    public void Lifecycle_RealParsingCustodyRetentionAndReopenedReadback(bool mixed, int runOrdinal)
    {
        using var fixture = new CohortNormalizedFixture();
        var normalized = fixture.Normalized(mixed ? "zzz-crosswalk" : "crosswalk", runOrdinal: runOrdinal);
        var verification = mixed ? fixture.Verification(normalized, fixture.Trx(runOrdinal)) : fixture.Verification(normalized);
        var originals = verification.Checks!.ToDictionary(check => check.Name,
            check => File.ReadAllBytes((check.NormalizedTestResultPaths ?? check.TestResultPaths!)[0]));
        var receipt = fixture.Save(verification);
        Assert.True(receipt.HasAuthoritativeLandingEvidence);
        Assert.Equal(mixed ? 2 : 1, receipt.GateChecks.Sum(check => check.ExecutedTestCount));
        Assert.Contains(receipt.GateEvidenceArtifacts, a => a.Kind == AcceptanceCohortCheckEvidence.NormalizedFormat);
        Assert.Equal(mixed, receipt.GateEvidenceArtifacts.Any(a => a.Kind == "trx"));
        foreach (var check in receipt.GateChecks)
        {
            Assert.Equal("attempt", check.AttemptId);
            Assert.Equal(receipt.Identity.Value, check.CohortId);
            Assert.Equal(runOrdinal, check.RunOrdinal);
            var artifact = Assert.Single(check.Artifacts);
            Assert.True(AcceptanceCohortGateEvidence.IsContentBoundArtifact(artifact));
            Assert.Equal(originals[check.CheckName], File.ReadAllBytes(artifact.Path));
            Assert.StartsWith(fixture.StorePath + ".artifacts", artifact.Path);
        }
        Directory.Delete(fixture.SourceRoot, recursive: true);
        var decisions = Sweep(fixture);
        Assert.Contains(decisions, d => d.Reason == "cohort-landing-evidence" && d.Action == EvidenceRetentionAction.Preserved);
        var reopened = Assert.IsType<AcceptanceCohortReceipt>(new CohortAcceptanceStore(fixture.StorePath).TryReadReceipt(receipt.Identity.Value));
        Assert.True(reopened.HasAuthoritativeLandingEvidence);
        Assert.Equal(receipt.Identity.Value, reopened.Identity.Value);
        Assert.Equal(receipt.Identity.Members, reopened.Identity.Members);
        Assert.Equal(receipt.GateEvidenceArtifacts, reopened.GateEvidenceArtifacts);
        Assert.All(reopened.GateChecks, check => Assert.Equal(runOrdinal, check.RunOrdinal));
        Assert.Equal(receipt.GateChecks.Sum(c => c.ExecutedTestCount), reopened.GateChecks.Sum(c => c.ExecutedTestCount));
        Assert.Single(new CohortAcceptanceStore(fixture.StorePath).ReadPassedReceiptsForGoal(receipt.Identity.Members[0].GoalId));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("altered")]
    [InlineData("verdict")]
    public void RetainedArtifactDamage_InvalidatesFreshReuse(string fault)
    {
        using var fixture = new CohortNormalizedFixture();
        var receipt = fixture.Save(fixture.Verification(fixture.Normalized()));
        var artifact = receipt.GateEvidenceArtifacts.Single(a => a.Kind ==
            (fault == "verdict" ? "gate-verdict" : AcceptanceCohortCheckEvidence.NormalizedFormat));
        if (fault == "missing") File.Delete(artifact.Path);
        else File.AppendAllText(artifact.Path, " ");
        Assert.False(receipt.HasAuthoritativeLandingEvidence);
        var store = new CohortAcceptanceStore(fixture.StorePath);
        Assert.False(store.TryReadReceipt(receipt.Identity.Value)!.HasAuthoritativeLandingEvidence);
        Assert.Empty(store.ReadPassedReceiptsForGoal(receipt.Identity.Members[0].GoalId));
    }

    [Theory]
    [InlineData("main")]
    [InlineData("tree")]
    [InlineData("candidate")]
    [InlineData("member")]
    [InlineData("attempt")]
    [InlineData("run")]
    public void ContentBoundVerdict_RejectsReboundIdentityOrExecution(string fault)
    {
        using var fixture = new CohortNormalizedFixture();
        var receipt = fixture.Save(fixture.Verification(fixture.Normalized()));
        var changed = fault switch
        {
            "main" => receipt with { Identity = CohortNormalizedFixture.MakeIdentity(main: 'e') },
            "tree" => receipt with { Identity = CohortNormalizedFixture.MakeIdentity(tree: 'e') },
            "candidate" => receipt with { Identity = CohortNormalizedFixture.MakeIdentity(candidate: 'e') },
            "member" => receipt with { Identity = CohortNormalizedFixture.MakeIdentity(otherMember: true) },
            "attempt" => receipt with { GateChecks = [receipt.GateChecks[0] with { AttemptId = "other" }] },
            _ => receipt with { GateChecks = [receipt.GateChecks[0] with { RunOrdinal = 2 }] }
        };
        Assert.False(changed.HasAuthoritativeLandingEvidence);
        if (fault is "main" or "tree" or "candidate" or "member")
            Assert.Throws<ArgumentException>(() => new CohortAcceptanceStore(fixture.StorePath).SaveGateReceipt(changed));
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("error")]
    public void StoreCannotAuthorizeFailedRecordsDespiteClaimedGreenReceipt(string status)
    {
        using var fixture = new CohortNormalizedFixture();
        var check = fixture.Normalized(status: status);
        Assert.Throws<ArgumentException>(() => new CohortAcceptanceStore(fixture.StorePath).SaveGateReceipt(new("receipt",
            fixture.Identity, AcceptanceCohortGateOutcome.Passed, CohortNormalizedFixture.Now, 1, [], 0,
            check.NormalizedTestResultPaths!, ValidForLanding: true) { GateChecks = [check.CohortEvidence!.BindToCohort(fixture.Identity)] }));
    }

    [Fact]
    public void Retention_ReclaimsDeclaredRedNormalizedArtifactsAfterAgeAndKeepsVerdict()
    {
        using var fixture = new CohortNormalizedFixture();
        var receipt = fixture.Save(fixture.Verification(fixture.Normalized()), red: true);
        var normalized = receipt.GateEvidenceArtifacts.Single(a => a.Kind == AcceptanceCohortCheckEvidence.NormalizedFormat);
        var decisions = Sweep(fixture);
        Assert.Contains(decisions, d => d.Path == normalized.Path && d.Action == EvidenceRetentionAction.Deleted);
        Assert.False(File.Exists(normalized.Path));
        Assert.True(File.Exists(receipt.GateEvidenceArtifacts.Single(a => a.Kind == "gate-verdict").Path));
        Assert.False(new CohortAcceptanceStore(fixture.StorePath).TryReadReceipt(receipt.Identity.Value)!.HasAuthoritativeLandingEvidence);
    }

    private static List<EvidenceRetentionDecision> Sweep(CohortNormalizedFixture fixture)
    {
        var decisions = new List<EvidenceRetentionDecision>();
        var goals = fixture.Identity.Members.Select(member => new StorageRetentionGoal(member.GoalId.Value,
            GoalStatus.Completed, new Dictionary<string, WorkTaskStatus>())).ToArray();
        CohortAcceptanceEvidenceRetention.Sweep(fixture.OrchestratorRoot, goals, CohortNormalizedFixture.Now, decisions);
        return decisions;
    }
}
