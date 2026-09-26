using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

public sealed class ConductorSpeculativeAcceptanceCohortPlannerTestsOmissions
{
    [Fact]
    public void OverflowKeepsCapacityAndCountsAllUnrenderedOutcomes()
    {
        var deferrals = Enumerable.Range(1, 3)
            .Select(index => (ConductorSpeculativeAcceptanceDisposition)new ConductorSpeculativeAcceptanceDisposition.Deferred(
                Id(index), ConductorSpeculativeAcceptanceDeferralReason.InsufficientCompatiblePeers));
        var exclusions = Enumerable.Range(4, 6)
            .Select(index => (ConductorSpeculativeAcceptanceDisposition)new ConductorSpeculativeAcceptanceDisposition.Excluded(
                Id(index), new ConductorSpeculativeAcceptanceExclusionEvidence.Upstream(
                    GateReadyCandidateExclusionReason.RevisionUnknown)));
        var upstream = Enumerable.Range(10, 20)
            .Select(index => new ConductorSpeculativeUpstreamExclusion(
                Id(index), ConductorSpeculativeUpstreamExclusionReason.DependencyHold)).ToArray();
        var plan = new ConductorSpeculativeAcceptancePlan(0, [], [.. deferrals, .. exclusions]);

        var receipt = plan.FormatReceipt(7,
            new ConductorSpeculativeCohortReceiptContext(upstream, 1, ["goal:cccccccc"]));

        Assert.Contains("capacity=AcceptanceWidthOccupied(width=1,occupants=goal:cccccccc)", receipt);
        Assert.Contains("omitted=21", receipt);
        Assert.DoesNotContain(":DependencyHold", receipt);
        Assert.DoesNotContain('\n', receipt);
        Assert.True(receipt.Length <= 1024);
    }

    [Fact]
    public void LongEvidenceDropsEntriesBeforeTheCharacterCap()
    {
        var exclusions = Enumerable.Range(1, 8)
            .Select(index => (ConductorSpeculativeAcceptanceDisposition)new ConductorSpeculativeAcceptanceDisposition.Excluded(
                Id(index), new ConductorSpeculativeAcceptanceExclusionEvidence.LandingPathOverlap(
                    Id(99), new string('a', 96), new string('b', 96)))).ToArray();
        var plan = new ConductorSpeculativeAcceptancePlan(0, [], exclusions);

        var receipt = plan.FormatReceipt(7,
            new ConductorSpeculativeCohortReceiptContext([], 1, ["goal:cccccccc"]));

        Assert.Contains("omitted=", receipt);
        Assert.DoesNotContain("truncated=true", receipt);
        Assert.True(receipt.Length <= 1024);
    }

    private static GoalId Id(int value) => new(value.ToString("x8").PadRight(32, '0'));
}
