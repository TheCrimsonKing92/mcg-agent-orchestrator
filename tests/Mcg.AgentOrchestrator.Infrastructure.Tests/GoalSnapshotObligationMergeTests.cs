using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel safe: each integration test owns and deletes its GUID-named SQLite store.
public sealed class GoalSnapshotObligationMergeTests
{
    private const string CandidateSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string OtherCandidateSha = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string ObligationId = "criterion-v1-0";

    [Fact]
    public async Task TickEvidenceSurvivesConcurrentStoreWrite()
    {
        using var store = new TempStore();
        var repository = new SqliteOrchestratorStateRepository(store.Path);
        var kernel = new AgentOrchestratorKernel();
        var goal = CreateBoundGoal(kernel);
        await repository.SaveAsync(kernel);
        var tick = await repository.LoadAsync();
        var baseline = tick.ExportSnapshot();
        Assert.Equal(CriterionEvidenceState.Pending,
            Assert.Single(tick.GetGoal(goal.Id).CriterionEvidenceObligations).State);

        var second = await repository.LoadAsync();
        second.SetGoalSourceBacklogItemId(goal.Id, "second-kernel-backlog");
        await repository.SaveAsync(second);
        tick.RecordCriterionEvidence(goal.Id, ObligationId, CriterionEvidenceOwner.Acceptance,
            CandidateSha, $"full-acceptance:{CandidateSha}", CriterionEvidenceScopes.FullAcceptanceGate,
            passed: true, detail: "Full acceptance passed on the tick candidate");

        var current = tick.ExportSnapshot();
        var result = Assert.Single(await repository.SaveGoalSnapshotsWithMergeAsync([
            new GoalSnapshotSaveRequest(baseline.Goals.Single(), current.Goals.Single(), current.HumanInputRequests)]));

        Assert.Equal(GoalSnapshotSaveDisposition.Merged, result.Disposition);
        var restored = (await repository.LoadAsync()).GetGoal(goal.Id);
        var obligation = Assert.Single(restored.CriterionEvidenceObligations);
        Assert.Equal(CriterionEvidenceState.Satisfied, obligation.State);
        Assert.Equal($"full-acceptance:{CandidateSha}", obligation.ReceiptId);
        Assert.Equal(CandidateSha, obligation.CandidateSha);
        Assert.Equal("second-kernel-backlog", restored.SourceBacklogItemId);
    }

    [Fact]
    public async Task StoreOnlyObligationChangeSurvivesUnrelatedTickChange()
    {
        using var store = new TempStore();
        var repository = new SqliteOrchestratorStateRepository(store.Path);
        var kernel = new AgentOrchestratorKernel();
        var goal = CreateBoundGoal(kernel, includeSecondCriterion: true);
        await repository.SaveAsync(kernel);
        var tick = await repository.LoadAsync();
        var baseline = tick.ExportSnapshot();
        Assert.Single(tick.GetGoal(goal.Id).CriterionEvidenceObligations);

        var second = await repository.LoadAsync();
        var mapped = second.MapCriterionEvidenceOwner(goal.Id, 1, 1, CriterionEvidenceOwner.Acceptance,
            "operator", expectedCandidateSha: CandidateSha);
        await repository.SaveAsync(second);
        tick.SetGoalPriority(goal.Id, GoalPriority.Critical, "test", "tick");

        var current = tick.ExportSnapshot();
        var result = Assert.Single(await repository.SaveGoalSnapshotsWithMergeAsync([
            new GoalSnapshotSaveRequest(baseline.Goals.Single(), current.Goals.Single(), current.HumanInputRequests)]));

        Assert.Equal(GoalSnapshotSaveDisposition.Merged, result.Disposition);
        var restored = (await repository.LoadAsync()).GetGoal(goal.Id);
        Assert.Equal(GoalPriority.Critical, restored.Priority);
        Assert.Equal(new[] { ObligationId, mapped.Id }, restored.CriterionEvidenceObligations.Select(item => item.Id));
        var obligation = restored.CriterionEvidenceObligations.Single(item => item.Id == mapped.Id);
        Assert.Equal(CriterionEvidenceOwner.Acceptance, obligation.Owner);
        Assert.Equal(CandidateSha, obligation.ExpectedCandidateSha);
        Assert.Equal(CriterionEvidenceState.Pending, obligation.State);
    }

    [Fact]
    public async Task ConflictingObligationChangeKeepsStoredAndNamesTheId()
    {
        using var store = new TempStore();
        var repository = new SqliteOrchestratorStateRepository(store.Path);
        var kernel = new AgentOrchestratorKernel();
        var goal = CreateBoundGoal(kernel);
        await repository.SaveAsync(kernel);
        var tick = await repository.LoadAsync();
        var baseline = tick.ExportSnapshot();

        var second = await repository.LoadAsync();
        second.MapCriterionEvidenceOwner(goal.Id, 0, 1, CriterionEvidenceOwner.Acceptance,
            "operator", expectedCandidateSha: OtherCandidateSha);
        await repository.SaveAsync(second);
        tick.RecordCriterionEvidence(goal.Id, ObligationId, CriterionEvidenceOwner.Acceptance,
            CandidateSha, $"full-acceptance:{CandidateSha}", CriterionEvidenceScopes.FullAcceptanceGate,
            passed: true, detail: "Tick still used the original binding");

        var current = tick.ExportSnapshot();
        var result = Assert.Single(await repository.SaveGoalSnapshotsWithMergeAsync([
            new GoalSnapshotSaveRequest(baseline.Goals.Single(), current.Goals.Single(), current.HumanInputRequests)]));

        Assert.Equal(GoalSnapshotSaveDisposition.Merged, result.Disposition);
        Assert.Contains($"criterion evidence obligation conflict kept stored: {ObligationId}", result.Message,
            StringComparison.Ordinal);
        var obligation = Assert.Single((await repository.LoadAsync()).GetGoal(goal.Id).CriterionEvidenceObligations);
        Assert.Equal(OtherCandidateSha, obligation.ExpectedCandidateSha);
        Assert.Equal(CriterionEvidenceState.Pending, obligation.State);
        Assert.Null(obligation.CandidateSha);
        Assert.Null(obligation.ReceiptId);
    }

    [Fact]
    public void MergeHelperRules()
    {
        var before = Enumerable.Range(0, 7).Select(Obligation).ToArray();
        var tickOnly = before[0] with { Provenance = "tick" };
        var storeOnly = before[1] with { Provenance = "store" };
        var shared = before[2] with { Provenance = "both", PriorReceipts = new List<CriterionEvidenceReceipt>() };
        var sharedCopy = shared with { PriorReceipts = new List<CriterionEvidenceReceipt>() };
        var conflictingStore = before[3] with { Provenance = "store" };
        var conflictingTick = before[3] with { Provenance = "tick" };
        var added = Obligation(7);

        // Deliberately reorder stored entries; baseline-only entries 5 and 6 are removed by both sides.
        var result = GoalSnapshotObligationMerge.Merge(before,
            [storeOnly, before[0], shared, conflictingStore, before[4]],
            [tickOnly, before[1], sharedCopy, conflictingTick, added]);

        Assert.Equal(new[] { storeOnly, tickOnly, shared, conflictingStore, added }, result.Obligations);
        Assert.Equal(new[] { before[3].Id }, result.ConflictIds);
        Assert.DoesNotContain(result.Obligations!, item => item.Id == before[4].Id);

        IReadOnlyList<CriterionEvidenceObligation> unchanged = [before[0]];
        var noChanges = GoalSnapshotObligationMerge.Merge(unchanged, unchanged, [before[0] with { }]);
        Assert.Same(unchanged, noChanges.Obligations);
        Assert.Empty(noChanges.ConflictIds);
        var storeChanged = GoalSnapshotObligationMerge.Merge(unchanged, [tickOnly], unchanged);
        Assert.Equal(new[] { tickOnly }, storeChanged.Obligations);
        Assert.Empty(storeChanged.ConflictIds);

        var bothNull = GoalSnapshotObligationMerge.Merge(null, null, null);
        Assert.Null(bothNull.Obligations);
        Assert.Empty(bothNull.ConflictIds);
        var removed = GoalSnapshotObligationMerge.Merge(unchanged, unchanged, null);
        Assert.Null(removed.Obligations);
        Assert.Empty(removed.ConflictIds);
        var inserted = GoalSnapshotObligationMerge.Merge(null, null, [added]);
        Assert.Equal(new[] { added }, inserted.Obligations);
        Assert.Empty(inserted.ConflictIds);

        var deletedInStore = GoalSnapshotObligationMerge.Merge(unchanged, null, [tickOnly]);
        Assert.Null(deletedInStore.Obligations);
        Assert.Equal(new[] { before[0].Id }, deletedInStore.ConflictIds);
        var deletedInTick = GoalSnapshotObligationMerge.Merge(unchanged, [tickOnly], null);
        Assert.Equal(new[] { tickOnly }, deletedInTick.Obligations);
        Assert.Equal(new[] { before[0].Id }, deletedInTick.ConflictIds);
    }

    private static Goal CreateBoundGoal(AgentOrchestratorKernel kernel, bool includeSecondCriterion = false)
    {
        var goal = kernel.CreateGoal("Merge criterion evidence");
        const string criterion = "Acceptance verifies the candidate";
        kernel.RecordGoalRefinement(goal.Id, new RefinedSpec(goal.Objective,
            includeSecondCriterion ? [criterion, "Second criterion"] : [criterion],
            VerificationClass.TestVerifiable, [], [])
        {
            AcceptanceGateOwnedAcceptanceCriteria = [criterion]
        });
        kernel.MapCriterionEvidenceOwner(goal.Id, 0, 1, CriterionEvidenceOwner.Acceptance,
            "operator", expectedCandidateSha: CandidateSha);
        return goal;
    }

    private static CriterionEvidenceObligation Obligation(int index) => new(
        CriterionEvidenceObligation.BuildId(1, index), index, 1, $"Criterion {index}",
        CriterionEvidenceOwner.Acceptance, CriterionEvidenceState.Pending,
        CriterionEvidenceScopes.FullAcceptanceGate, "baseline", DateTimeOffset.UnixEpoch,
        ExpectedCandidateSha: CandidateSha);

    private sealed class TempStore : IDisposable
    {
        private readonly string _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            $"mcg-obligation-merge-{Guid.NewGuid():N}");

        public TempStore()
        {
            Directory.CreateDirectory(_directory);
            Path = System.IO.Path.Combine(_directory, "state.db");
            StateDbMigrations.EnsureUpToDate(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(_directory, recursive: true);
    }
}
