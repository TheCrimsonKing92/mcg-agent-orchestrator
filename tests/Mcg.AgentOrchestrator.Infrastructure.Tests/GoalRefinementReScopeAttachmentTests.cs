using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class GoalRefinementReScopeAttachmentTests
{
    [Fact]
    public async Task Process_ReScope_PersistsCarriedOwnershipAndPreservesStoredEvidence()
    {
        // Parallel-safe: this scenario owns its SQLite seed and collaboration store.
        const string unchanged = "Assert deterministic mapper coverage.";
        const string candidate = "candidate-rescope";
        using var scenario = new GoalRefinementReScopeVersioningTests.Scenario(
            GoalRefinementReScopeVersioningTests.Original, unchanged);
        await scenario.Service.RefineAsync(scenario.Kernel, scenario.Goal.Id);
        var firstVersion = Assert.Single(scenario.Goal.RefinedSpecVersions);
        var question = Assert.Single(scenario.Goal.RefinedSpec!.OpenQuestions);
        scenario.Kernel.MapCriterionEvidenceOwner(scenario.Goal.Id, 0, 1,
            CriterionEvidenceOwner.Operator, "operator", requiredScope: "manual:benchmark", expectedCandidateSha: candidate);
        scenario.Kernel.MapCriterionEvidenceOwner(scenario.Goal.Id, 1, 1,
            CriterionEvidenceOwner.Acceptance, "operator", expectedCandidateSha: candidate);
        scenario.Kernel.RecordCriterionEvidence(scenario.Goal.Id, "criterion-v1-1",
            CriterionEvidenceOwner.Acceptance, candidate, "receipt-before-attachment",
            CriterionEvidenceScopes.FullAcceptanceGate, passed: true, detail: "Seed gate passed.");
        var storedObligations = scenario.Goal.CriterionEvidenceObligations.ToArray();
        var repository = CreateMigratedStateRepository(scenario.Workspace.SqliteStatePath);
        await repository.SaveAsync(scenario.Kernel);
        var seed = (await repository.LoadAsync()).GetGoal(scenario.Goal.Id);
        Assert.Equal(1, Assert.Single(seed.RefinedSpecVersions).Version);
        Assert.Equal(storedObligations, seed.CriterionEvidenceObligations);
        Assert.True(await scenario.Store.TryResolveAsync(question.Id,
            $"re-scope: {GoalRefinementReScopeVersioningTests.Replacement}"));
        var enqueued = await repository.EnsureOutboxMessageAsync(
            GoalRefinementWorkCoordinator.CreateMessage(scenario.Goal.Id));
        Assert.Equal(GoalRefinementWorkCoordinator.MessageId(scenario.Goal.Id), enqueued.State.Message.Id);

        var result = await GoalRefinementWorkCoordinator.ProcessAsync(repository,
            scenario.Workspace, scenario.Providers, WorkerProfileCatalog.Default(), scenario.Goal.Id);

        Assert.True(result.Claimed);
        Assert.True(result.Attached);
        var reloaded = (await repository.LoadAsync()).GetGoal(scenario.Goal.Id);
        Assert.Equal(2, reloaded.RefinedSpecVersions.Count);
        Assert.Equal(2, reloaded.RefinedSpecVersions[0].SupersededByVersion);
        Assert.Equal(firstVersion.RecordedAt, reloaded.RefinedSpecVersions[0].RecordedAt);
        Assert.Equal([GoalRefinementReScopeVersioningTests.Original, unchanged],
            reloaded.RefinedSpecVersions[0].Spec.AcceptanceCriteria);
        Assert.Equal([GoalRefinementReScopeVersioningTests.Replacement, unchanged],
            reloaded.RefinedSpecVersions[1].Spec.AcceptanceCriteria);
        Assert.Equal(4, reloaded.CriterionEvidenceObligations.Count);
        foreach (var stored in storedObligations)
            Assert.Equal(stored, Assert.Single(reloaded.CriterionEvidenceObligations, item => item.Id == stored.Id));

        var carried = Assert.Single(reloaded.CriterionEvidenceObligations, item => item.Id == "criterion-v2-1");
        Assert.Equal(unchanged, carried.Criterion);
        Assert.Equal(CriterionEvidenceOwner.Acceptance, carried.Owner);
        Assert.Equal("operator mapping by operator; carried forward from criterion-v1-1", carried.Provenance);
        Assert.Equal(candidate, carried.ExpectedCandidateSha);
        Assert.Equal(CriterionEvidenceState.Pending, carried.State);
        Assert.Null(carried.CandidateSha);
        Assert.Null(carried.ReceiptId);
        Assert.Null(carried.Detail);
        var rewritten = Assert.Single(reloaded.CriterionEvidenceObligations, item => item.Id == "criterion-v2-0");
        Assert.Equal(GoalRefinementReScopeVersioningTests.Replacement, rewritten.Criterion);
        Assert.Equal(CriterionEvidenceOwner.Unknown, rewritten.Owner);
        Assert.Equal(CriterionEvidenceState.Pending, rewritten.State);
        Assert.Equal("ownership carried from criterion-v1-0 (prior owner Operator); unresolved during refinement v2; ownership mapping required",
            rewritten.Provenance);
    }
}
