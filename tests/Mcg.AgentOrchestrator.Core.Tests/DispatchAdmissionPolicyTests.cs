using Mcg.AgentOrchestrator.Core;

public sealed class DispatchAdmissionPolicyTests
{
    [Xunit.Fact]
    public void BelowCap_WithSliceAllowed_Admits()
    {
        var decision = DispatchAdmissionPolicy.Evaluate(new(0, 4, 9, 0, 4, 4, true));

        Assert.Equal(DispatchAdmissionAction.Admit, decision.Action);
        Assert.Equal(0, decision.DiscriminatingRung);
        Assert.Equal("admitted", decision.DiscriminatingEvidence);
    }

    [Xunit.Fact]
    public void AtUnclampedCap_HoldsWithOriginalReason() => AssertHold(
        new(4, 4, 9, 0, 4, 4), 1, "worker-cap",
        "At worker cap (4/4); will advance when a slot opens");

    [Xunit.Fact]
    public void AtClampedCap_WithReservedGateSlot_HoldsWithOriginalReason() => AssertHold(
        new(8, 9, 9, 1, 8, 9), 1, "reserved-gate-slot",
        "At worker cap (8/8) with a gate-ready goal reserving a stable slot; will advance when a slot opens");

    [Xunit.Fact]
    public void AtClampedCap_WithoutReservation_HoldsWithOriginalReason() => AssertHold(
        new(9, 12, 9, 0, 9, 12), 1, "worker-admission-capacity",
        "At worker admission capacity (9/9); configured cap 12 is clamped; will advance when a slot opens");

    [Xunit.Fact]
    public void BelowCap_WithSliceRefused_HoldsWithSuppliedReason()
    {
        const string reason = "Slice-batch sibling abcdef12 occupies colliding scope ownership:shared-infrastructure:core/application.";
        AssertHold(new(0, 4, 9, 0, 4, 4, false, reason), 2, "slice-batch-admission", reason);
    }

    [Xunit.Fact]
    public void AboveCap_WorkerCapPrecedesSliceRefusal() => AssertHold(
        new(5, 4, 9, 0, 4, 4, false, "Slice refusal."), 1, "worker-cap",
        "At worker cap (5/4); will advance when a slot opens");

    [Xunit.Fact]
    public void RecordedFacts_MissingOrMalformedFact_FailsLoudly()
    {
        var facts = new DispatchAdmissionFacts(9, 12, 9, 0, 9, 12).ToRecordedFacts();
        Assert.Throws<InvalidOperationException>(() => DispatchAdmissionFacts.FromRecordedFacts(facts.Skip(1).ToArray()));
        Assert.Throws<InvalidOperationException>(() => DispatchAdmissionFacts.FromRecordedFacts(
            facts.Select(fact => fact.Name == "running" ? fact with { Value = "invalid" } : fact).ToArray()));
        Assert.Throws<InvalidOperationException>(() => DispatchAdmissionFacts.FromRecordedFacts(
            facts.Select(fact => fact.Name == "sliceBatchAdmission" ? fact with { Value = "invalid" } : fact).ToArray()));
    }

    private static void AssertHold(DispatchAdmissionFacts facts, int rung, string evidence, string reason)
    {
        var decision = DispatchAdmissionPolicy.Evaluate(facts);

        Assert.Equal(DispatchAdmissionAction.Hold, decision.Action);
        Assert.Equal(rung, decision.DiscriminatingRung);
        Assert.Equal(evidence, decision.DiscriminatingEvidence);
        Assert.Equal(reason, decision.Reason);
    }
}
