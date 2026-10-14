using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.Infrastructure;

// A single-store read snapshot. Never runs setup or retains evidence between requests.
public sealed class SqliteReadSession : IDisposable
{
    public SqliteConnection Connection { get; }

    public SqliteReadSession(string path, Action<string, SqliteConnection>? observe = null)
    {
        Connection = new(new SqliteConnectionStringBuilder
        { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        try
        {
            Connection.Open();
            observe?.Invoke(path, Connection);
            using var command = Connection.CreateCommand();
            command.CommandText = "BEGIN";
            command.ExecuteNonQuery();
        }
        catch { Connection.Dispose(); throw; }
    }

    public void Dispose() => Connection.Dispose();

    public static string BindIds(SqliteCommand command, IEnumerable<string> ids, string prefix = "id")
    {
        var names = ids.Distinct(StringComparer.Ordinal).Select((id, index) =>
        {
            var name = $"${prefix}{index}";
            command.Parameters.AddWithValue(name, id);
            return name;
        }).ToArray();
        // Empty scope must never become a global query.
        return names.Length == 0 ? "NULL" : string.Join(",", names);
    }
}
