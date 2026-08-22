using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

public sealed class PlannerCandidateDivergencePersistenceTests
{
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
            []);
        var snapshot = new TaskVerificationSnapshot(
            "planner", "C:\\work", 0, "ok", "", DateTimeOffset.UtcNow,
            PlannerCandidateDivergence: receipt);

        var restored = JsonSerializer.Deserialize<TaskVerificationSnapshot>(JsonSerializer.Serialize(snapshot));

        var divergence = Xunit.Assert.IsType<PlannerCandidateDivergenceReceipt>(restored!.PlannerCandidateDivergence);
        var section = Xunit.Assert.Single(divergence.Sections);
        Xunit.Assert.Equal("integration seams", section.Section);
        Xunit.Assert.Contains(section.Candidates, candidate =>
            candidate.CandidateIndex == 1 && candidate.DivergentFeatures.Contains("code:consensus-seam"));
    }
}
