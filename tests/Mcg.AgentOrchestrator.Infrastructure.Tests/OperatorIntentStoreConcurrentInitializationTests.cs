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
            using var connection = Open(databasePath);
            Xunit.Assert.Equal(1, StoreSchemaVersions.Read(connection, StoreSchemaRegistry.OperatorIntents.StoreName));
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

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task OperatorIntentStore_older_store_without_actor_kind_gains_column_once_and_rows_default_to_human(bool existingWal)
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
                        id, idempotency_key, verb, goal_id, task_id, payload_json,
                        payload_file_references_json, actor, channel,
                        authentication_assurance, created_at, status, claim_owner, claimed_at, completed_at, outcome)
                    VALUES (
                        'intent-1', 'key-1', 'progress', 'goal-1', 'task-1', '{"message":"first"}',
                        '["first.txt"]', 'operator', 'test', 'test-assurance',
                        '2026-09-25T00:00:00.0000000+00:00', 'Applied', 'owner-1',
                        '2026-09-25T01:00:00.0000000+00:00', '2026-09-25T02:00:00.0000000+00:00', 'first outcome'),
                        ('intent-2', 'key-2', 'retry', 'goal-2', 'task-2', '{"message":"second"}',
                        '["second.txt"]', 'another-operator', 'cli', 'another-assurance',
                        '2026-09-26T00:00:00.0000000+00:00', 'Claimed', 'owner-2',
                        '2026-09-26T01:00:00.0000000+00:00', NULL, NULL)
                    """);
                if (existingWal)
                    Execute(connection, "PRAGMA journal_mode=WAL");
            }

            Xunit.Assert.Empty(ReadActorKindColumns(databasePath));
            await InitializeTogether(databasePath, Path.Combine(root, "logs"), setupOnly: true);

            Xunit.Assert.Equal((byte)2, File.ReadAllBytes(databasePath)[18]);
            Xunit.Assert.Equal((byte)2, File.ReadAllBytes(databasePath)[19]);
            Xunit.Assert.Equal("TEXT", Xunit.Assert.Single(ReadActorKindColumns(databasePath)));
            var store = SqliteOperatorIntentStore.OpenExisting(root, Path.Combine(root, "reader-wakes"));
            var intent = Xunit.Assert.Single(await store.ListForGoalAsync("goal-1"));
            Xunit.Assert.Equal("intent-1", intent.Id);
            Xunit.Assert.Equal("key-1", intent.IdempotencyKey);
            Xunit.Assert.Equal("progress", intent.Verb);
            Xunit.Assert.Equal("goal-1", intent.GoalId);
            Xunit.Assert.Equal("task-1", intent.TaskId);
            Xunit.Assert.Equal("{\"message\":\"first\"}", intent.PayloadJson);
            Xunit.Assert.Equal(new[] { "first.txt" }, intent.PayloadFileReferences);
            Xunit.Assert.Equal("operator", intent.Actor);
            Xunit.Assert.Equal("test", intent.Channel);
            Xunit.Assert.Equal("test-assurance", intent.AuthenticationAssurance);
            Xunit.Assert.Equal(DateTimeOffset.Parse("2026-09-25T00:00:00Z"), intent.CreatedAt);
            Xunit.Assert.Equal(OperatorIntentStatus.Applied, intent.Status);
            Xunit.Assert.Equal("owner-1", intent.ClaimOwner);
            Xunit.Assert.Equal(DateTimeOffset.Parse("2026-09-25T01:00:00Z"), intent.ClaimedAt);
            Xunit.Assert.Equal(DateTimeOffset.Parse("2026-09-25T02:00:00Z"), intent.CompletedAt);
            Xunit.Assert.Equal("first outcome", intent.Outcome);
            Xunit.Assert.Equal(OperatorActorKind.Human, intent.ActorKind);
            var second = Xunit.Assert.Single(await store.ListForGoalAsync("goal-2"));
            Xunit.Assert.Equal("intent-2", second.Id);
            Xunit.Assert.Equal("key-2", second.IdempotencyKey);
            Xunit.Assert.Equal("retry", second.Verb);
            Xunit.Assert.Equal("goal-2", second.GoalId);
            Xunit.Assert.Equal("task-2", second.TaskId);
            Xunit.Assert.Equal("{\"message\":\"second\"}", second.PayloadJson);
            Xunit.Assert.Equal(new[] { "second.txt" }, second.PayloadFileReferences);
            Xunit.Assert.Equal("another-operator", second.Actor);
            Xunit.Assert.Equal("cli", second.Channel);
            Xunit.Assert.Equal("another-assurance", second.AuthenticationAssurance);
            Xunit.Assert.Equal(DateTimeOffset.Parse("2026-09-26T00:00:00Z"), second.CreatedAt);
            Xunit.Assert.Equal(OperatorIntentStatus.Claimed, second.Status);
            Xunit.Assert.Equal("owner-2", second.ClaimOwner);
            Xunit.Assert.Equal(DateTimeOffset.Parse("2026-09-26T01:00:00Z"), second.ClaimedAt);
            Xunit.Assert.Null(second.CompletedAt);
            Xunit.Assert.Null(second.Outcome);
            Xunit.Assert.Equal(OperatorActorKind.Human, second.ActorKind);
            var before = Snapshot(databasePath);
            SqliteOperatorIntentStore.Setup(databasePath);
            Xunit.Assert.Equal(before, Snapshot(databasePath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public async Task Setup_ConcurrentCurrentWalStore_PreservesWalRowsAndVersionAndAllowsReadOnlyReads()
    {
        var root = CreateTempDirectory();
        try
        {
            var databasePath = Path.Combine(root, SqliteOperatorIntentStore.DatabaseFileName);
            SqliteOperatorIntentStore.Setup(databasePath);
            var writer = SqliteOperatorIntentStore.ForDirectories(root, Path.Combine(root, "writer-wakes"));
            var intent = await writer.EnqueueAsync(new OperatorIntentRecord(
                "intent", "key", "retry", "goal", "task", "{}", ["payload.txt"],
                "operator", "cli", "test-assurance", DateTimeOffset.Parse("2026-09-25T00:00:00Z")));
            using (var connection = Open(databasePath))
                Execute(connection, "PRAGMA journal_mode=WAL");
            Xunit.Assert.Equal((byte)2, File.ReadAllBytes(databasePath)[18]);
            var before = Snapshot(databasePath);

            await InitializeTogether(databasePath, Path.Combine(root, "logs"), setupOnly: true);

            Xunit.Assert.Equal(before, Snapshot(databasePath));
            Xunit.Assert.Equal((byte)2, File.ReadAllBytes(databasePath)[18]);
            Xunit.Assert.Equal((byte)2, File.ReadAllBytes(databasePath)[19]);
            var bytes = File.ReadAllBytes(databasePath);
            var wakeDirectory = Path.Combine(root, "reader-wakes");
            var reader = SqliteOperatorIntentStore.OpenExisting(root, wakeDirectory);
            Xunit.Assert.Equal(System.Text.Json.JsonSerializer.Serialize(intent),
                System.Text.Json.JsonSerializer.Serialize(await reader.GetAsync(intent.Id)));
            Xunit.Assert.Equal(bytes, File.ReadAllBytes(databasePath));
            Xunit.Assert.Equal(before, Snapshot(databasePath));
            Xunit.Assert.False(Directory.Exists(wakeDirectory));

            // A reader must also observe intents submitted after it was opened.
            var nextIntent = intent with { Id = "next-intent", IdempotencyKey = "next-key" };
            Xunit.Assert.Equal(nextIntent.Id, (await writer.EnqueueAsync(nextIntent)).Id);
            Xunit.Assert.Equal(System.Text.Json.JsonSerializer.Serialize(nextIntent),
                System.Text.Json.JsonSerializer.Serialize(await reader.GetAsync(nextIntent.Id)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public void Setup_NewerVersion_PreservesSchemaRowsAndVersion()
    {
        var root = CreateTempDirectory();
        try
        {
            var databasePath = Path.Combine(root, SqliteOperatorIntentStore.DatabaseFileName);
            SqliteOperatorIntentStore.Setup(databasePath);
            using (var connection = Open(databasePath))
            {
                Execute(connection, "UPDATE store_schema_versions SET version = 2, applied_at = 'original'");
                Execute(connection, "PRAGMA journal_mode=WAL");
            }
            var before = Snapshot(databasePath);
            var bytes = File.ReadAllBytes(databasePath);

            SqliteOperatorIntentStore.Setup(databasePath);

            Xunit.Assert.Equal(before, Snapshot(databasePath));
            Xunit.Assert.Equal(bytes, File.ReadAllBytes(databasePath));
            using var readBack = Open(databasePath);
            Xunit.Assert.Equal(2, StoreSchemaVersions.Read(readBack, StoreSchemaRegistry.OperatorIntents.StoreName));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string[] Snapshot(string databasePath)
    {
        using var connection = Open(databasePath);
        var rows = new List<string>();
        foreach (var sql in new[]
        {
            "SELECT * FROM sqlite_schema ORDER BY type, name",
            "SELECT * FROM store_schema_versions ORDER BY store_name",
            "SELECT * FROM operator_intents ORDER BY id"
        })
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var values = new object[reader.FieldCount];
                reader.GetValues(values);
                rows.Add(System.Text.Json.JsonSerializer.Serialize(values));
            }
        }
        return rows.ToArray();
    }

    private static async Task InitializeTogether(string databasePath, string wakeDirectory, bool setupOnly = false)
    {
        using var gate = new Barrier(3);
        Task StartInitializer() => Task.Factory.StartNew(
            () =>
            {
                gate.SignalAndWait();
                if (setupOnly)
                    SqliteOperatorIntentStore.Setup(databasePath);
                else
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
