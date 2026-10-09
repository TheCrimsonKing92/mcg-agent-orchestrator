using System.Globalization;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.App.Cli;

// A v1 store has no plan. Validate legacy writes without upgrading a database on a refusal.
internal static class EpicPlanWritePreparation
{
    internal static (PortfolioEpic Epic, bool Legacy) Resolve(string path, string reference)
    {
        using var connection = Open(path);
        if (StoreSchemaVersions.Read(connection, "portfolio") != 1)
        {
            var epic = PortfolioStore.OpenReadOnly(path).ResolveEpicAsync(reference).GetAwaiter().GetResult()
                ?? throw new InvalidOperationException($"Epic '{reference}' was not found.");
            return (epic, false);
        }
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, title, project_id, created_at, created_by, updated_at, updated_by, description
            FROM epics WHERE id LIKE $prefix OR title = $title COLLATE NOCASE
            ORDER BY CASE WHEN title = $title COLLATE NOCASE THEN 0 ELSE 1 END, title COLLATE NOCASE LIMIT 2
            """;
        command.Parameters.AddWithValue("$prefix", reference + "%");
        command.Parameters.AddWithValue("$title", reference);
        var matches = new List<PortfolioEpic>();
        using (var reader = command.ExecuteReader())
            while (reader.Read()) matches.Add(new(reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2),
                DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture), reader.GetString(4),
                DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture), reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7)));
        return matches.Count switch
        {
            0 => throw new InvalidOperationException($"Epic '{reference}' was not found."),
            1 => (matches[0], true),
            _ => throw new InvalidOperationException($"Epic '{reference}' is ambiguous.")
        };
    }

    internal static void ValidateLegacy(string path, PortfolioEpic epic, string command, string? backlogId, int? at)
    {
        if (command is "epic-plan-move" or "epic-plan-remove" or "epic-plan-done")
            throw new ArgumentException("The epic has no plan items.");
        if (command == "epic-plan-add" && at is not (null or 1))
            throw new ArgumentException("Position must be between 1 and 1.");
        if (backlogId is null) return;
        using var connection = Open(path);
        using var query = connection.CreateCommand();
        query.CommandText = "SELECT e.id, e.title FROM backlog_epic_memberships m JOIN epics e ON e.id = m.epic_id WHERE m.backlog_item_id = $id";
        query.Parameters.AddWithValue("$id", backlogId);
        using var reader = query.ExecuteReader();
        if (reader.Read() && reader.GetString(0) != epic.Id)
            throw new InvalidOperationException($"Backlog item {backlogId[..Math.Min(8, backlogId.Length)]} belongs to epic {reader.GetString(1)} ({reader.GetString(0)})");
    }

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }
}
