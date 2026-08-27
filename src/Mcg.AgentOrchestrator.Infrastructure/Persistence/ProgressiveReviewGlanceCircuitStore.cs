using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed record ProgressiveReviewGlanceContractIdentity(
    string Provider,
    string Profile,
    string Model,
    string CommandFingerprint,
    string OutputFormat,
    string ParserContractVersion)
{
    public string CircuitIdentity => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
        string.Join("\n", Provider, Profile, Model, CommandFingerprint, OutputFormat, ParserContractVersion))));
}

public enum ProgressiveReviewGlanceCircuitAdmissionKind
{
    ProbeAcquired,
    SuppressedOpen,
    SuppressedProbeInFlight
}

public sealed record ProgressiveReviewGlanceCircuitAdmission(
    ProgressiveReviewGlanceCircuitAdmissionKind Kind,
    string CircuitIdentity,
    string? ProbeLeaseId = null,
    string? OpeningCause = null,
    string? OpeningReason = null,
    int SuppressionCount = 0);

public interface IProgressiveReviewGlanceCircuitStore
{
    ProgressiveReviewGlanceCircuitAdmission TryAcquireProbe(
        ProgressiveReviewGlanceContractIdentity identity,
        DateTimeOffset now,
        TimeSpan leaseDuration);

    void CompleteProbe(
        ProgressiveReviewGlanceContractIdentity identity,
        string probeLeaseId,
        bool openCircuit,
        bool resetOpenCircuits,
        string completionCause,
        string completionReason,
        DateTimeOffset now);
}

public sealed class InMemoryProgressiveReviewGlanceCircuitStore : IProgressiveReviewGlanceCircuitStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public ProgressiveReviewGlanceCircuitAdmission TryAcquireProbe(
        ProgressiveReviewGlanceContractIdentity identity,
        DateTimeOffset now,
        TimeSpan leaseDuration)
    {
        var key = identity.CircuitIdentity;
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var existing))
            {
                if (existing.State == "Open")
                {
                    existing.SuppressionCount++;
                    return new(ProgressiveReviewGlanceCircuitAdmissionKind.SuppressedOpen, key,
                        OpeningCause: existing.Cause, OpeningReason: existing.Reason,
                        SuppressionCount: existing.SuppressionCount);
                }

                if (existing.State == "ProbeInFlight" && existing.LeaseExpiresAt > now)
                {
                    existing.SuppressionCount++;
                    return new(ProgressiveReviewGlanceCircuitAdmissionKind.SuppressedProbeInFlight, key,
                        OpeningCause: existing.Cause, OpeningReason: existing.Reason,
                        SuppressionCount: existing.SuppressionCount);
                }
            }

            var leaseId = Guid.NewGuid().ToString("N");
            _entries[key] = new Entry("ProbeInFlight", leaseId, now + leaseDuration, null, null, 0);
            return new(ProgressiveReviewGlanceCircuitAdmissionKind.ProbeAcquired, key, leaseId);
        }
    }

    public void CompleteProbe(
        ProgressiveReviewGlanceContractIdentity identity,
        string probeLeaseId,
        bool openCircuit,
        bool resetOpenCircuits,
        string completionCause,
        string completionReason,
        DateTimeOffset now)
    {
        lock (_gate)
        {
            var key = identity.CircuitIdentity;
            if (!_entries.TryGetValue(key, out var entry) ||
                entry.State != "ProbeInFlight" ||
                !string.Equals(entry.LeaseId, probeLeaseId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("progressive glance circuit probe completion does not own the active lease");
            }

            if (resetOpenCircuits)
            {
                foreach (var openKey in _entries
                    .Where(pair => pair.Value.State == "Open")
                    .Select(pair => pair.Key)
                    .ToArray())
                {
                    var open = _entries[openKey];
                    _entries[openKey] = new Entry(
                        "Closed",
                        null,
                        null,
                        completionCause,
                        completionReason,
                        open.SuppressionCount);
                }
            }

            _entries[key] = openCircuit
                ? new Entry("Open", null, null, completionCause, completionReason, entry.SuppressionCount)
                : new Entry("Closed", null, null, completionCause, completionReason, entry.SuppressionCount);
        }
    }

    private sealed class Entry(
        string state,
        string? leaseId,
        DateTimeOffset? leaseExpiresAt,
        string? cause,
        string? reason,
        int suppressionCount)
    {
        public string State { get; } = state;
        public string? LeaseId { get; } = leaseId;
        public DateTimeOffset? LeaseExpiresAt { get; } = leaseExpiresAt;
        public string? Cause { get; } = cause;
        public string? Reason { get; } = reason;
        public int SuppressionCount { get; set; } = suppressionCount;
    }
}

public sealed class SqliteProgressiveReviewGlanceCircuitStore : IProgressiveReviewGlanceCircuitStore
{
    private readonly string _dbPath;

    public SqliteProgressiveReviewGlanceCircuitStore(string dbPath)
    {
        _dbPath = dbPath;
        EnsureSchema();
    }

    public static SqliteProgressiveReviewGlanceCircuitStore ForDirectory(string orchestratorDirectory) =>
        new(Path.Combine(orchestratorDirectory, "progressive-review-glance-circuit.db"));

    public ProgressiveReviewGlanceCircuitAdmission TryAcquireProbe(
        ProgressiveReviewGlanceContractIdentity identity,
        DateTimeOffset now,
        TimeSpan leaseDuration)
    {
        using var connection = OpenConnection();
        Execute(connection, "BEGIN IMMEDIATE");
        try
        {
            var key = identity.CircuitIdentity;
            var current = Read(connection, key);
            if (current is not null && current.State == "Open")
            {
                var count = IncrementSuppression(connection, key, now);
                Execute(connection, "COMMIT");
                return new(ProgressiveReviewGlanceCircuitAdmissionKind.SuppressedOpen, key,
                    OpeningCause: current.Cause, OpeningReason: current.Reason, SuppressionCount: count);
            }

            if (current is not null && current.State == "ProbeInFlight" && current.LeaseExpiresAt > now)
            {
                var count = IncrementSuppression(connection, key, now);
                Execute(connection, "COMMIT");
                return new(ProgressiveReviewGlanceCircuitAdmissionKind.SuppressedProbeInFlight, key,
                    OpeningCause: current.Cause, OpeningReason: current.Reason, SuppressionCount: count);
            }

            var leaseId = Guid.NewGuid().ToString("N");
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO progressive_review_glance_circuits (
                    circuit_identity, provider, profile, model, command_fingerprint, output_format,
                    parser_contract_version, state, probe_lease_id, probe_expires_at,
                    opening_cause, opening_reason, suppression_count, updated_at)
                VALUES ($id, $provider, $profile, $model, $command, $format, $parser,
                        'ProbeInFlight', $lease, $expires, NULL, NULL, 0, $updated)
                ON CONFLICT(circuit_identity) DO UPDATE SET
                    provider = excluded.provider,
                    profile = excluded.profile,
                    model = excluded.model,
                    command_fingerprint = excluded.command_fingerprint,
                    output_format = excluded.output_format,
                    parser_contract_version = excluded.parser_contract_version,
                    state = 'ProbeInFlight',
                    probe_lease_id = excluded.probe_lease_id,
                    probe_expires_at = excluded.probe_expires_at,
                    opening_cause = NULL,
                    opening_reason = NULL,
                    suppression_count = 0,
                    updated_at = excluded.updated_at
                """;
            AddIdentity(command, identity);
            command.Parameters.AddWithValue("$id", key);
            command.Parameters.AddWithValue("$lease", leaseId);
            command.Parameters.AddWithValue("$expires", Format(now + leaseDuration));
            command.Parameters.AddWithValue("$updated", Format(now));
            command.ExecuteNonQuery();
            Execute(connection, "COMMIT");
            return new(ProgressiveReviewGlanceCircuitAdmissionKind.ProbeAcquired, key, leaseId);
        }
        catch
        {
            TryRollback(connection);
            throw;
        }
    }

    public void CompleteProbe(
        ProgressiveReviewGlanceContractIdentity identity,
        string probeLeaseId,
        bool openCircuit,
        bool resetOpenCircuits,
        string completionCause,
        string completionReason,
        DateTimeOffset now)
    {
        using var connection = OpenConnection();
        Execute(connection, "BEGIN IMMEDIATE");
        try
        {
            if (resetOpenCircuits)
            {
                using var closePrevious = connection.CreateCommand();
                closePrevious.CommandText = """
                    UPDATE progressive_review_glance_circuits
                    SET state = 'Closed',
                        probe_lease_id = NULL,
                        probe_expires_at = NULL,
                        opening_cause = $cause,
                        opening_reason = $reason,
                        updated_at = $updated
                    WHERE state = 'Open'
                    """;
                closePrevious.Parameters.AddWithValue("$cause", completionCause);
                closePrevious.Parameters.AddWithValue("$reason", completionReason);
                closePrevious.Parameters.AddWithValue("$updated", Format(now));
                closePrevious.ExecuteNonQuery();
            }

            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE progressive_review_glance_circuits
                SET state = $state,
                    probe_lease_id = NULL,
                    probe_expires_at = NULL,
                    opening_cause = $cause,
                    opening_reason = $reason,
                    updated_at = $updated
                WHERE circuit_identity = $id
                  AND state = 'ProbeInFlight'
                  AND probe_lease_id = $lease
                """;
            command.Parameters.AddWithValue("$state", openCircuit ? "Open" : "Closed");
            command.Parameters.AddWithValue("$cause", completionCause);
            command.Parameters.AddWithValue("$reason", completionReason);
            command.Parameters.AddWithValue("$updated", Format(now));
            command.Parameters.AddWithValue("$id", identity.CircuitIdentity);
            command.Parameters.AddWithValue("$lease", probeLeaseId);
            if (command.ExecuteNonQuery() != 1)
                throw new InvalidOperationException("progressive glance circuit probe completion does not own the active lease");

            Execute(connection, "COMMIT");
        }
        catch
        {
            TryRollback(connection);
            throw;
        }
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = false
        }.ToString());
        connection.Open();
        Execute(connection, "PRAGMA busy_timeout=30000");
        return connection;
    }

    private void EnsureSchema()
    {
        var directory = Path.GetDirectoryName(_dbPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
        using var connection = OpenConnection();
        Execute(connection, """
            CREATE TABLE IF NOT EXISTS progressive_review_glance_circuits (
                circuit_identity       TEXT PRIMARY KEY,
                provider               TEXT NOT NULL,
                profile                TEXT NOT NULL,
                model                  TEXT NOT NULL,
                command_fingerprint    TEXT NOT NULL,
                output_format          TEXT NOT NULL,
                parser_contract_version TEXT NOT NULL,
                state                  TEXT NOT NULL CHECK(state IN ('Closed', 'ProbeInFlight', 'Open')),
                probe_lease_id         TEXT,
                probe_expires_at       TEXT,
                opening_cause          TEXT,
                opening_reason         TEXT,
                suppression_count      INTEGER NOT NULL DEFAULT 0,
                updated_at             TEXT NOT NULL
            )
            """);
    }

    private static CircuitRow? Read(SqliteConnection connection, string key)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT state, probe_lease_id, probe_expires_at, opening_cause, opening_reason
            FROM progressive_review_glance_circuits WHERE circuit_identity = $id
            """;
        command.Parameters.AddWithValue("$id", key);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return null;
        return new CircuitRow(
            reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4));
    }

    private static int IncrementSuppression(SqliteConnection connection, string key, DateTimeOffset now)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE progressive_review_glance_circuits
            SET suppression_count = suppression_count + 1, updated_at = $updated
            WHERE circuit_identity = $id
            RETURNING suppression_count
            """;
        command.Parameters.AddWithValue("$updated", Format(now));
        command.Parameters.AddWithValue("$id", key);
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static void AddIdentity(SqliteCommand command, ProgressiveReviewGlanceContractIdentity identity)
    {
        command.Parameters.AddWithValue("$provider", identity.Provider);
        command.Parameters.AddWithValue("$profile", identity.Profile);
        command.Parameters.AddWithValue("$model", identity.Model);
        command.Parameters.AddWithValue("$command", identity.CommandFingerprint);
        command.Parameters.AddWithValue("$format", identity.OutputFormat);
        command.Parameters.AddWithValue("$parser", identity.ParserContractVersion);
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void TryRollback(SqliteConnection connection)
    {
        try { Execute(connection, "ROLLBACK"); }
        catch (SqliteException) { }
    }

    private static string Format(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private sealed record CircuitRow(
        string State,
        string? LeaseId,
        DateTimeOffset? LeaseExpiresAt,
        string? Cause,
        string? Reason);
}
