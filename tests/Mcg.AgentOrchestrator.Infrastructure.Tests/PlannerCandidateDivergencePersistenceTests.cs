using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

public sealed class PlannerCandidateDivergencePersistenceTests
{
    [Xunit.Fact]
    public void LegacyReceiptJson_MissingCandidateEvidence_RemainsReadable()
    {
        var json = """
            {
              "CandidateCount": 2,
              "SelectedCandidateIndex": 1,
              "SelectionSignal": "orchestrator-peer-agreement-jaccard-v1",
              "CandidateScores": [0.2, 0.8],
              "Sections": [],
              "Diagnostics": []
            }
            """;

        var restored = JsonSerializer.Deserialize<PlannerCandidateDivergenceReceipt>(json);

        Xunit.Assert.NotNull(restored);
        Xunit.Assert.Equal(1, restored.SelectedCandidateIndex);
        Xunit.Assert.Null(restored.Candidates);
        Xunit.Assert.Equal("legacy-selection", restored.SelectionReason);
    }

    [Xunit.Fact]
    public void VerificationSnapshotRoundTripPreservesSectionAndCandidateDetails()
    {
        var receipt = new PlannerCandidateDivergenceReceipt(
            3,
            1,
            "orchestrator-peer-agreement-jaccard-v1",
            [0.2, 0.8, 0.8],
            [new PlannerSectionDivergence(
                "integration seams",
                ["word:dispatch"],
                [new PlannerCandidateSectionFeatures(0, "hash-0", ["code:first-seam"]),
                 new PlannerCandidateSectionFeatures(1, "hash-1", ["code:consensus-seam"])])],
            [],
            "structural-quality",
            null,
            [new PlannerCandidateEvidenceReceipt(
                1,
                new string('a', 64),
                new string('b', 64),
                PlannerCandidateTerminalState.Succeeded,
                PlannerCandidateNormalizationState.Normalized,
                PlannerCandidateContractVerdict.Valid,
                1_234,
                new PlannerCandidateUsageReceipt(PlannerCandidateUsageState.Reported, 11, 7, 5, null),
                new PlannerStructuralQualityVector(1, 1, 1, 1, 1, 1),
                string.Empty)]);
        var snapshot = new TaskVerificationSnapshot(
            "planner", "C:\\work", 0, "ok", "", DateTimeOffset.UtcNow,
            PlannerCandidateDivergence: receipt);

        var restored = JsonSerializer.Deserialize<TaskVerificationSnapshot>(JsonSerializer.Serialize(snapshot));

        var divergence = Xunit.Assert.IsType<PlannerCandidateDivergenceReceipt>(restored!.PlannerCandidateDivergence);
        var section = Xunit.Assert.Single(divergence.Sections);
        Xunit.Assert.Equal("integration seams", section.Section);
        Xunit.Assert.Contains(section.Candidates, candidate =>
            candidate.CandidateIndex == 1 && candidate.DivergentFeatures.Contains("code:consensus-seam"));
        var candidate = Xunit.Assert.Single(divergence.Candidates!);
        Xunit.Assert.Equal(PlannerCandidateNormalizationState.Normalized, candidate.NormalizationState);
        Xunit.Assert.Equal(1_234, candidate.ElapsedMilliseconds);
        Xunit.Assert.Equal(11, candidate.ProviderUsage.InputTokens);
        Xunit.Assert.Equal("structural-quality", divergence.SelectionReason);
    }
}
