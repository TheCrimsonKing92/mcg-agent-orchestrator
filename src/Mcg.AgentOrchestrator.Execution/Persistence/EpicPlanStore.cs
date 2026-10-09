using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.Infrastructure;

public enum EpicPlanItemKind { Slice, Step }
public sealed record EpicPlanItem(int Position, EpicPlanItemKind Kind, string? BacklogItemId, string? Text, bool Done);
public sealed record EpicPlanDecision(long Id, DateTimeOffset DecidedAt, string Text, string? DecidedBy);
public sealed record EpicPlan(string EpicId, string? Bar, IReadOnlyList<EpicPlanItem> Items, IReadOnlyList<EpicPlanDecision> Decisions);

// Owns ordered plan rows, decisions and the bar. Slice progress belongs to backlog/goal state.
public sealed class EpicPlanStore
{
    private readonly string _path;
    private readonly bool _readOnly;

    public EpicPlanStore(string path) : this(path, false)
    {
        // Avoid even setup's write transaction for an already-current database.
        if (File.Exists(path))
        {
            using var connection = Open(path, true);
            if (StoreSchemaVersions.Verify(connection, StoreSchemaRegistry.Portfolio) == StoreSchemaState.Current)
                return;
        }
        PortfolioStore.Setup(path);
    }

    private EpicPlanStore(string path, bool readOnly) => (_path, _readOnly) = (path, readOnly);

    public static EpicPlanStore OpenReadOnly(string path)
    {
        // Reuse the portfolio version and missing-store contract.
        PortfolioStore.OpenReadOnly(path);
        return new(path, true);
    }

    public Task<EpicPlan> LoadAsync(string epicId)
    {
        using var connection = Open(_path, _readOnly);
        Execute(connection, "BEGIN", epicId);
        RequireEpic(connection, epicId);
        var bar = Scalar(connection, "SELECT bar FROM epics WHERE id = $epic", epicId) as string;
        var items = Rows(connection, epicId).Select(row => row.Item).ToArray();
        var decisions = new List<EpicPlanDecision>();
        using var command = Command(connection,
            "SELECT id, decided_at, text, decided_by FROM epic_decisions WHERE epic_id = $epic ORDER BY decided_at DESC, id DESC", epicId);
        using var reader = command.ExecuteReader();
        while (reader.Read())
            decisions.Add(new(reader.GetInt64(0), DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture),
                reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3)));
        return Task.FromResult(new EpicPlan(epicId, bar, items, decisions));
    }

    public Task AddSliceAsync(string epicId, string backlogItemId, int? at = null, string actor = "operator")
    {
        RequireText(backlogItemId, nameof(backlogItemId));
        RequireText(actor, nameof(actor));
        return WriteAsync(epicId, connection =>
        {
            var rows = Rows(connection, epicId);
            var position = ValidateInsertion(at, rows.Count);
            using var membership = Command(connection,
                "SELECT e.id, e.title FROM backlog_epic_memberships m JOIN epics e ON e.id = m.epic_id WHERE m.backlog_item_id = $backlog", epicId);
            membership.Parameters.AddWithValue("$backlog", backlogItemId);
            using (var reader = membership.ExecuteReader())
                if (reader.Read() && reader.GetString(0) != epicId)
                    throw new InvalidOperationException($"Backlog item {Short(backlogItemId)} belongs to epic {reader.GetString(1)} ({Short(reader.GetString(0))})");
            var duplicate = rows.FirstOrDefault(row => row.Item.BacklogItemId == backlogItemId);
            if (duplicate is not null)
                throw new InvalidOperationException($"Backlog item {Short(backlogItemId)} is already in this plan at {duplicate.Item.Position}");

            Execute(connection, "INSERT OR IGNORE INTO backlog_epic_memberships (backlog_item_id, epic_id, created_at, created_by) VALUES ($backlog, $epic, $now, $actor)", epicId,
                ("$backlog", backlogItemId), ("$now", Now()), ("$actor", actor));
            Insert(connection, epicId, rows, position, "slice", backlogItemId, null, actor);
        });
    }

    public Task AddStepAsync(string epicId, string text, int? at = null, string actor = "operator")
    {
        text = RequireText(text, nameof(text));
        RequireText(actor, nameof(actor));
        return WriteAsync(epicId, connection =>
        {
            var rows = Rows(connection, epicId);
            Insert(connection, epicId, rows, ValidateInsertion(at, rows.Count), "step", null, text, actor);
        });
    }

    public Task MoveAsync(string epicId, int from, int to) => WriteAsync(epicId, connection =>
    {
        var rows = Rows(connection, epicId);
        ValidatePosition(from, rows.Count);
        ValidatePosition(to, rows.Count);
        if (from == to) return;
        var row = rows[from - 1];
        rows.RemoveAt(from - 1);
        rows.Insert(to - 1, row);
        Renumber(connection, epicId, rows);
    });

    public Task RemoveAsync(string epicId, int position) => WriteAsync(epicId, connection =>
    {
        var rows = Rows(connection, epicId);
        ValidatePosition(position, rows.Count);
        Execute(connection, "DELETE FROM epic_plan_items WHERE id = $id AND epic_id = $epic", epicId, ("$id", rows[position - 1].Id));
        rows.RemoveAt(position - 1);
        Renumber(connection, epicId, rows);
    });

    public Task MarkStepDoneAsync(string epicId, int position) => WriteAsync(epicId, connection =>
    {
        var rows = Rows(connection, epicId);
        ValidatePosition(position, rows.Count);
        var row = rows[position - 1];
        if (row.Item.Kind == EpicPlanItemKind.Slice)
            throw new InvalidOperationException("slice status follows its backlog item and goal");
        if (!row.Item.Done)
            Execute(connection, "UPDATE epic_plan_items SET done = 1 WHERE id = $id AND epic_id = $epic", epicId, ("$id", row.Id));
    });

    public Task AddDecisionAsync(string epicId, string text, string? decidedBy = null)
    {
        text = RequireText(text, nameof(text));
        if (decidedBy is not null) decidedBy = RequireText(decidedBy, nameof(decidedBy));
        return WriteAsync(epicId, connection => Execute(connection,
            "INSERT INTO epic_decisions (epic_id, decided_at, text, decided_by) VALUES ($epic, $now, $text, $by)", epicId,
            ("$now", Now()), ("$text", text), ("$by", decidedBy)));
    }

    public Task SetBarAsync(string epicId, string? text) => WriteAsync(epicId, connection => Execute(connection,
        "UPDATE epics SET bar = $text WHERE id = $epic", epicId, ("$text", string.IsNullOrWhiteSpace(text) ? null : text.Trim())));

    private async Task WriteAsync(string epicId, Action<SqliteConnection> write)
    {
        if (_readOnly) throw new InvalidOperationException("Plan store is read-only.");
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var connection = Open(_path, false);
                Execute(connection, "BEGIN IMMEDIATE", epicId);
                try
                {
                    RequireEpic(connection, epicId);
                    write(connection);
                    Execute(connection, "COMMIT", epicId);
                    return;
                }
                catch
                {
                    try { Execute(connection, "ROLLBACK", epicId); } catch (SqliteException) { }
                    throw;
                }
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode is 5 or 6 && attempt < 6)
            {
                await Task.Delay(25 * (attempt + 1)).ConfigureAwait(false);
            }
        }
    }

    private static void Insert(SqliteConnection connection, string epicId, List<Row> rows, int position,
        string kind, string? backlogId, string? text, string actor)
    {
        Execute(connection, """
            INSERT INTO epic_plan_items (epic_id, position, kind, backlog_item_id, text, created_at, created_by)
            VALUES ($epic, $position, $kind, $backlog, $text, $now, $actor)
            """, epicId, ("$position", position), ("$kind", kind), ("$backlog", backlogId), ("$text", text), ("$now", Now()), ("$actor", actor));
        var id = Convert.ToInt64(Scalar(connection, "SELECT last_insert_rowid()", epicId), CultureInfo.InvariantCulture);
        rows.Insert(position - 1, new(id, new(position, kind == "slice" ? EpicPlanItemKind.Slice : EpicPlanItemKind.Step, backlogId, text, false)));
        Renumber(connection, epicId, rows);
    }

    private static void Renumber(SqliteConnection connection, string epicId, IReadOnlyList<Row> rows)
    {
        for (var index = 0; index < rows.Count; index++)
            Execute(connection, "UPDATE epic_plan_items SET position = $position WHERE id = $id AND epic_id = $epic", epicId,
                ("$position", index + 1), ("$id", rows[index].Id));
    }

    private sealed record Row(long Id, EpicPlanItem Item);
    private static List<Row> Rows(SqliteConnection connection, string epicId)
    {
        using var command = Command(connection,
            "SELECT id, position, kind, backlog_item_id, text, done FROM epic_plan_items WHERE epic_id = $epic ORDER BY position, id", epicId);
        using var reader = command.ExecuteReader();
        var rows = new List<Row>();
        while (reader.Read())
            rows.Add(new(reader.GetInt64(0), new(reader.GetInt32(1), reader.GetString(2) switch
            {
                "slice" => EpicPlanItemKind.Slice, "step" => EpicPlanItemKind.Step,
                var kind => throw new InvalidOperationException($"Unknown plan item kind '{kind}'.")
            }, reader.IsDBNull(3) ? null : reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetBoolean(5))));
        return rows;
    }

    private static SqliteConnection Open(string path, bool readOnly)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = path, Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout=30000; PRAGMA foreign_keys=ON";
        command.ExecuteNonQuery();
        return connection;
    }

    private static SqliteCommand Command(SqliteConnection connection, string sql, string epicId,
        params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$epic", epicId);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }

    private static void Execute(SqliteConnection connection, string sql, string epicId,
        params (string Name, object? Value)[] parameters)
    {
        using var command = Command(connection, sql, epicId, parameters);
        command.ExecuteNonQuery();
    }

    private static object? Scalar(SqliteConnection connection, string sql, string epicId)
    {
        using var command = Command(connection, sql, epicId);
        return command.ExecuteScalar();
    }

    private static void RequireEpic(SqliteConnection connection, string epicId)
    {
        if (Scalar(connection, "SELECT 1 FROM epics WHERE id = $epic", epicId) is null)
            throw new InvalidOperationException($"Epic '{epicId}' was not found.");
    }

    internal static int ValidateInsertion(int? position, int count)
    {
        var result = position ?? count + 1;
        if (result < 1 || result > count + 1) throw new ArgumentException($"Position must be between 1 and {count + 1}.");
        return result;
    }

    private static void ValidatePosition(int position, int count)
    {
        if (position < 1 || position > count) throw new ArgumentException($"Position must be between 1 and {count}.");
    }

    private static string RequireText(string text, string name) => string.IsNullOrWhiteSpace(text)
        ? throw new ArgumentException($"{name} must not be blank.") : text.Trim();
    private static string Now() => DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
    private static string Short(string id) => id[..Math.Min(8, id.Length)];
}
