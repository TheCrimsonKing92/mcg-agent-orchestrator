using System.Globalization;
using Mcg.AgentOrchestrator.Core;
using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.Infrastructure;

// Append-only attributed events; expiry is a projection of these events and the reader's clock.
public sealed class SqliteOperatorDirectiveStore(string databasePath)
{
    public const string DatabaseFileName = "operator-directives.db";
    private readonly string _databasePath = Path.GetFullPath(databasePath);
    private readonly bool _readOnly;

    private SqliteOperatorDirectiveStore(string databasePath, bool readOnly) : this(databasePath) =>
        _readOnly = readOnly;

    public static SqliteOperatorDirectiveStore OpenReadOnly(string databasePath)
    {
        var store = new SqliteOperatorDirectiveStore(databasePath, true);
        using var connection = store.OpenVerified(SqliteOpenMode.ReadOnly);
        return store;
    }

    public bool HasRecordSource(string sourceIntentId) => HasStoredSource("directives", sourceIntentId);
    public bool HasRetirementSource(string sourceIntentId) => HasStoredSource("directive_retirements", sourceIntentId);
    public bool HasHandoffSource(string sourceIntentId) => HasStoredSource("directive_handoffs", sourceIntentId);

    public bool TryAppendDirective(OperatorDirective directive, string sourceIntentId)
    {
        using var connection = OpenWritable();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO directives
                (id, source_intent_id, kind, text, actor, actor_kind, channel, recorded_at, expires_at, until_handoff)
            VALUES ($id, $source, $kind, $text, $actor, $actorKind, $channel, $at, $expires, $until)
            """;
        command.Parameters.AddWithValue("$id", directive.Id);
        command.Parameters.AddWithValue("$source", sourceIntentId);
        command.Parameters.AddWithValue("$kind", directive.Kind);
        command.Parameters.AddWithValue("$text", directive.Text);
        AddAttribution(command, directive.Actor, directive.ActorKind, directive.Channel, directive.RecordedAt);
        command.Parameters.AddWithValue("$expires", directive.ExpiresAt is { } expiry ? RoundTrip(expiry) : DBNull.Value);
        command.Parameters.AddWithValue("$until", directive.UntilHandoff);
        var inserted = command.ExecuteNonQuery() == 1;
        if (!inserted && !HasSource(connection, "directives", sourceIntentId))
            throw new InvalidOperationException($"Directive id '{directive.Id}' is already owned by another intent.");
        return inserted;
    }

    // Resolve prefixes and distinguish replay/unknown/retired while holding the write transaction.
    public (bool Appended, bool Replayed, string? Rejection) TryAppendRetirement(
        string directiveId, string sourceIntentId, string reason, string actor,
        OperatorActorKind actorKind, string channel, DateTimeOffset at)
    {
        EnsureWritable();
        if (!File.Exists(_databasePath)) return (false, false, "directive-not-found");
        using var connection = OpenWritable();
        using var transaction = connection.BeginTransaction(deferred: false);
        if (HasSource(connection, "directive_retirements", sourceIntentId, transaction))
            return (false, true, null);
        if (string.IsNullOrWhiteSpace(directiveId) || directiveId.Length < 8)
            return (false, false, "directive-id-prefix-too-short");
        var matches = new List<string>();
        using (var find = connection.CreateCommand())
        {
            find.Transaction = transaction;
            find.CommandText = "SELECT id FROM directives WHERE substr(id, 1, length($prefix)) = $prefix COLLATE NOCASE LIMIT 2";
            find.Parameters.AddWithValue("$prefix", directiveId);
            using var reader = find.ExecuteReader();
            while (reader.Read()) matches.Add(reader.GetString(0));
        }
        if (matches.Count == 0) return (false, false, "directive-not-found");
        if (matches.Count > 1) return (false, false, "directive-id-ambiguous");
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR IGNORE INTO directive_retirements
                (directive_id, source_intent_id, reason, actor, actor_kind, channel, retired_at)
            VALUES ($id, $source, $reason, $actor, $actorKind, $channel, $at)
            """;
        command.Parameters.AddWithValue("$id", matches[0]);
        command.Parameters.AddWithValue("$source", sourceIntentId);
        command.Parameters.AddWithValue("$reason", reason);
        AddAttribution(command, actor, actorKind, channel, at);
        if (command.ExecuteNonQuery() != 1) return (false, false, "directive-already-retired");
        transaction.Commit();
        return (true, false, null);
    }

    public bool TryAppendHandoff(string id, string sourceIntentId, string? note,
        string actor, OperatorActorKind actorKind, string channel, DateTimeOffset at)
    {
        using var connection = OpenWritable();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO directive_handoffs
                (id, source_intent_id, note, actor, actor_kind, channel, recorded_at)
            VALUES ($id, $source, $note, $actor, $actorKind, $channel, $at)
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$source", sourceIntentId);
        command.Parameters.AddWithValue("$note", (object?)note ?? DBNull.Value);
        AddAttribution(command, actor, actorKind, channel, at);
        var inserted = command.ExecuteNonQuery() == 1;
        if (!inserted && !HasSource(connection, "directive_handoffs", sourceIntentId))
            throw new InvalidOperationException($"Handoff id '{id}' is already owned by another intent.");
        return inserted;
    }

    public IReadOnlyList<OperatorDirective> List(DateTimeOffset now, bool includeRetired = false)
    {
        if (!File.Exists(_databasePath)) return [];
        using var connection = OpenVerified(SqliteOpenMode.ReadOnly);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT d.id, d.kind, d.text, d.actor, d.actor_kind, d.channel,
                   d.recorded_at, d.expires_at, d.until_handoff, r.reason, r.actor, r.retired_at,
                   EXISTS(SELECT 1 FROM directive_handoffs h WHERE h.recorded_at > d.recorded_at)
            FROM directives d LEFT JOIN directive_retirements r ON r.directive_id=d.id
            ORDER BY d.recorded_at, d.id
            """;
        using var reader = command.ExecuteReader();
        var rows = new List<OperatorDirective>();
        while (reader.Read())
        {
            var retired = !reader.IsDBNull(11);
            if (retired && !includeRetired) continue;
            DateTimeOffset? expires = reader.IsDBNull(7) ? null : Parse(reader.GetString(7));
            var until = reader.GetBoolean(8);
            rows.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), Enum.Parse<OperatorActorKind>(reader.GetString(4)), reader.GetString(5),
                Parse(reader.GetString(6)), expires, until,
                retired ? reader.GetString(9) : null, retired ? reader.GetString(10) : null,
                retired ? Parse(reader.GetString(11)) : null,
                expires <= now || until && reader.GetBoolean(12)));
        }
        return rows;
    }

    private SqliteConnection OpenWritable()
    {
        EnsureWritable();
        OperatorDirectiveStoreSetup.Setup(_databasePath);
        return OpenVerified(SqliteOpenMode.ReadWrite);
    }

    private void EnsureWritable()
    {
        if (_readOnly) throw new InvalidOperationException("Operator directives store was opened read-only.");
    }

    private SqliteConnection OpenVerified(SqliteOpenMode mode)
    {
        if (!File.Exists(_databasePath)) throw SchemaSetupRequired(StoreSchemaState.Missing);
        var connection = OpenConnection(_databasePath, mode);
        try
        {
            var state = StoreSchemaVersions.Verify(connection, StoreSchemaRegistry.OperatorDirectives);
            if (state != StoreSchemaState.Current) throw SchemaSetupRequired(state);
            return connection;
        }
        catch { connection.Dispose(); throw; }
    }

    private InvalidOperationException SchemaSetupRequired(StoreSchemaState state) =>
        new($"Operator directives store '{_databasePath}' schema is {state} (expected version {StoreSchemaRegistry.OperatorDirectives.CurrentVersion}); run setup.");

    internal static SqliteConnection OpenConnection(string path, SqliteOpenMode mode)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = mode, Pooling = false
        }.ToString());
        connection.Open();
        return connection;
    }

    private bool HasStoredSource(string table, string source)
    {
        if (!File.Exists(_databasePath)) return false;
        using var connection = OpenVerified(SqliteOpenMode.ReadOnly);
        return HasSource(connection, table, source);
    }

    private static bool HasSource(SqliteConnection connection, string table, string source,
        SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT EXISTS(SELECT 1 FROM {table} WHERE source_intent_id=$source)";
        command.Parameters.AddWithValue("$source", source);
        return Convert.ToInt32(command.ExecuteScalar()) == 1;
    }

    private static void AddAttribution(SqliteCommand command, string actor,
        OperatorActorKind kind, string channel, DateTimeOffset at)
    {
        command.Parameters.AddWithValue("$actor", actor);
        command.Parameters.AddWithValue("$actorKind", kind.ToString());
        command.Parameters.AddWithValue("$channel", channel);
        command.Parameters.AddWithValue("$at", RoundTrip(at));
    }

    private static string RoundTrip(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset Parse(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);
}
