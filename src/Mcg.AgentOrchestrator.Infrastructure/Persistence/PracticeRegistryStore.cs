using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed class PracticeRegistryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string _dbPath;
    private string ConnectionString => $"Data Source={_dbPath};Mode=ReadWriteCreate;Pooling=False;";

    public PracticeRegistryStore(string dbPath)
    {
        if (string.IsNullOrWhiteSpace(dbPath))
            throw new ArgumentException("Value cannot be empty.", nameof(dbPath));

        _dbPath = dbPath;
        var directory = Path.GetDirectoryName(_dbPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        using var conn = OpenConnection();
        EnsureSchemaAndSeed(conn);
    }

    public IReadOnlyList<EngineeringPractice> ListActive()
    {
        using var conn = OpenConnection();
        EnsureSchemaAndSeed(conn);
        return ListActive(conn);
    }

    public void Upsert(EngineeringPractice practice)
    {
        ArgumentNullException.ThrowIfNull(practice);
        using var conn = OpenConnection();
        EnsureSchemaAndSeed(conn);
        Upsert(conn, practice, overwriteExisting: true);
    }

    internal static void EnsureSchemaAndSeed(SqliteConnection conn)
    {
        RunNonQuery(conn, """
            CREATE TABLE IF NOT EXISTS engineering_practices (
                id                  TEXT PRIMARY KEY,
                name                TEXT NOT NULL,
                constraint_text     TEXT NOT NULL,
                reviewer_check      TEXT NOT NULL,
                scope_patterns_json TEXT NOT NULL,
                provenance_json     TEXT NOT NULL,
                priority            INTEGER NOT NULL,
                enabled             INTEGER NOT NULL,
                seeded_at           TEXT NOT NULL,
                updated_at          TEXT NOT NULL
            )
            """);
        RunNonQuery(conn, "CREATE INDEX IF NOT EXISTS ix_engineering_practices_enabled_priority ON engineering_practices(enabled, priority)");

        foreach (var practice in EngineeringPracticeDefaults.SeedEntries)
        {
            Upsert(conn, practice, overwriteExisting: false);
        }
    }

    internal static IReadOnlyList<EngineeringPractice> ListActive(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, name, constraint_text, reviewer_check, scope_patterns_json, provenance_json, priority, enabled
            FROM engineering_practices
            WHERE enabled = 1
            ORDER BY priority DESC, name ASC
            """;

        using var reader = cmd.ExecuteReader();
        var result = new List<EngineeringPractice>();
        while (reader.Read())
        {
            result.Add(ReadPractice(reader));
        }

        return result;
    }

    private SqliteConnection OpenConnection()
    {
        var conn = new SqliteConnection(ConnectionString);
        conn.Open();
        RunNonQuery(conn, "PRAGMA busy_timeout=30000");
        RunNonQuery(conn, "PRAGMA journal_mode=WAL");
        return conn;
    }

    private static void Upsert(SqliteConnection conn, EngineeringPractice practice, bool overwriteExisting)
    {
        if (string.IsNullOrWhiteSpace(practice.Id))
            throw new ArgumentException("Practice id cannot be empty.", nameof(practice));
        if (string.IsNullOrWhiteSpace(practice.Name))
            throw new ArgumentException("Practice name cannot be empty.", nameof(practice));

        using var cmd = conn.CreateCommand();
        cmd.CommandText = overwriteExisting
            ? """
                INSERT INTO engineering_practices (
                    id, name, constraint_text, reviewer_check, scope_patterns_json, provenance_json,
                    priority, enabled, seeded_at, updated_at)
                VALUES (
                    $id, $name, $constraint_text, $reviewer_check, $scope_patterns_json, $provenance_json,
                    $priority, $enabled, $seeded_at, $updated_at)
                ON CONFLICT(id) DO UPDATE SET
                    name = excluded.name,
                    constraint_text = excluded.constraint_text,
                    reviewer_check = excluded.reviewer_check,
                    scope_patterns_json = excluded.scope_patterns_json,
                    provenance_json = excluded.provenance_json,
                    priority = excluded.priority,
                    enabled = excluded.enabled,
                    updated_at = excluded.updated_at
                """
            : """
                INSERT OR IGNORE INTO engineering_practices (
                    id, name, constraint_text, reviewer_check, scope_patterns_json, provenance_json,
                    priority, enabled, seeded_at, updated_at)
                VALUES (
                    $id, $name, $constraint_text, $reviewer_check, $scope_patterns_json, $provenance_json,
                    $priority, $enabled, $seeded_at, $updated_at)
                """;
        var now = DateTimeOffset.UtcNow.ToString("O");
        cmd.Parameters.AddWithValue("$id", practice.Id.Trim());
        cmd.Parameters.AddWithValue("$name", practice.Name.Trim());
        cmd.Parameters.AddWithValue("$constraint_text", practice.Constraint.Trim());
        cmd.Parameters.AddWithValue("$reviewer_check", practice.ReviewerCheck.Trim());
        cmd.Parameters.AddWithValue("$scope_patterns_json", JsonSerializer.Serialize(practice.ScopePatterns, JsonOptions));
        cmd.Parameters.AddWithValue("$provenance_json", JsonSerializer.Serialize(practice.Provenance, JsonOptions));
        cmd.Parameters.AddWithValue("$priority", practice.Priority);
        cmd.Parameters.AddWithValue("$enabled", practice.IsEnabled ? 1 : 0);
        cmd.Parameters.AddWithValue("$seeded_at", now);
        cmd.Parameters.AddWithValue("$updated_at", now);
        cmd.ExecuteNonQuery();
    }

    private static EngineeringPractice ReadPractice(SqliteDataReader reader)
    {
        var scopePatterns = JsonSerializer.Deserialize<List<string>>(reader.GetString(4), JsonOptions) ?? [];
        var provenance = JsonSerializer.Deserialize<List<EngineeringPracticeProvenance>>(reader.GetString(5), JsonOptions) ?? [];
        return new EngineeringPractice(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            scopePatterns,
            provenance,
            reader.GetInt32(6),
            reader.GetInt32(7) != 0);
    }

    private static void RunNonQuery(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
