using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core.Conductor;

public sealed class FindingEvidenceContinuationPersistenceTests
{
    [Theory]
    [InlineData("intact", false)]
    [InlineData("missing", false)]
    [InlineData("corrupt", false)]
    [InlineData("missing", true)]
    [InlineData("corrupt", true)]
    public void EquivalentCopyCannotMaskUnavailableClaimedArtifactsAfterReload(string original, bool prepared)
    {
        using var fixture = new FreshFindingEvidenceFixture();
        var receipt = fixture.Receipt();
        fixture.Attach(receipt);
        Assert.True(fixture.Select(out var claimed, out var reason), reason);
        fixture.Kernel.RecordFindingEvidenceReviewContinuation(fixture.Goal.Id, fixture.Reviewer.Id, claimed!);
        fixture.Kernel.RetryTaskAutomatically(fixture.Goal.Id, fixture.Reviewer.Id, claimed!.Render(), RetryCause.NewTestFinding);
        if (prepared)
            fixture.Kernel.RecordTaskDispatch(fixture.Goal.Id, fixture.Reviewer.Id,
                new TaskDispatchRecord("fixture", "review", fixture.Root, FreshFindingEvidenceFixture.Now.AddMinutes(10),
                    CandidateIdentity: fixture.Identity, FindingEvidenceReviewContinuation: claimed));
        var copy = fixture.Receipt(id: "equivalent-copy");
        fixture.Attach(copy);
        Assert.Equal(FindingEvidenceReviewContext.ExecutionIdentity(receipt), FindingEvidenceReviewContext.ExecutionIdentity(copy));
        var path = receipt.Arms!.Single().TestResultPaths!.Single();
        if (original == "missing") File.Delete(path);
        if (original == "corrupt") File.AppendAllText(path, "corrupt");
        fixture.Reload();
        Assert.True(fixture.Select(out var copySelection, out reason), reason);
        Assert.Equal(copy.ReceiptId, Assert.Single(copySelection!.Selections).ReceiptId);
        var driver = fixture.Driver();

        var valid = FindingEvidenceContinuationCoordinator.TrySelectFindingEvidenceContinuation(driver,
            fixture.Goal, fixture.Reviewer, out var selected, out reason);

        Assert.Equal(original == "intact", valid);
        if (valid) Assert.Equal(receipt.ReceiptId, Assert.Single(selected!.Selections).ReceiptId);
        else Assert.Equal("claimed-review-evidence-unavailable", reason);
        var result = driver.AdvanceOnce(fixture.Goal, ConductorAutonomyPolicy.Permissive);
        Assert.Equal(valid ? 1 : 0, fixture.Launches);
        if (!valid)
        {
            var held = Assert.IsType<ConductorAdvanceOutcome.Held>(result.Outcome);
            Assert.Equal(prepared
                ? "Finding evidence continuation held: prepared dispatch evidence no longer qualifies."
                : "Finding evidence continuation held: selected evidence no longer qualifies for the recorded review decision.", held.Reason);
            var decision = Assert.IsType<PolicyDecisionRecord>(held.Decision);
            Assert.Equal("finding-evidence-continuation", decision.Stage);
            Assert.Equal("Hold", decision.Action);
            Assert.Equal("claimed-review-evidence-unavailable", decision.DiscriminatingEvidence);
            Assert.Equal(held.Reason, decision.Reason);
            var facts = decision.Facts.ToDictionary(fact => fact.Name, fact => fact.Value);
            Assert.Equal(fixture.Goal.Id.Value, facts["goalId"]);
            Assert.Equal(fixture.Reviewer.Id.Value, facts["taskId"]);
            Assert.Equal(FreshFindingEvidenceFixture.Candidate, facts["candidateSha"]);
            Assert.Equal(fixture.Identity.Canonical, facts["candidateIdentity"]);
            Assert.Equal(claimed.DecisionKey, facts["decisionKey"]);
            Assert.Equal(claimed.Key, facts["continuationKey"]);
            Assert.Equal(claimed.PriorVerificationAt.ToString("O"), facts["priorVerificationAt"]);
            Assert.Equal(claimed.PriorDispatchAt.ToString("O"), facts["priorDispatchAt"]);
            Assert.Equal(System.Text.Json.JsonSerializer.Serialize(claimed.Selections), facts["selections"]);
        }
    }

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
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task DurablePreparationAndStartClaimSurviveInterruption(bool claimBeforeReload, bool differentFingerprint)
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
        var duplicateFingerprint = differentFingerprint
            ? fingerprint with { Value = fingerprint.Value + "-another-preparation" }
            : fingerprint;
        var duplicateDispatch = prepared with { DispatchedAt = at.AddMinutes(2), RetryContextFingerprint = duplicateFingerprint };
        var duplicateBeforeLaunch = await RetryAdmissionReservationStore.TryReserveAsync(database, fixture.Goal.Id, fixture.Reviewer.Id,
            duplicateFingerprint, PaidRouteClassification.Paid, RetryCause.NewTestFinding, duplicateDispatch,
            at.AddMinutes(2), "another-owner", at.AddMinutes(3));
        Assert.False(duplicateBeforeLaunch!.Admission.AllowsProcessStart);
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
        var sameDispatchDuplicate = await RetryAdmissionReservationStore.TryReserveAsync(database, fixture.Goal.Id, fixture.Reviewer.Id,
            duplicateFingerprint, PaidRouteClassification.Paid, RetryCause.NewTestFinding, duplicateDispatch with { DispatchedAt = at },
            at.AddMinutes(2), "another-owner", at.AddMinutes(3));
        Assert.False(sameDispatchDuplicate!.Admission.AllowsProcessStart);
        var duplicate = await RetryAdmissionReservationStore.TryReserveAsync(database, fixture.Goal.Id, fixture.Reviewer.Id,
            duplicateFingerprint, PaidRouteClassification.Paid, RetryCause.NewTestFinding, duplicateDispatch,
            at.AddMinutes(2), "another-owner", at.AddMinutes(3));
        Assert.False(duplicate!.Admission.AllowsProcessStart);
        var afterDuplicate = (await repository.LoadGoalAsync(fixture.Goal.Id))!.Tasks.Single(task => task.Id == fixture.Reviewer.Id.Value);
        Assert.Equal(at, afterDuplicate.LastDispatch!.DispatchedAt);
        Assert.Single(afterDuplicate.RetryAdmissionHistory!.Where(receipt =>
            receipt.Decision is RetryAdmissionDecision.Allowed or RetryAdmissionDecision.ResumedReservation));
    }
}
