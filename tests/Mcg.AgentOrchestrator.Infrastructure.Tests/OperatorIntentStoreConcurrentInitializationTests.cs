using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

public sealed class OperatorIntentStoreConcurrentInitializationTests
{
    [Xunit.Fact]
    public async Task OperatorIntentStore_concurrent_new_store_initializers_both_succeed_with_one_actor_kind_column()
    {
        var root = CreateTempDirectory();
        try
        {
            var databasePath = Path.Combine(root, "operator-intents.db");
            await InitializeTogether(databasePath, Path.Combine(root, "logs"));

            var columns = ReadActorKindColumns(databasePath);
            Xunit.Assert.Equal("TEXT", Xunit.Assert.Single(columns));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public void OperatorIntentStore_duplicate_column_interleaving_is_tolerated()
    {
        var root = CreateTempDirectory();
        try
        {
            var databasePath = Path.Combine(root, "operator-intents.db");
            using var connection = Open(databasePath);
            Execute(connection, "CREATE TABLE operator_intents (id TEXT PRIMARY KEY, actor_kind TEXT)");

            var failure = Xunit.Assert.Throws<SqliteException>(() =>
                Execute(connection, "ALTER TABLE operator_intents ADD COLUMN actor_kind TEXT"));
            Xunit.Assert.Contains("duplicate column name", failure.Message, StringComparison.OrdinalIgnoreCase);

            SqliteOperatorIntentStore.AddColumnTolerant(connection, "operator_intents", "actor_kind", "TEXT");
            Xunit.Assert.Equal("TEXT", Xunit.Assert.Single(ReadActorKindColumns(connection)));
            Xunit.Assert.Throws<SqliteException>(() =>
                SqliteOperatorIntentStore.AddColumnTolerant(connection, "missing_table", "actor_kind", "TEXT"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public async Task OperatorIntentStore_older_store_without_actor_kind_gains_column_once_and_rows_default_to_human()
    {
        var root = CreateTempDirectory();
        try
        {
            var databasePath = Path.Combine(root, "operator-intents.db");
            using (var connection = Open(databasePath))
            {
                Execute(connection, """
                    CREATE TABLE operator_intents (
                        id TEXT PRIMARY KEY,
                        idempotency_key TEXT NOT NULL UNIQUE,
                        verb TEXT NOT NULL,
                        goal_id TEXT NOT NULL,
                        task_id TEXT,
                        payload_json TEXT NOT NULL,
                        payload_file_references_json TEXT NOT NULL,
                        actor TEXT NOT NULL,
                        channel TEXT NOT NULL,
                        authentication_assurance TEXT NOT NULL,
                        created_at TEXT NOT NULL,
                        status TEXT NOT NULL,
                        claim_owner TEXT,
                        claimed_at TEXT,
                        completed_at TEXT,
                        outcome TEXT
                    )
                    """);
                Execute(connection, """
                    INSERT INTO operator_intents (
                        id, idempotency_key, verb, goal_id, payload_json,
                        payload_file_references_json, actor, channel,
                        authentication_assurance, created_at, status)
                    VALUES (
                        'intent-1', 'key-1', 'progress', 'goal-1', '{}',
                        '[]', 'operator', 'test', 'test-assurance',
                        '2026-09-25T00:00:00.0000000+00:00', 'Pending')
                    """);
            }

            Xunit.Assert.Empty(ReadActorKindColumns(databasePath));
            await InitializeTogether(databasePath, Path.Combine(root, "logs"));

            Xunit.Assert.Equal("TEXT", Xunit.Assert.Single(ReadActorKindColumns(databasePath)));
            var store = new SqliteOperatorIntentStore(databasePath, Path.Combine(root, "logs"));
            var intent = Xunit.Assert.Single(await store.ListForGoalAsync("goal-1"));
            Xunit.Assert.Equal("intent-1", intent.Id);
            Xunit.Assert.Equal(OperatorActorKind.Human, intent.ActorKind);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task InitializeTogether(string databasePath, string wakeDirectory)
    {
        using var gate = new Barrier(3);
        Task StartInitializer() => Task.Factory.StartNew(
            () =>
            {
                gate.SignalAndWait();
                _ = new SqliteOperatorIntentStore(databasePath, wakeDirectory);
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        var initializers = new[] { StartInitializer(), StartInitializer() };
        gate.SignalAndWait();
        await Task.WhenAll(initializers);
    }

    private static IReadOnlyList<string> ReadActorKindColumns(string databasePath)
    {
        using var connection = Open(databasePath);
        return ReadActorKindColumns(connection);
    }

    private static IReadOnlyList<string> ReadActorKindColumns(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(operator_intents)";
        using var reader = command.ExecuteReader();
        var types = new List<string>();
        while (reader.Read())
            if (reader.GetString(1).Equals("actor_kind", StringComparison.OrdinalIgnoreCase))
                types.Add(reader.GetString(2));
        return types;
    }

    private static SqliteConnection Open(string databasePath)
    {
        var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadWriteCreate;Pooling=False;");
        connection.Open();
        return connection;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static string CreateTempDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mcg-operator-intent-init-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }
}
