using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class FindingEvidenceContinuationPersistenceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReloadBeforeRetryOrPreparationDoesNotLoseTheContinuation(bool recordClaim)
    {
        using var fixture = new FreshFindingEvidenceFixture();
        fixture.Attach(fixture.Receipt());
        Assert.True(fixture.Select(out var continuation, out var reason), reason);
        if (recordClaim)
            fixture.Kernel.RecordFindingEvidenceReviewContinuation(fixture.Goal.Id, fixture.Reviewer.Id, continuation!);
        fixture.Reload();

        fixture.Driver().AdvanceOnce(fixture.Goal, Mcg.AgentOrchestrator.Core.Conductor.ConductorAutonomyPolicy.Permissive);

        Assert.Equal(1, fixture.Launches);
        Assert.Equal(RetryCause.NewTestFinding, Assert.Single(fixture.Causes));
        Assert.Equal(continuation!.Key, fixture.Reviewer.LastDispatch!.FindingEvidenceReviewContinuation!.Key);
    }

    [Fact]
    public void DuplicateIngestionAndReloadKeepOneSelectedTransition()
    {
        using var fixture = new FreshFindingEvidenceFixture();
        var receipt = fixture.Receipt();
        fixture.Attach(receipt);
        fixture.Attach(receipt);
        Assert.True(fixture.Select(out var first, out var reason), reason);
        fixture.Kernel.RecordFindingEvidenceReviewContinuation(fixture.Goal.Id, fixture.Reviewer.Id, first!);
        fixture.Reload();

        Assert.True(fixture.Select(out var restored, out reason), reason);
        Assert.Equal(first!.Key, restored!.Key);
        Assert.Equal(first.Selections, restored.Selections);
        Assert.Equal(first.Key, fixture.Reviewer.FindingEvidenceReviewContinuation!.Key);
        Assert.Single(fixture.Tester.LastVerification!.FindingEvidenceReceipts!, item => item.ReceiptId == receipt.ReceiptId);
    }

    [Fact]
    public void EquivalentReceiptCopyWasAlreadyDeliveredAndDoesNotAdvanceContext()
    {
        using var fixture = new FreshFindingEvidenceFixture();
        var delivered = fixture.Tester.LastVerification!.FindingEvidenceReceipts!.Single();
        var copy = delivered with { ReceiptId = "copied-receipt", Summary = "new filename timestamp" };
        copy = copy with { RequestDispositions = copy.RequestDispositions!.Select(item => item with { SourceReceiptIds = [copy.ReceiptId] }).ToArray() };
        fixture.Attach(copy);

        Assert.Equal(FindingEvidenceReviewContext.ExecutionIdentity(delivered), FindingEvidenceReviewContext.ExecutionIdentity(copy));
        Assert.False(fixture.Select(out _, out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DurablePreparationAndStartClaimSurviveInterruption(bool claimBeforeReload)
    {
        using var fixture = new FreshFindingEvidenceFixture();
        fixture.Attach(fixture.Receipt());
        Assert.True(fixture.Select(out var continuation, out var reason), reason);
        fixture.Kernel.RecordFindingEvidenceReviewContinuation(fixture.Goal.Id, fixture.Reviewer.Id, continuation!);
        fixture.Kernel.RetryTaskAutomatically(fixture.Goal.Id, fixture.Reviewer.Id, continuation!.Render(), RetryCause.NewTestFinding);
        var database = Path.Combine(fixture.Root, "state.db");
        _ = StateDbMigrations.EnsureUpToDate(database);
        var repository = new SqliteOrchestratorStateRepository(database);
        await repository.SaveAsync(fixture.Kernel);
        var at = FreshFindingEvidenceFixture.Now.AddMinutes(10);
        var fingerprint = new RetryContextFingerprint(RetryContextFingerprint.CurrentSchemaVersion, continuation.Key);
        var prepared = new TaskDispatchRecord("fixture", "review", fixture.Root, at,
            BaseCommit: FreshFindingEvidenceFixture.Candidate, CandidateIdentity: fixture.Identity,
            RetryContextFingerprint: fingerprint, PaidRoute: PaidRouteClassification.Paid,
            FindingEvidenceReviewContext: FindingEvidenceReviewContext.Capture(fixture.Goal, fixture.Reviewer, FreshFindingEvidenceFixture.Candidate, at),
            FindingEvidenceReviewContinuation: continuation);
        var reserved = await RetryAdmissionReservationStore.TryReserveAsync(database, fixture.Goal.Id, fixture.Reviewer.Id,
            fingerprint, PaidRouteClassification.Paid, RetryCause.NewTestFinding, prepared, at, "owner", at.AddMinutes(1));
        Assert.True(reserved!.Admission.AllowsProcessStart);
        if (claimBeforeReload)
            Assert.True(await RetryAdmissionReservationStore.TryClaimStartAsync(database, fixture.Goal.Id, fixture.Reviewer.Id, at, "owner", at));

        var saved = (await repository.LoadGoalAsync(fixture.Goal.Id))!;
        var restored = AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot([saved], [])).Goals.Single().Tasks.Single(task => task.Id == fixture.Reviewer.Id);
        Assert.Equal(continuation.Key, restored.LastDispatch!.FindingEvidenceReviewContinuation!.Key);
        Assert.Equal(continuation.Selections, restored.LastDispatch.FindingEvidenceReviewContinuation.Selections);
        Assert.Equal(continuation.Key, restored.FindingEvidenceReviewContinuation!.Key);
        var claim = await RetryAdmissionReservationStore.TryClaimStartAsync(database, fixture.Goal.Id, fixture.Reviewer.Id, at, "owner", at);
        Assert.Equal(!claimBeforeReload, claim);
        Assert.False(await RetryAdmissionReservationStore.TryClaimStartAsync(database, fixture.Goal.Id, fixture.Reviewer.Id, at, "owner", at));
        var confirmed = await RetryAdmissionReservationStore.TryConfirmStartAsync(database, fixture.Goal.Id, fixture.Reviewer.Id, at, "owner", at);
        Assert.True(confirmed!.Claimed);
        var duplicate = await RetryAdmissionReservationStore.TryReserveAsync(database, fixture.Goal.Id, fixture.Reviewer.Id,
            fingerprint, PaidRouteClassification.Paid, RetryCause.NewTestFinding, prepared with { DispatchedAt = at.AddMinutes(2) },
            at.AddMinutes(2), "another-owner", at.AddMinutes(3));
        Assert.False(duplicate!.Admission.AllowsProcessStart);
    }
}
