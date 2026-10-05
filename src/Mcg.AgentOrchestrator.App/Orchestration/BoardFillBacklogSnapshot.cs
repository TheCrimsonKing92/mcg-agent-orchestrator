using System.Globalization;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Snapshot only: never instantiate BacklogStore, whose constructor writes schema.
internal static class BoardFillBacklogSnapshot
{
    internal static IReadOnlyList<BacklogItem> Read(string path)
    {
        if (!File.Exists(path)) return [];
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false
        }.ToString());
        connection.Open();
        using var transaction = connection.BeginTransaction(deferred: true);
        var items = new Dictionary<string, BacklogItem>(StringComparer.Ordinal);
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT id, title, body, status, created_at, updated_at, source_goal_id, priority, tags FROM backlog";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var item = new BacklogItem(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                    Enum.Parse<BacklogItemStatus>(reader.GetString(3)), Parse(reader.GetString(4)),
                    Parse(reader.GetString(5)), reader.IsDBNull(6) ? null : reader.GetString(6),
                    reader.IsDBNull(7) ? null : reader.GetString(7), reader.GetString(8));
                items.Add(item.Id, item);
            }
        }
        var notes = new Dictionary<string, List<BacklogNote>>(StringComparer.Ordinal);
        var dependencies = new Dictionary<string, List<BacklogDependency>>(StringComparer.Ordinal);
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT backlog_item_id, created_at, text FROM backlog_notes ORDER BY created_at, id";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var id = reader.GetString(0);
                if (!notes.TryGetValue(id, out var list)) notes.Add(id, list = []);
                list.Add(new(Parse(reader.GetString(1)), reader.GetString(2)));
            }
        }
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT dependent_id, prerequisite_id, prerequisite_kind, created_at FROM backlog_dependencies ORDER BY sequence";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var id = reader.GetString(0);
                if (!dependencies.TryGetValue(id, out var list)) dependencies.Add(id, list = []);
                list.Add(new(id, reader.GetString(1), Enum.Parse<BacklogDependencyTargetKind>(reader.GetString(2)),
                    Parse(reader.GetString(3))));
            }
        }
        return items.Values.Select(item => item with
        {
            Notes = notes.TryGetValue(item.Id, out var noteList) ? noteList : [],
            Dependencies = dependencies.TryGetValue(item.Id, out var dependencyList) ? dependencyList : []
        }).ToArray();
    }

    private static DateTimeOffset Parse(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);
}
