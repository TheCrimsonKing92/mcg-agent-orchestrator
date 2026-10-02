using System.Globalization;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// BacklogStore's constructor performs schema writes. Drafting must only read the existing store.
internal static class AuthorBriefDraftBacklog
{
    internal static BacklogItem Read(string storePath, string prefix)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = storePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false
        }.ToString());
        connection.Open();
        using var transaction = connection.BeginTransaction(deferred: true);
        BacklogItem? item = null;
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT id, title, body, status, created_at, updated_at, source_goal_id
                FROM backlog WHERE substr(id, 1, length($prefix)) = $prefix COLLATE NOCASE LIMIT 2
                """;
            command.Parameters.AddWithValue("$prefix", prefix);
            using var reader = command.ExecuteReader();
            if (reader.Read())
                item = new BacklogItem(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                    Enum.Parse<BacklogItemStatus>(reader.GetString(3)),
                    DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture),
                    DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture),
                    reader.IsDBNull(6) ? null : reader.GetString(6));
            if (reader.Read()) throw new ArgumentException($"Backlog prefix '{prefix}' is ambiguous.");
        }
        if (item is null) throw new ArgumentException($"No backlog item matches '{prefix}'.");
        using var notesCommand = connection.CreateCommand();
        notesCommand.Transaction = transaction;
        notesCommand.CommandText = "SELECT created_at, text FROM backlog_notes WHERE backlog_item_id = $id ORDER BY created_at, id";
        notesCommand.Parameters.AddWithValue("$id", item.Id);
        var notes = new List<BacklogNote>();
        using (var reader = notesCommand.ExecuteReader())
            while (reader.Read()) notes.Add(new BacklogNote(
                DateTimeOffset.Parse(reader.GetString(0), CultureInfo.InvariantCulture), reader.GetString(1)));
        return item with { Notes = notes };
    }
}
