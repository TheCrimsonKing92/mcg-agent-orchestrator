using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.Infrastructure;

// Inventory entries describe schema-owning source files. A null version means setup conversion
// is deferred to a later slice; it does not assert that the existing store's schema is version zero.
public sealed record StoreSchemaEntry(
    string StoreName, string SourceFile, string Database, string Family, int? CurrentVersion);

public static class StoreSchemaRegistry
{
    private const string Persistence = "src/Mcg.AgentOrchestrator.Execution/Persistence/";
    private const string Conductor = "src/Mcg.AgentOrchestrator.App/Orchestration/";
    private const string Comms = "src/Mcg.AgentOrchestrator.Infrastructure.OperatorComms/";

    public static StoreSchemaEntry Portfolio { get; } =
        new("portfolio", Persistence + "PortfolioStore.cs", "portfolio.db", "portfolio", 2);

    public static StoreSchemaEntry Experiments { get; } =
        new("experiments", Persistence + "ExperimentStore.cs", "experiments.db", "experiment", 1);

    public static StoreSchemaEntry Backlog { get; } =
        new("backlog", Persistence + "BacklogStore.cs", "backlog.db", "backlog", 1);

    public static StoreSchemaEntry OperatorIntents { get; } =
        new("operator-intents", Persistence + "OperatorIntentStore.cs", SqliteOperatorIntentStore.DatabaseFileName, "backlog", 1);

    public static StoreSchemaEntry OperatorLessons { get; } =
        new("operator-lessons", Persistence + "OperatorLessonStore.cs", "operator-lessons.db", "backlog", 1);

    public static StoreSchemaEntry OperatorEscapes { get; } =
        new("operator-escapes", Persistence + "OperatorEscapeStore.cs", "operator-escapes.db", "backlog", 1);

    public static StoreSchemaEntry MergeTrainAcceptance { get; } =
        new("merge-train-acceptance", Persistence + "MergeTrainAcceptanceStore.cs", "merge-train-acceptance.db", "cohort", 1);

    public static StoreSchemaEntry FollowerGateAcceptance { get; } =
        new("follower-gate-acceptance", Persistence + "FollowerGateAcceptanceStoreSetup.cs", "follower-gate-acceptance.db", "cohort", 1);

    public static StoreSchemaEntry DogfoodLog { get; } =
        new("dogfood-log", Persistence + "DogfoodLogStore.cs", "dogfood-log.db", "collaboration", 1);

    public static StoreSchemaEntry ProgressiveReviewSteering { get; } =
        new("progressive-review-steering", Persistence + "ProgressiveReviewSteeringStoreSetup.cs", "progressive-review-steering.db", "review", 1);

    // Source fragments of one database are separate inventory entries, not separate databases.
    public static IReadOnlyList<StoreSchemaEntry> Inventory { get; } = Array.AsReadOnly<StoreSchemaEntry>(
    [
        Portfolio,
        Experiments,
        Backlog,
        OperatorIntents,
        OperatorLessons,
        OperatorEscapes,
        new("cohort-acceptance", Persistence + "CohortAcceptanceStore.cs", "cohort-acceptance.db", "cohort", null),
        new("cohort-attribution-retractions", Persistence + "CohortAcceptanceStore.AttributionRetractions.cs", "cohort-acceptance.db", "cohort", null),
        new("cohort-solo-inherited-receipts", Persistence + "CohortAcceptanceStore.SoloInheritedReceipts.cs", "cohort-acceptance.db", "cohort", null),
        MergeTrainAcceptance,
        FollowerGateAcceptance,
        new("collaboration-items", Persistence + "CollaborationItemStore.Storage.cs", "collaboration-items.db", "collaboration", null),
        new("run-events", Persistence + "RunEventStore.cs", "run-events.db", "collaboration", null),
        DogfoodLog,
        new("practice-registry", Persistence + "PracticeRegistryStore.cs", "state.db", "collaboration", null),
        new("author-claims", Conductor + "ConductorAuthorClaimStore.cs", "author-claims.db", "conductor", null),
        new("steward-triggers", Conductor + "ConductorStewardTriggerStore.cs", "steward-triggers.db", "conductor", null),
        new("steward-triage-receipts", Comms + "StewardTriageReceiptStore.cs", "steward-triage-receipts.db", "conductor", null),
        new("judge-panel", Conductor + "ConductorJudgePanelCaseStore.Schema.cs", "judge-panel.db", "conductor", null),
        new("board-fill", Conductor + "ConductorBoardFillDraftStore.cs", "board-fill.db", "conductor", null),
        new("progressive-review-glance-circuit", Persistence + "ProgressiveReviewGlanceCircuitStore.cs", "progressive-review-glance-circuit.db", "review", null),
        ProgressiveReviewSteering,
        new("reconcile-sweep-remediation", Persistence + "ReconcileSweepRemediationStore.cs", "state.db", "review", null),
        new("control-plane-delivery", Comms + "DiscordControlPlaneDeliveryStore.cs", "control-plane-delivery.db", "review", null),
        new("orchestrator-state", Persistence + "SqliteOrchestratorStateRepository.cs", "state.db", "review", null),
        new("state-history", Persistence + "StateDbMigrations.cs", "state.db", "state-history", null),
        new("schema-registry", Persistence + "StoreSchemaRegistry.cs", "per-database", "schema-registry", null)
    ]);
}

public enum StoreSchemaState { Missing, Older, Current, Newer }

public static class StoreSchemaVersions
{
    public const string TableName = "store_schema_versions";

    // Both probes accept an already-open connection and only issue SELECT statements.
    public static int? Read(SqliteConnection connection, string storeName)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sqlite_schema WHERE type = 'table' AND name = $table";
        cmd.Parameters.AddWithValue("$table", TableName);
        if (cmd.ExecuteScalar() is null)
            return null;

        cmd.Parameters.Clear();
        cmd.CommandText = "SELECT version FROM store_schema_versions WHERE store_name = $store";
        cmd.Parameters.AddWithValue("$store", storeName);
        var version = cmd.ExecuteScalar();
        return version is null ? null : Convert.ToInt32(version, CultureInfo.InvariantCulture);
    }

    public static StoreSchemaState Verify(SqliteConnection connection, StoreSchemaEntry entry)
    {
        var current = RequireVersion(entry);
        var recorded = Read(connection, entry.StoreName);
        return recorded switch
        {
            null => StoreSchemaState.Missing,
            var value when value < current => StoreSchemaState.Older,
            var value when value > current => StoreSchemaState.Newer,
            _ => StoreSchemaState.Current
        };
    }

    // The store owns schema changes and holds its write transaction through version recording.
    // This operation records the applied schema; it does not run another store's migrations.
    public static void UpgradeToCurrent(SqliteConnection connection, StoreSchemaEntry entry)
    {
        var current = RequireVersion(entry);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS store_schema_versions (
                store_name TEXT NOT NULL PRIMARY KEY,
                version INTEGER NOT NULL,
                applied_at TEXT NOT NULL
            ) WITHOUT ROWID
            """;
        cmd.ExecuteNonQuery();
        cmd.CommandText = """
            INSERT INTO store_schema_versions (store_name, version, applied_at)
            VALUES ($store, $version, $appliedAt)
            ON CONFLICT(store_name) DO UPDATE SET version = excluded.version, applied_at = excluded.applied_at
            WHERE store_schema_versions.version < excluded.version
            """;
        cmd.Parameters.AddWithValue("$store", entry.StoreName);
        cmd.Parameters.AddWithValue("$version", current);
        cmd.Parameters.AddWithValue("$appliedAt", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        cmd.ExecuteNonQuery();
    }

    private static int RequireVersion(StoreSchemaEntry entry) => entry.CurrentVersion
        ?? throw new InvalidOperationException($"Store '{entry.StoreName}' has not been converted to versioned setup.");
}
