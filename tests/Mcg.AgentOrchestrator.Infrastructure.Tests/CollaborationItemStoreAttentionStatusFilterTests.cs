using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

// Parallel-safe: every test owns a unique directory and unpooled SQLite connections.
public sealed class CollaborationItemStoreAttentionStatusFilterTests : IDisposable
{
    private readonly string _root = CreateTempDirectory();
    private string DatabasePath => Path.Combine(_root, "collaboration-items.db");

    [Xunit.Fact]
    public async Task AttentionQueue_MixedStatuses_MatchesFullListContentAndOrder()
    {
        var store = CollaborationItemStore.ForDirectory(_root);
        AssertStatusRaisedIndex();
        SeedItems(includeOpen: true);

        var all = await store.ListAsync();
        var expected = CollaborationItemLifecycle.BuildAttentionQueue(all);
        var actual = await store.GetAttentionQueueAsync();

        Xunit.Assert.Equal(280, all.Count(item => CollaborationItemLifecycle.IsTerminal(item.Status)));
        Xunit.Assert.Equal(42, all.Count(item => !CollaborationItemLifecycle.IsTerminal(item.Status)));
        Xunit.Assert.Equal(18, expected.Count);
        foreach (var type in new[]
                 {
                     CollaborationItemType.Decision,
                     CollaborationItemType.Clarification,
                     CollaborationItemType.Verify
                 })
        {
            Xunit.Assert.Contains(expected, item => item.Type == type
                && item.Status == CollaborationItemStatus.Raised);
            Xunit.Assert.Contains(expected, item => item.Type == type
                && item.Status == CollaborationItemStatus.Delivered);
        }
        Xunit.Assert.DoesNotContain(actual, item => !CollaborationItemLifecycle.IsReachUpType(item.Type));
        Xunit.Assert.Equal(expected.Select(item => item.Id), actual.Select(item => item.Id));
    }

    [Xunit.Fact]
    public async Task AttentionQueue_OnlyTerminalItems_ReturnsEmpty()
    {
        var store = CollaborationItemStore.ForDirectory(_root);
        AssertStatusRaisedIndex();
        SeedItems(includeOpen: false);

        Xunit.Assert.Equal(280, (await store.ListAsync()).Count);
        Xunit.Assert.Empty(await store.GetAttentionQueueAsync());
    }

    [Xunit.Fact]
    public async Task AttentionQueue_StatusTransitions_RemovesResolvedAndKeepsDelivered()
    {
        var store = CollaborationItemStore.ForDirectory(_root);
        AssertStatusRaisedIndex();
        var resolved = await store.RaiseAsync(
            CollaborationItemType.Decision, "goal-one", "Resolve me", "Body", "resolve-key");
        var delivered = await store.RaiseAsync(
            CollaborationItemType.Decision, "goal-two", "Deliver me", "Body", "deliver-key");
        var before = await store.GetAttentionQueueAsync();
        Xunit.Assert.Equal(2, before.Count);
        Xunit.Assert.Contains(before, item => item.Id == resolved.Id);
        Xunit.Assert.Contains(before, item => item.Id == delivered.Id);

        Xunit.Assert.True(await store.TryResolveAsync("resolve-key", "Resolved"));
        Xunit.Assert.True(await store.TryMarkDeliveredAsync("deliver-key"));

        var remaining = Xunit.Assert.Single(await store.GetAttentionQueueAsync());
        Xunit.Assert.Equal(delivered.Id, remaining.Id);
        Xunit.Assert.Equal(CollaborationItemStatus.Delivered, remaining.Status);
        Xunit.Assert.Equal(
            CollaborationItemStatus.Resolved,
            (await store.ListAsync()).Single(item => item.Id == resolved.Id).Status);
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task Schema_ReopeningExistingDatabase_CreatesStatusRaisedIndexIdempotently(
        bool removeIndexBeforeReopening)
    {
        var store = CollaborationItemStore.ForDirectory(_root);
        var item = await store.RaiseAsync(
            CollaborationItemType.Verify, "goal-one", "Verify", "Body");
        AssertStatusRaisedIndex();

        if (removeIndexBeforeReopening)
        {
            // Simulate an unversioned populated database created before this index was introduced.
            using var connection = new SqliteConnection(
                $"Data Source={DatabasePath};Mode=ReadWrite;Pooling=False;");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DROP INDEX idx_collaboration_items_status_raised";
            command.ExecuteNonQuery();
            command.CommandText = "DROP TABLE store_schema_versions";
            command.ExecuteNonQuery();
        }

        var reopened = CollaborationItemStore.ForDirectory(_root);

        AssertStatusRaisedIndex();
        Xunit.Assert.Equal(item.Id, Xunit.Assert.Single(await reopened.GetAttentionQueueAsync()).Id);
    }

    private void SeedItems(bool includeOpen)
    {
        var items = new List<CollaborationItem>();
        var types = Enum.GetValues<CollaborationItemType>();
        var raisedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        foreach (var type in types)
        foreach (var status in new[] { CollaborationItemStatus.Resolved, CollaborationItemStatus.Closed })
        for (var i = 0; i < 20; i++)
        {
            items.Add(CreateItem(type, status, raisedAt.AddSeconds(items.Count * 2), items.Count));
        }

        if (includeOpen)
        {
            var openIndex = 0;
            foreach (var type in types)
            foreach (var status in new[] { CollaborationItemStatus.Raised, CollaborationItemStatus.Delivered })
            for (var i = 0; i < 3; i++)
            {
                // Interleave types and statuses as well as terminal/open rows, with no ties.
                var timeSlot = (openIndex % 6) * types.Length + openIndex / 6;
                openIndex++;
                items.Add(CreateItem(type, status, raisedAt.AddSeconds(timeSlot * 14 + 1), items.Count));
            }
        }

        using var connection = new SqliteConnection(
            $"Data Source={DatabasePath};Mode=ReadWrite;Pooling=False;");
        connection.Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO collaboration_items
                (id, type, goal_id, status, subject, body, correlation_key, raised_at,
                 resolved_at, resolution, answer_history_json)
            VALUES ($id, $type, $goal_id, $status, $subject, $body, $correlation_key,
                    $raised_at, $resolved_at, $resolution, $answer_history_json)
            """;
        // Insert in reverse order so insertion order cannot satisfy the time ordering.
        foreach (var item in items.AsEnumerable().Reverse())
        {
            command.Parameters.Clear();
            command.Parameters.AddWithValue("$id", item.Id);
            command.Parameters.AddWithValue("$type", item.Type.ToString());
            command.Parameters.AddWithValue("$goal_id", item.GoalId!);
            command.Parameters.AddWithValue("$status", item.Status.ToString());
            command.Parameters.AddWithValue("$subject", item.Subject);
            command.Parameters.AddWithValue("$body", item.Body);
            command.Parameters.AddWithValue("$correlation_key", item.CorrelationKey!);
            command.Parameters.AddWithValue("$raised_at", item.RaisedAt.ToString("O"));
            command.Parameters.AddWithValue("$resolved_at", (object?)item.ResolvedAt?.ToString("O") ?? DBNull.Value);
            command.Parameters.AddWithValue("$resolution", (object?)item.Resolution ?? DBNull.Value);
            command.Parameters.AddWithValue("$answer_history_json", DBNull.Value);
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    private static CollaborationItem CreateItem(
        CollaborationItemType type, CollaborationItemStatus status, DateTimeOffset raisedAt, int index) =>
        new($"item-{index}", type, $"goal-{index % 3}", status, $"Subject {index}", $"Body {index}",
            $"key-{index}", raisedAt,
            CollaborationItemLifecycle.IsTerminal(status) ? raisedAt.AddMinutes(1) : null,
            CollaborationItemLifecycle.IsTerminal(status) ? "Complete" : null);

    private void AssertStatusRaisedIndex()
    {
        using var connection = new SqliteConnection(
            $"Data Source={DatabasePath};Mode=ReadOnly;Pooling=False;");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA index_list(collaboration_items)";
        var indexes = new List<string>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
                indexes.Add(reader.GetString(1));
        }
        Xunit.Assert.Contains("idx_collaboration_items_status_raised", indexes);

        command.CommandText = "PRAGMA index_info(idx_collaboration_items_status_raised)";
        var columns = new List<(int Sequence, string Name)>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
                columns.Add((reader.GetInt32(0), reader.GetString(2)));
        }
        Xunit.Assert.Equal(new[] { "status", "raised_at" },
            columns.OrderBy(column => column.Sequence).Select(column => column.Name));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
