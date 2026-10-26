using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class SqliteGoalBoardRowReader
{
    internal static async Task<IReadOnlyList<GoalSummary>> ReadAsync(SqliteConnection connection,
        GoalBoardRowQuery query, Action<string>? observer, CancellationToken cancellationToken)
    {
        query.EnsureValid();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT id, status, updated_at,
                CASE WHEN $title_chars = 0 THEN ''
                    WHEN $marker IS NOT NULL AND instr(objective, $marker) > 0 THEN objective
                    ELSE substr(objective, 1, $title_chars) END
            FROM goals
            WHERE status NOT IN ({GoalBoardRowQuery.TerminalStatusSql})
                OR id IN (SELECT id FROM goals WHERE status IN ({GoalBoardRowQuery.TerminalStatusSql})
                    ORDER BY updated_at DESC LIMIT $terminal_limit)
            ORDER BY updated_at DESC
            """;
        command.Parameters.AddWithValue("$title_chars", query.TitlePrefixChars);
        command.Parameters.AddWithValue("$marker", (object?)query.FullObjectiveMarker ?? DBNull.Value);
        command.Parameters.AddWithValue("$terminal_limit", query.RecentTerminalLimit ?? -1);
        observer?.Invoke(command.CommandText);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<GoalSummary>();
        while (await reader.ReadAsync(cancellationToken))
            rows.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(3), reader.GetString(2)));
        return rows;
    }
}
