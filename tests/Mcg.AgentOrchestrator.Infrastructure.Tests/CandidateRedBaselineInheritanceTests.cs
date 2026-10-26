using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using static ConductorDriverTests;

// Parallel-safe: in-memory receipts and kernel, with no child process or shared state.
public sealed class CandidateRedBaselineInheritanceTests
{
    private const string CandidateSha = "abc1234";
    private const string Alpha = "Fixture.AlphaTests.FailsOnCandidate";

    [Fact]
    public void DisjointFailureSetsAreCandidateOwn()
    {
        var baselineFailures = Enumerable.Range(1, 6)
            .Select(row => $"Fixture.BetaTests.FailsOnBaseline(row: {row})").ToArray();
        var comparison = Compare([Alpha], baselineFailures);

        Assert.Equal(CandidateRedBaselineVerdict.CandidateOwn, comparison.Verdict);
        Assert.Equal(new[] { Alpha }, comparison.CandidateOnlyFailures);
        Assert.Equal($"candidate_failures={Alpha}; baseline_failures={string.Join(',', baselineFailures)}; " +
                     $"candidate_only_failures={Alpha}",
            CandidateRedBaselineInheritance.FormatFailureSets(comparison));
    }

    [Fact]
    public void SubsetIsInheritedAndPartialOverlapIsNot()
    {
        var subset = Compare(["A"], ["A", "B"]);
        Assert.Equal(CandidateRedBaselineVerdict.Inherited, subset.Verdict);
        Assert.Empty(subset.CandidateOnlyFailures);

        var overlap = Compare(["A", "B"], ["B"]);
        Assert.Equal(CandidateRedBaselineVerdict.CandidateOwn, overlap.Verdict);
        Assert.Equal(new[] { "A" }, overlap.CandidateOnlyFailures);

        var trimmed = Compare([" A", "A ", " "], ["A", " A "]);
        Assert.Equal(CandidateRedBaselineVerdict.Inherited, trimmed.Verdict);
        Assert.Equal(new[] { "A" }, trimmed.CandidateFailures);
        Assert.Equal(new[] { "A" }, trimmed.BaselineFailures);
        Assert.Equal(CandidateRedBaselineVerdict.CandidateOwn, Compare(["a"], ["A"]).Verdict);
    }

    [Fact]
    public void NegativeControls()
    {
        var differentCase = Compare(["Fixture.GammaTests.Method(x: 1)"], ["Fixture.GammaTests.Method(x: 2)"]);
        Assert.Equal(CandidateRedBaselineVerdict.CandidateOwn, differentCase.Verdict);
        Assert.Equal(new[] { "Fixture.GammaTests.Method(x: 1)" }, differentCase.CandidateOnlyFailures);
        Assert.Equal(CandidateRedBaselineVerdict.BaselineIdentitiesUnavailable, Compare([Alpha], []).Verdict);

        var (_, goal) = SoftwareGoal();
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        var batch = Batch();
        var candidate = Arm(FindingEvidenceArm.Candidate, [" " + Alpha, Alpha]);
        var baseline = Arm(FindingEvidenceArm.Baseline, [], FindingEvidenceArmDisposition.Green);
        var attribution = MakeDriver().TryAttributeActionableCandidateRed(
            goal, CandidateSha, [candidate, baseline], batch);

        Assert.NotNull(attribution);
        Assert.Equal(batch.Findings, attribution.Findings);
        Assert.Equal(candidate.FailingTestIdentities, attribution.FailingTestIdentities);
        Assert.Null(attribution.BaselineFailingTestIdentities);
        var legacyAttribution = new ConductorDriver.ActionableCandidateRedAttribution(
            batch.Findings, candidate.FailingTestIdentities!, []);
        var expected = ConductorDriver.BuildActionableCandidateRedDecision(
            goal, reviewer, CandidateSha, "receipt-1", legacyAttribution);
        var actual = ConductorDriver.BuildActionableCandidateRedDecision(
            goal, reviewer, CandidateSha, "receipt-1", attribution);
        Assert.Equal(expected, actual);
        Assert.StartsWith($"ACTIONABLE_CANDIDATE_RED candidate_sha={CandidateSha}; receipt_id=receipt-1; " +
                          $"finding_ids=fixture-red; failing_tests= {Alpha},{Alpha}.  " +
                          "Repair the Developer-owned source/test anchor before any remaining focused evidence or downstream verification runs.",
            actual.Evidence, StringComparison.Ordinal);
        Assert.DoesNotContain("baseline_failures=", actual.Evidence, StringComparison.Ordinal);
    }

    [Fact]
    public void EscalationMessagesNameBothSets()
    {
        foreach (var (baselineFailures, phrase) in new (string[] Failures, string Phrase)[]
                 {
                     ([Alpha, "B"], "is inherited from a RED Baseline arm"),
                     ([], "RED but carried no failing test identities, so inheritance cannot be decided"),
                     (["B"], "has failures absent from the RED Baseline arm and no Developer-owned finding matched them")
                 })
        {
            Assert.True(CandidateRedBaselineInheritance.TryBuildUnattributableRedEscalation(
                CandidateSha, [Arm(FindingEvidenceArm.Candidate, [Alpha]),
                    Arm(FindingEvidenceArm.Baseline, baselineFailures)], Batch(), "receipt-1", out var decision));
            Assert.Equal(FailedGoalFindingObservationKind.FindingOperatorEvidenceRequired, decision.Kind);
            Assert.Contains(phrase, decision.Evidence, StringComparison.Ordinal);
            Assert.Contains($"candidate_failures={Alpha}; baseline_failures=" +
                            (baselineFailures.Length == 0 ? "none" : string.Join(',', baselineFailures)),
                decision.Evidence, StringComparison.Ordinal);
            Assert.Contains("candidate_only_failures=" + (baselineFailures.Contains(Alpha) ? "none" : Alpha),
                decision.Evidence, StringComparison.Ordinal);
            Assert.EndsWith("No worker was dispatched.", decision.Evidence, StringComparison.Ordinal);
            if (!baselineFailures.Contains(Alpha))
            {
                Assert.DoesNotContain("inherited", decision.Evidence, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void PartialOverlapAttributesOnlyCandidateFailuresToMatchingFinding()
    {
        var (_, goal) = SoftwareGoal();
        var alphaFinding = EvidenceFindingWithRequest("Candidate failure", id: "alpha", classes: ["AlphaTests"]);
        var betaFinding = EvidenceFindingWithRequest("Baseline failure", id: "beta", classes: ["BetaTests"]);
        var batch = Batch(alphaFinding, betaFinding);
        const string beta = "Fixture.BetaTests.FailsOnBaseline";
        var attribution = MakeDriver().TryAttributeActionableCandidateRed(goal, CandidateSha,
            [Arm(FindingEvidenceArm.Candidate, [" " + Alpha, beta]),
                Arm(FindingEvidenceArm.Baseline, [beta + " "])], batch);

        Assert.NotNull(attribution);
        Assert.Equal(new[] { alphaFinding }, attribution.Findings);
        Assert.Equal(new[] { Alpha }, attribution.FailingTestIdentities);
        Assert.Equal(new[] { beta }, attribution.BaselineFailingTestIdentities);
    }

    private static CandidateRedBaselineComparison Compare(string[] candidate, string[] baseline) =>
        CandidateRedBaselineInheritance.Compare(Arm(FindingEvidenceArm.Candidate, candidate),
            Arm(FindingEvidenceArm.Baseline, baseline));

    private static FindingEvidenceArmReceipt Arm(FindingEvidenceArm arm, string[] failures,
        FindingEvidenceArmDisposition disposition = FindingEvidenceArmDisposition.Red) =>
        new(arm, arm == FindingEvidenceArm.Candidate ? CandidateSha : "baseline-sha", disposition,
            Accepted: true, Passed: disposition == FindingEvidenceArmDisposition.Green, Summary: "synthetic evidence",
            FailingTestIdentities: failures, TestResultPaths: []);

    private static FindingEvidenceBatch Batch(params ReviewFinding[] findings)
    {
        if (findings.Length == 0)
        {
            findings = [EvidenceFindingWithRequest("Developer-owned fixture", id: "fixture-red", classes: ["AlphaTests"])];
        }
        var members = findings.Select(finding => new FindingEvidenceRequestGroup(
            finding.StableId, "synthetic request", finding.EvidenceRequest!, [finding])).ToArray();
        return new("batch", "synthetic request", members[0].TypedRequest, findings.ToList(), members);
    }
}
