using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class ConductorStatusLatestLandingReader
{
    internal static ConductorStatusSnapshot.LandingObservation? Read(string sqliteStatePath)
    {
        if (!File.Exists(sqliteStatePath)) return null;
        try
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = sqliteStatePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false
            }.ToString());
            connection.Open();
            using var schema = connection.CreateCommand();
            schema.CommandText = "SELECT 1 FROM sqlite_schema WHERE type = 'table' AND name = 'goal_promotion_facts'";
            if (schema.ExecuteScalar() is null) return null;

            using var command = connection.CreateCommand();
            command.CommandText = "SELECT goal_id, promoted_at FROM goal_promotion_facts ORDER BY promoted_at DESC, rowid DESC";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (reader.IsDBNull(0) || reader.IsDBNull(1)) continue;
                if (DateTimeOffset.TryParseExact(reader.GetString(1), "O", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var at))
                    return new(reader.GetString(0), at);
            }
            return null;
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
