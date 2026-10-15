using System.Security.Cryptography;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

// Parallel-safe: each instance owns every real portfolio and receipt in its private temporary root.
public sealed class EpicOutcomeStoreTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "epic-outcome-" + Guid.NewGuid().ToString("n"));
    private string PathFor(string name) => Path.Combine(root, name);
    private static EpicOutcomeAttribution Owner => new("owner", OperatorActorKind.Human, "cli", AuthorizationTier.Mutate);
    private static EpicOutcomeDefinition Definition(long version = 0) => new(1, version, "Observed service outcome",
        [new("service", "The owner observes the required service behavior", "file-sha256", "owner-receipt")]);

    [Fact]
    public async Task Definition_and_observations_reload_bound_to_version_and_preserve_history()
    {
        var (store, epic) = await Create();
        await store.AddDecisionAsync(epic.Id, "Keep the existing decision", "owner");
        Assert.False(await store.DefineOutcomeAsync(epic.Id, Definition(), "define-1", Owner));
        var evidence = Evidence(1);
        Assert.False(await store.RecordOutcomeAsync(epic.Id, evidence, "receipt-1", Owner));
        Assert.True(await store.DefineOutcomeAsync(epic.Id, Definition(), "define-1", Owner));
        Assert.True(await store.RecordOutcomeAsync(epic.Id, evidence, "receipt-1", Owner));
        var loaded = await EpicPlanStore.OpenReadOnly(PathFor("portfolio.db")).LoadAsync(epic.Id);
        var history = Assert.IsType<EpicOutcomeHistory>(loaded.Outcome);
        Assert.Equal(1, history.Version);
        Assert.Equal("service", Assert.Single(history.Current!.Requirements).Id);
        var observation = Assert.Single(history.Observations);
        Assert.Equal(1, observation.BarVersion);
        Assert.Equal("receipt-1", observation.ReceiptId);
        Assert.Equal("owner", observation.Actor);
        Assert.Equal("cli", observation.Channel);
        Assert.Equal(File.ReadAllBytes(evidence.Source.Locator), Convert.FromBase64String(observation.SnapshotBase64));
        Assert.Equal("Keep the existing decision", Assert.Single(loaded.Decisions).Text);
        Assert.Equal("Preserved description", (await PortfolioStore.OpenReadOnly(PathFor("portfolio.db")).ResolveEpicAsync(epic.Id))!.Description);
        Assert.False(await store.DefineOutcomeAsync(epic.Id, Definition(1), "define-2", Owner));
        loaded = await store.LoadAsync(epic.Id);
        Assert.Equal(2, loaded.Outcome!.Version);
        Assert.Equal(1, Assert.Single(loaded.Outcome.Observations).BarVersion);
        Assert.Equal(2, loaded.Outcome.Revisions.Count);
    }

    [Fact]
    public async Task Refused_writes_leave_revisions_observations_and_decisions_unchanged()
    {
        var (store, epic) = await Create();
        await store.AddDecisionAsync(epic.Id, "Preserved");
        await store.DefineOutcomeAsync(epic.Id, Definition(), "definition", Owner);
        await store.RecordOutcomeAsync(epic.Id, Evidence(1), "receipt", Owner);
        var before = await State(store, epic.Id);
        Assert.Equal("outcome-stale-version", (await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.DefineOutcomeAsync(epic.Id, Definition(), "stale", Owner))).Message);
        await Assert.ThrowsAsync<ArgumentException>(() => store.DefineOutcomeAsync(epic.Id,
            Definition(1) with { Requirements = [Definition().Requirements[0], Definition().Requirements[0]] }, "malformed", Owner));
        Assert.Equal("outcome-request-payload-mismatch", (await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.DefineOutcomeAsync(epic.Id, Definition() with { DisplayText = "changed" }, "definition", Owner))).Message);
        Assert.Equal("tier-below-mutate", (await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.SetBarAsync(epic.Id, "bypass", Owner with { Authorization = AuthorizationTier.Read }))).Message);
        Assert.Equal("outcome-stale-observation-sequence", (await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.RecordOutcomeAsync(epic.Id, Evidence(1), "stale-sequence", Owner))).Message);
        var missing = Evidence(1) with { ExpectedObservationSequence = 1 };
        File.Delete(missing.Source.Locator);
        Assert.Equal("outcome-missing-receipt", (await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.RecordOutcomeAsync(epic.Id, missing, "missing", Owner))).Message);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.DefineOutcomeAsync("absent", Definition(), "absent", Owner));
        Assert.Equal(before, await State(store, epic.Id));
    }

    [Fact]
    public async Task Compatibility_bar_changes_invalidate_proof_once_and_keep_old_receipts()
    {
        var (store, epic) = await Create();
        await store.SetBarAsync(epic.Id, "legacy prose");
        Assert.Empty((await store.LoadAsync(epic.Id)).Outcome!.Current!.Requirements);
        await store.DefineOutcomeAsync(epic.Id, Definition(1), "defined", Owner);
        await store.RecordOutcomeAsync(epic.Id, Evidence(2), "proof", Owner);
        await store.SetBarAsync(epic.Id, "changed prose", Owner);
        var changed = await store.LoadAsync(epic.Id);
        Assert.Equal("changed prose", changed.Bar);
        Assert.Equal(3, changed.Outcome!.Version);
        Assert.Empty(changed.Outcome.Current!.Requirements);
        Assert.Equal(2, Assert.Single(changed.Outcome.Observations).BarVersion);
        await store.SetBarAsync(epic.Id, "changed prose", Owner);
        Assert.Equal(3, (await store.LoadAsync(epic.Id)).Outcome!.Version);
        await store.SetBarAsync(epic.Id, "", Owner);
        Assert.Null((await store.LoadAsync(epic.Id)).Bar);
        Assert.Equal(4, (await store.LoadAsync(epic.Id)).Outcome!.Version);
    }

    [Fact]
    public async Task Frozen_v2_portfolio_migrates_additively_without_approving_any_outcome()
    {
        Directory.CreateDirectory(root);
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = PathFor("portfolio.db"), Pooling = false }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            // Frozen pre-outcome tables, independent of the current schema constructor.
            command.CommandText = """
                CREATE TABLE epics (id TEXT PRIMARY KEY, title TEXT NOT NULL, project_id TEXT NULL,
                    created_at TEXT NOT NULL, created_by TEXT NOT NULL, updated_at TEXT NOT NULL, updated_by TEXT NOT NULL,
                    description TEXT NULL, bar TEXT NULL);
                CREATE TABLE epic_plan_items (id INTEGER PRIMARY KEY AUTOINCREMENT, epic_id TEXT NOT NULL,
                    position INTEGER NOT NULL, kind TEXT NOT NULL, backlog_item_id TEXT NULL, text TEXT NULL,
                    done INTEGER NOT NULL DEFAULT 0, created_at TEXT NOT NULL, created_by TEXT NOT NULL);
                CREATE TABLE epic_decisions (id INTEGER PRIMARY KEY AUTOINCREMENT, epic_id TEXT NOT NULL,
                    decided_at TEXT NOT NULL, text TEXT NOT NULL, decided_by TEXT NULL);
                CREATE TABLE store_schema_versions (store_name TEXT NOT NULL PRIMARY KEY, version INTEGER NOT NULL, applied_at TEXT NOT NULL) WITHOUT ROWID;
                INSERT INTO store_schema_versions VALUES ('portfolio', 2, '2026-01-01T00:00:00Z');
                INSERT INTO epics VALUES ('legacy', 'Legacy', NULL, '2026-01-01T00:00:00Z', 'owner', '2026-01-01T00:00:00Z', 'owner', 'Description', 'Narrative bar');
                INSERT INTO epic_plan_items (epic_id,position,kind,text,done,created_at,created_by) VALUES ('legacy',1,'step','Preserved step',1,'2026-01-01T00:00:00Z','owner');
                INSERT INTO epic_decisions (epic_id,decided_at,text,decided_by) VALUES ('legacy','2026-01-01T00:00:00Z','Preserved decision','owner');
                """;
            command.ExecuteNonQuery();
            Assert.Equal(2, StoreSchemaVersions.Read(connection, "portfolio"));
        }
        Assert.Throws<InvalidOperationException>(() => EpicPlanStore.OpenReadOnly(PathFor("portfolio.db")));
        var before = File.ReadAllBytes(PathFor("portfolio.db"));
        var (legacyEpic, planless) = EpicPlanWritePreparation.Resolve(PathFor("portfolio.db"), "legacy");
        Assert.False(planless);
        Assert.Throws<ArgumentException>(() => EpicPlanWritePreparation.ValidateVersion2(PathFor("portfolio.db"), legacyEpic,
            "epic-plan-move", null, null, 2, 1));
        EpicPlanWritePreparation.ValidateVersion2(PathFor("portfolio.db"), legacyEpic, "epic-plan-add", null, 2, 0, 0);
        Assert.Equal(before, File.ReadAllBytes(PathFor("portfolio.db")));
        PortfolioStore.Setup(PathFor("portfolio.db"));
        PortfolioStore.Setup(PathFor("portfolio.db"));
        var loaded = await EpicPlanStore.OpenReadOnly(PathFor("portfolio.db")).LoadAsync("legacy");
        Assert.Equal("Narrative bar", loaded.Bar);
        Assert.Equal("Preserved step", Assert.Single(loaded.Items).Text);
        Assert.True(loaded.Items[0].Done);
        Assert.Equal("Preserved decision", Assert.Single(loaded.Decisions).Text);
        Assert.Empty(loaded.Outcome!.Revisions);
        Assert.Empty(loaded.Outcome.Observations);
        Assert.Equal(0, loaded.Outcome.Version);
        Assert.Equal("Description", (await PortfolioStore.OpenReadOnly(PathFor("portfolio.db")).ResolveEpicAsync("legacy"))!.Description);
    }

    private async Task<(EpicPlanStore Store, PortfolioEpic Epic)> Create()
    {
        Directory.CreateDirectory(root);
        var epic = await new PortfolioStore(PathFor("portfolio.db")).AddEpicAsync("Outcome", description: "Preserved description");
        return (new EpicPlanStore(PathFor("portfolio.db")), epic);
    }

    private EpicOutcomeEvidence Evidence(long version)
    {
        Directory.CreateDirectory(root);
        var file = PathFor("receipt.txt");
        File.WriteAllText(file, "Owner observed the required behavior.");
        return new(1, version, "service", 0, true, DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
            new("file-sha256", file, "owner-receipt", Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)))));
    }

    private static async Task<string> State(EpicPlanStore store, string epicId) =>
        JsonSerializer.Serialize(await store.LoadAsync(epicId), EpicOutcomeContract.JsonOptions);
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
}
