using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

public sealed class ConductorSpeculativeAcceptanceCohortPlannerTests
{
    private const string MainRevision = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public void ThreeDisjointCandidates_FormOneStableOldestFirstCohort()
    {
        var fixtures = new[]
        {
            new OrderedCandidate(2, Ready("33333333333333333333333333333333", "src/App/Third.cs")),
            new OrderedCandidate(0, Ready("11111111111111111111111111111111", "src/App/First.cs")),
            new OrderedCandidate(1, Ready("22222222222222222222222222222222", "src/App/Second.cs"))
        };

        var first = ConductorSpeculativeAcceptanceCohortPlanner.Plan(
            fixtures.OrderBy(candidate => candidate.OldestFirstKey).Select(candidate => candidate.Candidate).ToArray());
        var second = ConductorSpeculativeAcceptanceCohortPlanner.Plan(
            fixtures.Reverse().OrderBy(candidate => candidate.OldestFirstKey).Select(candidate => candidate.Candidate).ToArray());

        Assert.Equal(
            ["11111111", "22222222", "33333333"],
            Prefixes(Assert.Single(first.Cohorts)));
        Assert.Equal(Prefixes(Assert.Single(first.Cohorts)), Prefixes(Assert.Single(second.Cohorts)));
        Assert.All(first.Dispositions, disposition =>
            Assert.IsType<ConductorSpeculativeAcceptanceDisposition.Proposed>(disposition));
    }

    [Fact]
    public void FullLandingPathOverlap_ExcludesConfigAndDocumentationPairs_ButKeepsDisjointCliCandidate()
    {
        var configFirst = Ready("11111111111111111111111111111111", "config/acceptance-manifest.json");
        var configSecond = Ready("22222222222222222222222222222222", "config/acceptance-manifest.json");
        var cli = Ready(
            "33333333333333333333333333333333",
            "src/Mcg.AgentOrchestrator.App/Cli/Commands.cs");

        var configPlan = ConductorSpeculativeAcceptanceCohortPlanner.Plan([configFirst, configSecond, cli]);
        var configOverlap = Assert.IsType<ConductorSpeculativeAcceptanceExclusionEvidence.LandingPathOverlap>(
            Assert.IsType<ConductorSpeculativeAcceptanceDisposition.Excluded>(configPlan.Dispositions[1]).Evidence);

        Assert.Equal("config/acceptance-manifest.json", configOverlap.Path);
        Assert.Equal(["11111111", "33333333"], Prefixes(Assert.Single(configPlan.Cohorts)));

        var documentationPlan = ConductorSpeculativeAcceptanceCohortPlanner.Plan(
        [
            Ready("44444444444444444444444444444444", "docs/shared.md"),
            Ready("55555555555555555555555555555555", "docs/shared.md")
        ]);
        var documentationOverlap = Assert.IsType<ConductorSpeculativeAcceptanceExclusionEvidence.LandingPathOverlap>(
            Assert.IsType<ConductorSpeculativeAcceptanceDisposition.Excluded>(documentationPlan.Dispositions[1]).Evidence);

        Assert.Equal("docs/shared.md", documentationOverlap.Path);
        Assert.Empty(documentationPlan.Cohorts);
        Assert.IsType<ConductorSpeculativeAcceptanceDisposition.Deferred>(documentationPlan.Dispositions[0]);
    }

    [Theory]
    [InlineData((int)GateReadyCandidateExclusionReason.LifecycleNotReady)]
    [InlineData((int)GateReadyCandidateExclusionReason.GateNotReady)]
    [InlineData((int)GateReadyCandidateExclusionReason.RevisionUnknown)]
    [InlineData((int)GateReadyCandidateExclusionReason.RevisionStale)]
    [InlineData((int)GateReadyCandidateExclusionReason.RiskUnknown)]
    [InlineData((int)GateReadyCandidateExclusionReason.RiskNotAutoPromotable)]
    [InlineData((int)GateReadyCandidateExclusionReason.ScopeResolutionFailed)]
    [InlineData((int)GateReadyCandidateExclusionReason.ScopeEmpty)]
    [InlineData((int)GateReadyCandidateExclusionReason.MergeConflict)]
    [InlineData((int)GateReadyCandidateExclusionReason.MergeIndeterminate)]
    public void UpstreamExclusion_IsPreservedExactly(int reasonValue)
    {
        var reason = (GateReadyCandidateExclusionReason)reasonValue;
        var goalId = new GoalId("11111111111111111111111111111111");
        var plan = ConductorSpeculativeAcceptanceCohortPlanner.Plan(
        [
            new ConductorSpeculativeAcceptanceCandidate(
                goalId,
                new GateReadyCandidateProjectionResult.Excluded(reason))
        ]);

        var evidence = Assert.IsType<ConductorSpeculativeAcceptanceExclusionEvidence.Upstream>(
            Assert.IsType<ConductorSpeculativeAcceptanceDisposition.Excluded>(Assert.Single(plan.Dispositions)).Evidence);
        Assert.Equal(reason, evidence.Reason);
        Assert.Empty(plan.Cohorts);
    }

    [Fact]
    public void RevisionAndSerializedResourceConflicts_HaveTypedDiscriminatingEvidence()
    {
        var first = Ready("11111111111111111111111111111111", "scripts/first.ps1");
        var resourceConflict = Ready("22222222222222222222222222222222", "scripts/second.ps1");
        var differentMain = Ready(
            "33333333333333333333333333333333",
            "src/App/Third.cs",
            mainRevision: "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");

        var plan = ConductorSpeculativeAcceptanceCohortPlanner.Plan([first, resourceConflict, differentMain]);
        var resourceEvidence = Assert.IsType<ConductorSpeculativeAcceptanceExclusionEvidence.SerializedResourceOverlap>(
            Assert.IsType<ConductorSpeculativeAcceptanceDisposition.Excluded>(plan.Dispositions[1]).Evidence);
        var revisionEvidence = Assert.IsType<ConductorSpeculativeAcceptanceExclusionEvidence.MainRevisionMismatch>(
            Assert.IsType<ConductorSpeculativeAcceptanceDisposition.Excluded>(plan.Dispositions[2]).Evidence);

        Assert.Equal("ownership:scripts", resourceEvidence.ResourceKey);
        Assert.Equal(MainRevision, revisionEvidence.ExpectedMainRevision);
        Assert.Equal("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", revisionEvidence.ActualMainRevision);
        Assert.Empty(plan.Cohorts);
        Assert.Equal(
            ConductorSpeculativeAcceptanceDeferralReason.InsufficientCompatiblePeers,
            Assert.IsType<ConductorSpeculativeAcceptanceDisposition.Deferred>(plan.Dispositions[0]).Reason);
    }

    [Fact]
    public void CohortIsBoundedAtFour_AndFifthCandidateRemainsVisibleAsDeferred()
    {
        var candidates = Enumerable.Range(1, 5)
            .Select(index => Ready(
                index.ToString().PadLeft(32, (char)('0' + index)),
                $"src/App/Candidate{index}.cs"))
            .ToArray();

        var plan = ConductorSpeculativeAcceptanceCohortPlanner.Plan(candidates);

        Assert.Equal(ConductorSpeculativeAcceptanceCohortPlanner.MaximumCohortSize, Assert.Single(plan.Cohorts).Members.Count);
        var fifth = Assert.IsType<ConductorSpeculativeAcceptanceDisposition.Deferred>(plan.Dispositions[4]);
        Assert.Equal(candidates[4].GoalId, fifth.GoalId);
        Assert.Equal(ConductorSpeculativeAcceptanceDeferralReason.CohortCapacity, fifth.Reason);
        Assert.Equal(5, plan.Dispositions.Count);
    }

    [Fact]
    public void IncompatibleCandidateBeyondCapacity_PreservesExactTypedExclusion()
    {
        var selected = new[]
        {
            Ready("11111111111111111111111111111111", "scripts/first.ps1"),
            Ready("22222222222222222222222222222222", "src/App/Second.cs"),
            Ready("33333333333333333333333333333333", "src/Cli/Third.cs"),
            Ready("44444444444444444444444444444444", "tests/Fourth.cs")
        };
        var pathPlan = ConductorSpeculativeAcceptanceCohortPlanner.Plan(
            [.. selected, Ready("55555555555555555555555555555555", "src/App/Second.cs")]);
        var resourcePlan = ConductorSpeculativeAcceptanceCohortPlanner.Plan(
            [.. selected, Ready("66666666666666666666666666666666", "scripts/fifth.ps1")]);
        var revisionPlan = ConductorSpeculativeAcceptanceCohortPlanner.Plan(
            [.. selected, Ready(
                "77777777777777777777777777777777",
                "docs/fifth.md",
                mainRevision: "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")]);

        var pathEvidence = Assert.IsType<ConductorSpeculativeAcceptanceExclusionEvidence.LandingPathOverlap>(
            Assert.IsType<ConductorSpeculativeAcceptanceDisposition.Excluded>(pathPlan.Dispositions[4]).Evidence);
        var resourceEvidence = Assert.IsType<ConductorSpeculativeAcceptanceExclusionEvidence.SerializedResourceOverlap>(
            Assert.IsType<ConductorSpeculativeAcceptanceDisposition.Excluded>(resourcePlan.Dispositions[4]).Evidence);
        var revisionEvidence = Assert.IsType<ConductorSpeculativeAcceptanceExclusionEvidence.MainRevisionMismatch>(
            Assert.IsType<ConductorSpeculativeAcceptanceDisposition.Excluded>(revisionPlan.Dispositions[4]).Evidence);

        Assert.Equal(selected[1].GoalId, pathEvidence.ConflictingGoalId);
        Assert.Equal("src/App/Second.cs", pathEvidence.Path);
        Assert.Equal(selected[0].GoalId, resourceEvidence.ConflictingGoalId);
        Assert.Equal("ownership:scripts", resourceEvidence.ResourceKey);
        Assert.Equal(selected[0].GoalId, revisionEvidence.ConflictingGoalId);
        Assert.Equal(MainRevision, revisionEvidence.ExpectedMainRevision);
        Assert.Equal("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", revisionEvidence.ActualMainRevision);
    }

    [Fact]
    public void Receipt_IsSingleLineBoundedAndReportsOmittedOutcomes()
    {
        var candidates = Enumerable.Range(1, 20)
            .Select(index => new ConductorSpeculativeAcceptanceCandidate(
                new GoalId(index.ToString("x").PadLeft(32, '0')),
                new GateReadyCandidateProjectionResult.Excluded(
                    GateReadyCandidateExclusionReason.RevisionUnknown)))
            .ToArray();

        var receipt = ConductorSpeculativeAcceptanceCohortPlanner.Plan(candidates).FormatReceipt(7);

        Assert.StartsWith("SPECULATIVE_COHORT_PLAN tick=7 advisory=true members=none", receipt, StringComparison.Ordinal);
        Assert.Contains("RevisionUnknown", receipt, StringComparison.Ordinal);
        Assert.Contains("omitted=12", receipt, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', receipt);
        Assert.True(receipt.Length <= 1024);
    }

    private static ConductorSpeculativeAcceptanceCandidate Ready(
        string goalValue,
        string path,
        string mainRevision = MainRevision)
    {
        var goalId = new GoalId(goalValue);
        var scope = RepositoryLandingScopeNormalization.Normalize([path]);
        var projection = new GateReadyCandidateProjection(
            goalId,
            GoalLifecycleState.Verified,
            GateReadyVerificationState.Satisfied,
            ChangeRiskTier.Behavior,
            ConductorTransitionDecision.Auto,
            scope.Paths,
            scope.ResourceKeys,
            new GateReadyMergeEvidence(
                goalValue.PadRight(40, 'c')[..40],
                mainRevision,
                GateReadyMergeStatus.Clean,
                GateReadyMergeReason.NoConflictsDetected));
        return new ConductorSpeculativeAcceptanceCandidate(
            goalId,
            new GateReadyCandidateProjectionResult.Ready(projection));
    }

    private static string[] Prefixes(ConductorSpeculativeAcceptanceCohort cohort) =>
        cohort.Members.Select(member => member.GoalId.Value[..8]).ToArray();

    private sealed record OrderedCandidate(int OldestFirstKey, ConductorSpeculativeAcceptanceCandidate Candidate);
}
