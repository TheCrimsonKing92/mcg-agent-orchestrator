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
        using var session = new SqliteReadSession(path);
        return ReadCore(session.Connection);
    }

    internal static IReadOnlyList<BacklogItem> ReadScoped(SqliteConnection connection, IEnumerable<string> ids,
        CancellationToken cancellationToken)
    {
        var scope = ids.Distinct(StringComparer.Ordinal).ToArray();
        var items = new Dictionary<string, BacklogItem>(StringComparer.Ordinal);
        foreach (var batch in scope.Chunk(400))
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var item in ReadCore(connection, batch, cancellationToken)) items[item.Id] = item;
        }
        var prerequisites = items.Values.SelectMany(item => item.Dependencies)
            .Where(dependency => dependency.TargetKind == BacklogDependencyTargetKind.Backlog)
            .Select(dependency => dependency.PrerequisiteId).Distinct(StringComparer.Ordinal)
            .Except(scope, StringComparer.Ordinal).ToArray();
        foreach (var batch in prerequisites.Chunk(400))
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var item in ReadCore(connection, batch, cancellationToken)) items[item.Id] = item;
        }
        return items.Values.ToArray();
    }

    private static IReadOnlyList<BacklogItem> ReadCore(SqliteConnection connection, string[]? ids = null,
        CancellationToken cancellationToken = default)
    {
        var items = new Dictionary<string, BacklogItem>(StringComparer.Ordinal);
        using (var command = connection.CreateCommand())
        {
            cancellationToken.ThrowIfCancellationRequested();
            command.CommandText = "SELECT id, title, body, status, created_at, updated_at, source_goal_id, priority, tags FROM backlog";
            if (ids is not null) command.CommandText += $" WHERE id IN ({SqliteReadSession.BindIds(command, ids)})";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
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
            cancellationToken.ThrowIfCancellationRequested();
            command.CommandText = "SELECT backlog_item_id, created_at, text FROM backlog_notes";
            if (ids is not null) command.CommandText += $" WHERE backlog_item_id IN ({SqliteReadSession.BindIds(command, ids)})";
            command.CommandText += " ORDER BY created_at, id";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var id = reader.GetString(0);
                if (!notes.TryGetValue(id, out var list)) notes.Add(id, list = []);
                list.Add(new(Parse(reader.GetString(1)), reader.GetString(2)));
            }
        }
        using (var command = connection.CreateCommand())
        {
            cancellationToken.ThrowIfCancellationRequested();
            command.CommandText = "SELECT dependent_id, prerequisite_id, prerequisite_kind, created_at FROM backlog_dependencies";
            if (ids is not null) command.CommandText += $" WHERE dependent_id IN ({SqliteReadSession.BindIds(command, ids)})";
            command.CommandText += " ORDER BY sequence";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
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
