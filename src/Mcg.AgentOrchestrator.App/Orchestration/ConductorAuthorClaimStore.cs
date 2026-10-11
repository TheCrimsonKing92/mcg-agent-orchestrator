using Microsoft.Data.Sqlite;
using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed class ConductorAuthorClaimStore
{
    private readonly string _connectionString;

    public ConductorAuthorClaimStore(string path)
    {
        AuthorClaimStoreSetup.Setup(path);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path, Pooling = false
        }.ToString();
    }

    internal bool TryClaim(ConductorAuthorItem item, DateTimeOffset now)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT OR IGNORE INTO author_claims (identity, claimed_at, outcome, item_json) VALUES ($identity, $now, 'in-flight', $item)";
        command.Parameters.AddWithValue("$identity", item.Identity);
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        command.Parameters.AddWithValue("$item", JsonSerializer.Serialize(item));
        if (command.ExecuteNonQuery() == 1) return true;
        command.CommandText = "UPDATE author_claims SET outcome = 'in-flight', claimed_at = $now, item_json = $item, attempt = attempt + 1 WHERE identity = $identity AND outcome = 'retryable'";
        return command.ExecuteNonQuery() == 1;
    }

    internal int Attempt(string identity)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT attempt FROM author_claims WHERE identity = $identity";
        command.Parameters.AddWithValue("$identity", identity);
        return command.ExecuteScalar() is long attempt ? checked((int)attempt) :
            throw new InvalidOperationException($"Author claim {identity} does not exist.");
    }

    internal void Preserve(ConductorAuthorItem item, string output)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE author_claims SET outcome = 'ready', output = $output WHERE identity = $identity AND outcome = 'in-flight'";
        command.Parameters.AddWithValue("$identity", item.Identity);
        command.Parameters.AddWithValue("$output", output);
        if (command.ExecuteNonQuery() != 1)
            throw new InvalidOperationException($"Author claim {item.Identity} is no longer in flight.");
    }

    internal IReadOnlyList<(ConductorAuthorItem Item, string Output)> Ready()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT item_json, output FROM author_claims WHERE outcome = 'ready' ORDER BY claimed_at";
        using var reader = command.ExecuteReader();
        var result = new List<(ConductorAuthorItem, string)>();
        while (reader.Read())
        {
            var item = JsonSerializer.Deserialize<ConductorAuthorItem>(reader.GetString(0)) ??
                throw new InvalidOperationException("Author ready claim has no item.");
            result.Add((item, reader.GetString(1)));
        }
        return result;
    }

    internal void Release(string identity)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM author_claims WHERE identity = $identity AND outcome = 'in-flight'";
        command.Parameters.AddWithValue("$identity", identity);
        if (command.ExecuteNonQuery() != 1)
            throw new InvalidOperationException($"Author claim {identity} is no longer in flight.");
    }

    internal void Complete(string identity, string outcome, string? intentId = null)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE author_claims SET outcome = $outcome, intent_id = $intent, output = NULL WHERE identity = $identity AND outcome IN ('in-flight', 'ready')";
        command.Parameters.AddWithValue("$outcome", outcome);
        command.Parameters.AddWithValue("$intent", (object?)intentId ?? DBNull.Value);
        command.Parameters.AddWithValue("$identity", identity);
        if (command.ExecuteNonQuery() != 1)
            throw new InvalidOperationException($"Author claim {identity} is no longer active.");
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }
}
