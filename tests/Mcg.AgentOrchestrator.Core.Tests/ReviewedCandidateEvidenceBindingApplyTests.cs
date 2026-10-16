using Mcg.AgentOrchestrator.Core;

public sealed class ReviewedCandidateEvidenceBindingApplyTests
{
    private static readonly DateTimeOffset OriginalTime = new(2026, 10, 14, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset ReviewTime = OriginalTime.AddDays(1);

    [Theory]
    [InlineData(CriterionEvidenceOwner.Operator, CriterionEvidenceState.Pending)]
    [InlineData(CriterionEvidenceOwner.Operator, CriterionEvidenceState.Satisfied)]
    [InlineData(CriterionEvidenceOwner.Unknown, CriterionEvidenceState.Pending)]
    [InlineData(CriterionEvidenceOwner.Unknown, CriterionEvidenceState.Repaired)]
    [InlineData(CriterionEvidenceOwner.Acceptance, CriterionEvidenceState.Repaired)]
    public void OtherOwnersAndRepairedObligationsRemainUnchanged(
        CriterionEvidenceOwner owner, CriterionEvidenceState state)
    {
        var obligation = Obligation(owner, state);

        Assert.Equal(obligation, ReviewedCandidateEvidenceBinding.Apply(obligation, "B", ReviewTime));
    }

    [Fact]
    public void OtherScopeRemainsUnchanged()
    {
        var obligation = Obligation() with { RequiredScope = "operator:live-proof" };

        Assert.Equal(obligation, ReviewedCandidateEvidenceBinding.Apply(obligation, "B", ReviewTime));
    }

    [Fact]
    public void PendingBindingKeepsFindingAndHistoryAndRefreshesRecordedTime()
    {
        var history = new[] { new CriterionEvidenceReceipt(CriterionEvidenceOwner.Acceptance,
            "prior", "full-acceptance:prior", CriterionEvidenceScopes.FullAcceptanceGate,
            true, "Prior gate passed", "operator", OriginalTime) };
        var obligation = Obligation() with { PriorReceipts = history };

        var bound = ReviewedCandidateEvidenceBinding.Apply(obligation, "B", ReviewTime);

        Assert.Equal(obligation with { ExpectedCandidateSha = "B", RecordedAt = ReviewTime }, bound);
        Assert.Same(history, bound.PriorReceipts);
        Assert.Equal(bound with { RecordedAt = ReviewTime.AddMinutes(1) },
            ReviewedCandidateEvidenceBinding.Apply(bound, "B", ReviewTime.AddMinutes(1)));
    }

    [Theory]
    [InlineData(CriterionEvidenceState.Satisfied)]
    [InlineData(CriterionEvidenceState.Failed)]
    public void ReceiptOnReviewedCandidateIsUnchangedIgnoringCase(CriterionEvidenceState state)
    {
        var obligation = Obligation(state: state);

        Assert.Equal(obligation, ReviewedCandidateEvidenceBinding.Apply(obligation, "a", ReviewTime));
    }

    [Theory]
    [InlineData(CriterionEvidenceState.Satisfied)]
    [InlineData(CriterionEvidenceState.Failed)]
    public void DifferentReviewedCandidateArchivesReceiptWithoutMovingEvidence(CriterionEvidenceState state)
    {
        var obligation = Obligation(state: state);

        var reopened = ReviewedCandidateEvidenceBinding.Apply(obligation, "B", ReviewTime);

        Assert.Equal(obligation with
        {
            State = CriterionEvidenceState.Pending,
            CandidateSha = null,
            ReceiptId = null,
            Detail = null,
            ExpectedCandidateSha = "B",
            RecordedAt = ReviewTime,
            PriorReceipts = reopened.PriorReceipts
        }, reopened);
        var archived = Assert.Single(reopened.PriorReceipts!);
        Assert.Equal(new CriterionEvidenceReceipt(obligation.Owner, "A", "full-acceptance:A",
            obligation.RequiredScope, state == CriterionEvidenceState.Satisfied, obligation.Detail!,
            obligation.Provenance, OriginalTime), archived);
        Assert.True(reopened.HasValidEvidenceState);
    }

    private static CriterionEvidenceObligation Obligation(
        CriterionEvidenceOwner owner = CriterionEvidenceOwner.Acceptance,
        CriterionEvidenceState state = CriterionEvidenceState.Pending) => new(
            CriterionEvidenceObligation.BuildId(1, 0), 0, 1, "Full gate passes", owner, state,
            CriterionEvidenceScopes.FullAcceptanceGate, "operator", OriginalTime,
            CandidateSha: state is CriterionEvidenceState.Satisfied or CriterionEvidenceState.Failed ? "A" : null,
            ReceiptId: state is CriterionEvidenceState.Satisfied or CriterionEvidenceState.Failed ? "full-acceptance:A" : null,
            Detail: state is CriterionEvidenceState.Satisfied or CriterionEvidenceState.Failed ? "Gate result" : null,
            FindingStableId: "gate-proof", ExpectedCandidateSha: "A");
}
