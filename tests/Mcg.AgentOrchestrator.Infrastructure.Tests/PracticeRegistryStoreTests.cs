using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class PracticeRegistryStoreTests
{
    [Xunit.Fact(DisplayName = "PracticeRegistryStore_seeds_practices_with_provenance")]
    public void PracticeRegistryStoreSeedsPracticesWithProvenance()
    {
        var store = new PracticeRegistryStore(TempDb());

        var practices = store.ListActive();

        Assert.Contains(practices, practice =>
            practice.Id == "classifier-positive-evidence" &&
            practice.ScopePatterns.Contains("classifier") &&
            practice.Provenance.Any(item => item.ReceiptId == "b0807c0e"));
        Assert.Contains(practices, practice =>
            practice.Id == "process-dispatch-hygiene" &&
            practice.Provenance.Any(item => item.ReceiptId == "c86de253"));
    }

    [Xunit.Fact(DisplayName = "PracticeRegistryStore_upsert_preserves_operator_edits_against_seed_replay")]
    public void PracticeRegistryStoreUpsertPreservesOperatorEditsAgainstSeedReplay()
    {
        var db = TempDb();
        var store = new PracticeRegistryStore(db);
        var edited = EngineeringPracticeDefaults.SeedEntries
            .Single(practice => practice.Id == "classifier-positive-evidence")
            with
            {
                Constraint = "operator edited classifier constraint",
                ScopePatterns = ["operator-classifier-scope"]
            };
        store.Upsert(edited);

        var replayed = new PracticeRegistryStore(db);
        var practice = replayed.ListActive().Single(item => item.Id == "classifier-positive-evidence");

        Assert.Equal("operator edited classifier constraint", practice.Constraint);
        Assert.Equal(["operator-classifier-scope"], practice.ScopePatterns);
    }

    [Xunit.Fact(DisplayName = "SqliteOrchestratorStateRepository_loads_registry_snapshot_into_kernel")]
    public async Task SqliteOrchestratorStateRepositoryLoadsRegistrySnapshotIntoKernel()
    {
        var db = TempDb();
        var practiceStore = new PracticeRegistryStore(db);
        practiceStore.Upsert(new EngineeringPractice(
            "custom-dispatch-practice",
            "Custom dispatch practice",
            "operator custom dispatch constraint",
            "operator custom dispatch review",
            ["dispatch"],
            [new EngineeringPracticeProvenance("operator-receipt", "operator promoted finding")],
            Priority: 500));
        var repository = new SqliteOrchestratorStateRepository(db);
        await repository.SaveAsync(new AgentOrchestratorKernel());

        var kernel = await repository.LoadAsync();

        Assert.Contains(kernel.EngineeringPractices, practice => practice.Id == "custom-dispatch-practice");
    }

    private static string TempDb()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mcg-practice-registry-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "state.db");
        _ = StateDbMigrations.EnsureUpToDate(path);
        return path;
    }
}
