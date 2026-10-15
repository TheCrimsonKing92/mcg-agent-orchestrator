using System.Security.Cryptography;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

// Parallel-safe: each case owns actual SQLite stores beneath a unique disposable directory.
public sealed class StoreSchemaDiagnosticsTests
{
    [Theory]
    [InlineData("collaboration-items")]
    [InlineData("dogfood-log")]
    [InlineData("follower-gate-acceptance")]
    [InlineData("merge-train-acceptance")]
    [InlineData("operator-escapes")]
    [InlineData("operator-intents")]
    [InlineData("operator-lessons")]
    [InlineData("progressive-review-glance-circuit")]
    [InlineData("progressive-review-steering")]
    public void ReadOnlyReaderRefusesNewerWithSafeRemedyAndPreservesOtherSchemaContracts(string storeName)
    {
        var root = Directory.CreateTempSubdirectory("schema-diagnostic-").FullName;
        try
        {
            var (entry, setup, open) = Reader(storeName);
            var path = Path.Combine(root, entry.Database);
            var before = Snapshot(root);
            var missing = Assert.Throws<InvalidOperationException>(() => open(path));
            Assert.Contains("schema is Missing", missing.Message);
            Assert.Contains("run setup", StoreSchemaDiagnostics.Message(missing));
            Assert.Equal(before, Snapshot(root));

            setup(path);
            before = Snapshot(root);
            Assert.NotNull(open(path)); // Current schema remains usable without writes.
            Assert.Equal(before, Snapshot(root));

            SetVersion(path, entry.StoreName, 0);
            before = Snapshot(root);
            var older = Assert.Throws<InvalidOperationException>(() => open(path));
            Assert.Contains("schema is Older", older.Message);
            Assert.Contains("run setup", older.Message);
            Assert.Equal(older.Message, StoreSchemaDiagnostics.Message(older));
            Assert.Equal(before, Snapshot(root));

            SetVersion(path, entry.StoreName, 99);
            before = Snapshot(root);
            var newer = Assert.Throws<InvalidOperationException>(() => open(path));
            // Keep the legacy exception contract; CLI rendering consumes compatibility metadata.
            Assert.Contains("schema is Newer", newer.Message);
            Assert.Contains("run setup", newer.Message);
            var diagnostic = StoreSchemaDiagnostics.Message(newer);
            Assert.Contains($"Store '{entry.StoreName}' at '{path}' records schema version 99", diagnostic);
            Assert.Contains($"newer than runtime-supported version {entry.CurrentVersion}", diagnostic);
            Assert.Contains("Use a compatible runtime or independently verified recovery", diagnostic);
            Assert.Contains("older setup cannot repair this schema", diagnostic);
            Assert.DoesNotContain("run setup", diagnostic);
            Assert.Equal(before, Snapshot(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static (StoreSchemaEntry Entry, Action<string> Setup, Func<string, object> Open) Reader(string storeName) =>
        storeName switch
        {
            "collaboration-items" => (StoreSchemaRegistry.CollaborationItems, CollaborationItemStore.Setup,
                path => CollaborationItemStore.OpenReadOnly(path)),
            "dogfood-log" => (StoreSchemaRegistry.DogfoodLog, DogfoodLogStore.Setup,
                path => DogfoodLogStore.OpenReadOnly(path)),
            "follower-gate-acceptance" => (StoreSchemaRegistry.FollowerGateAcceptance, FollowerGateAcceptanceStoreSetup.Setup,
                path => FollowerGateAcceptanceStore.OpenReadOnly(path)),
            "merge-train-acceptance" => (StoreSchemaRegistry.MergeTrainAcceptance, MergeTrainAcceptanceStore.Setup,
                path => MergeTrainAcceptanceStore.OpenReadOnly(path)),
            "operator-escapes" => (StoreSchemaRegistry.OperatorEscapes, SqliteOperatorEscapeStore.Setup,
                path => SqliteOperatorEscapeStore.OpenReadOnly(path)),
            "operator-intents" => (StoreSchemaRegistry.OperatorIntents, SqliteOperatorIntentStore.Setup,
                path => SqliteOperatorIntentStore.OpenExisting(Path.GetDirectoryName(path)!,
                    Path.Combine(Path.GetDirectoryName(path)!, "logs"))),
            "operator-lessons" => (StoreSchemaRegistry.OperatorLessons, SqliteOperatorLessonStore.Setup,
                path => SqliteOperatorLessonStore.OpenReadOnly(path)),
            "progressive-review-glance-circuit" => (StoreSchemaRegistry.ProgressiveReviewGlanceCircuit, SqliteProgressiveReviewGlanceCircuitStore.Setup,
                path => SqliteProgressiveReviewGlanceCircuitStore.OpenReadOnly(path)),
            "progressive-review-steering" => (StoreSchemaRegistry.ProgressiveReviewSteering, ProgressiveReviewSteeringStoreSetup.Setup,
                path => SqliteProgressiveReviewSteeringStore.OpenReadOnly(path)),
            _ => throw new ArgumentOutOfRangeException(nameof(storeName))
        };

    private static void SetVersion(string path, string storeName, int version)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE store_schema_versions SET version = $version WHERE store_name = $store";
        command.Parameters.AddWithValue("$version", version);
        command.Parameters.AddWithValue("$store", storeName);
        Assert.Equal(1, command.ExecuteNonQuery());
    }

    private static string[] Snapshot(string root) => Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
        .Order(StringComparer.Ordinal).Select(path => Path.GetRelativePath(root, path) +
            (File.Exists(path) ? ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) : ":directory")).ToArray();
}
