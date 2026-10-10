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

public sealed record ProgressiveReviewGlanceSuppressionObservation(
    string RoundKey,
    string GoalId,
    string TaskId,
    string CircuitIdentity,
    string AdmissionOutcome,
    string OpeningCause,
    string OriginalReason,
    bool ChangedFilesTrigger,
    int AvoidedInputTokens,
    long AdmissionLatencyMilliseconds,
    string ProbeOutcome);

public sealed record ProgressiveReviewGlanceSuppressionAggregate(
    string RoundKey,
    string GoalId,
    string TaskId,
    string CircuitIdentity,
    string AdmissionOutcome,
    string OpeningCause,
    string OriginalReason,
    int AvoidedCallCount,
    int AvoidedInputTokens,
    long AdmissionLatencyMilliseconds,
    int ChangedFilesTriggerCount,
    int ElapsedTriggerCount,
    string ProbeOutcome);

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

    void AccumulateSuppression(ProgressiveReviewGlanceSuppressionObservation observation);

    IReadOnlyList<ProgressiveReviewGlanceSuppressionAggregate> DrainInactiveSuppressions(
        IReadOnlySet<string> activeRoundKeys);
}

public sealed class InMemoryProgressiveReviewGlanceCircuitStore : IProgressiveReviewGlanceCircuitStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SuppressionEntry> _suppressions = new(StringComparer.Ordinal);

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

    public void AccumulateSuppression(ProgressiveReviewGlanceSuppressionObservation observation)
    {
        ProgressiveReviewGlanceCircuitSuppression.Validate(observation);
        var key = ProgressiveReviewGlanceCircuitSuppression.AggregationKey(observation);
        lock (_gate)
        {
            if (!_suppressions.TryGetValue(key, out var aggregate))
            {
                aggregate = new SuppressionEntry(observation);
                _suppressions.Add(key, aggregate);
            }
            else
            {
                aggregate.AssertCompatible(observation);
            }

            aggregate.Add(observation);
        }
    }

    public IReadOnlyList<ProgressiveReviewGlanceSuppressionAggregate> DrainInactiveSuppressions(
        IReadOnlySet<string> activeRoundKeys)
    {
        ArgumentNullException.ThrowIfNull(activeRoundKeys);
        lock (_gate)
        {
            var keys = _suppressions
                .Where(pair => !activeRoundKeys.Contains(pair.Value.RoundKey))
                .Select(pair => pair.Key)
                .Order(StringComparer.Ordinal)
                .ToArray();
            var drained = keys.Select(key => _suppressions[key].Snapshot()).ToArray();
            foreach (var key in keys)
                _suppressions.Remove(key);
            return drained;
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

    private sealed class SuppressionEntry(ProgressiveReviewGlanceSuppressionObservation first)
    {
        public string RoundKey { get; } = first.RoundKey;
        private string GoalId { get; } = first.GoalId;
        private string TaskId { get; } = first.TaskId;
        private string CircuitIdentity { get; } = first.CircuitIdentity;
        private string AdmissionOutcome { get; } = first.AdmissionOutcome;
        private string OpeningCause { get; } = first.OpeningCause;
        private string OriginalReason { get; } = first.OriginalReason;
        private string ProbeOutcome { get; } = first.ProbeOutcome;
        private int AvoidedCallCount { get; set; }
        private int AvoidedInputTokens { get; set; }
        private long AdmissionLatencyMilliseconds { get; set; }
        private int ChangedFilesTriggerCount { get; set; }
        private int ElapsedTriggerCount { get; set; }

        public void AssertCompatible(ProgressiveReviewGlanceSuppressionObservation observation)
        {
            if (!GoalId.Equals(observation.GoalId, StringComparison.Ordinal) ||
                !TaskId.Equals(observation.TaskId, StringComparison.Ordinal) ||
                !OpeningCause.Equals(observation.OpeningCause, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("progressive glance suppression window metadata changed");
            }
        }

        public void Add(ProgressiveReviewGlanceSuppressionObservation observation)
        {
            AvoidedCallCount++;
            AvoidedInputTokens += observation.AvoidedInputTokens;
            AdmissionLatencyMilliseconds += observation.AdmissionLatencyMilliseconds;
            if (observation.ChangedFilesTrigger)
                ChangedFilesTriggerCount++;
            else
                ElapsedTriggerCount++;
        }

        public ProgressiveReviewGlanceSuppressionAggregate Snapshot() => new(
            RoundKey,
            GoalId,
            TaskId,
            CircuitIdentity,
            AdmissionOutcome,
            OpeningCause,
            OriginalReason,
            AvoidedCallCount,
            AvoidedInputTokens,
            AdmissionLatencyMilliseconds,
            ChangedFilesTriggerCount,
            ElapsedTriggerCount,
            ProbeOutcome);
    }
}

public sealed class SqliteProgressiveReviewGlanceCircuitStore : IProgressiveReviewGlanceCircuitStore
{
    private readonly string _dbPath;
    private readonly bool _readOnly;

    public SqliteProgressiveReviewGlanceCircuitStore(string dbPath) : this(dbPath, readOnly: false)
    {
        Setup(dbPath);
    }

    private SqliteProgressiveReviewGlanceCircuitStore(string dbPath, bool readOnly)
    {
        _dbPath = dbPath;
        _readOnly = readOnly;
    }

    public static SqliteProgressiveReviewGlanceCircuitStore ForDirectory(string orchestratorDirectory) =>
        new(Path.Combine(orchestratorDirectory, "progressive-review-glance-circuit.db"));

    public static SqliteProgressiveReviewGlanceCircuitStore OpenReadOnly(string dbPath)
    {
        if (!File.Exists(dbPath))
            throw SchemaSetupRequired(dbPath, StoreSchemaState.Missing);

        var store = new SqliteProgressiveReviewGlanceCircuitStore(dbPath, readOnly: true);
        using var connection = store.OpenConnection();
        var state = StoreSchemaVersions.Verify(connection, StoreSchemaRegistry.ProgressiveReviewGlanceCircuit);
        if (state != StoreSchemaState.Current)
            throw SchemaSetupRequired(dbPath, state);
        return store;
    }

    private static InvalidOperationException SchemaSetupRequired(string dbPath, StoreSchemaState state) =>
        new($"Progressive review glance circuit store '{dbPath}' schema is {state} (expected version {StoreSchemaRegistry.ProgressiveReviewGlanceCircuit.CurrentVersion}); run setup.");

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

    public void AccumulateSuppression(ProgressiveReviewGlanceSuppressionObservation observation)
    {
        ProgressiveReviewGlanceCircuitSuppression.Validate(observation);
        var key = ProgressiveReviewGlanceCircuitSuppression.AggregationKey(observation);
        using var connection = OpenConnection();
        Execute(connection, "BEGIN IMMEDIATE");
        try
        {
            var existing = ReadSuppression(connection, key);
            if (existing is not null)
            {
                existing.AssertCompatible(observation);
                using var update = connection.CreateCommand();
                update.CommandText = """
                    UPDATE progressive_review_glance_suppressions
                    SET avoided_call_count = avoided_call_count + 1,
                        avoided_input_tokens = avoided_input_tokens + $tokens,
                        admission_latency_ms = admission_latency_ms + $latency,
                        changed_files_trigger_count = changed_files_trigger_count + $changed,
                        elapsed_trigger_count = elapsed_trigger_count + $elapsed,
                        updated_at = $updated
                    WHERE aggregation_key = $key
                    """;
                AddSuppressionIncrement(update, observation);
                update.Parameters.AddWithValue("$updated", Format(DateTimeOffset.UtcNow));
                update.Parameters.AddWithValue("$key", key);
                if (update.ExecuteNonQuery() != 1)
                    throw new InvalidOperationException("progressive glance suppression window disappeared during update");
            }
            else
            {
                using var insert = connection.CreateCommand();
                insert.CommandText = """
                    INSERT INTO progressive_review_glance_suppressions (
                        aggregation_key, round_key, goal_id, task_id, circuit_identity,
                        admission_outcome, opening_cause, original_reason, probe_outcome,
                        avoided_call_count, avoided_input_tokens, admission_latency_ms,
                        changed_files_trigger_count, elapsed_trigger_count, updated_at)
                    VALUES ($key, $round, $goal, $task, $circuit, $admission, $cause, $reason, $probe,
                            1, $tokens, $latency, $changed, $elapsed, $updated)
                    """;
                AddSuppressionMetadata(insert, key, observation);
                AddSuppressionIncrement(insert, observation);
                insert.Parameters.AddWithValue("$updated", Format(DateTimeOffset.UtcNow));
                insert.ExecuteNonQuery();
            }

            Execute(connection, "COMMIT");
        }
        catch
        {
            TryRollback(connection);
            throw;
        }
    }

    public IReadOnlyList<ProgressiveReviewGlanceSuppressionAggregate> DrainInactiveSuppressions(
        IReadOnlySet<string> activeRoundKeys)
    {
        ArgumentNullException.ThrowIfNull(activeRoundKeys);
        using var connection = OpenConnection();
        Execute(connection, "BEGIN IMMEDIATE");
        try
        {
            var inactive = ReadSuppressions(connection)
                .Where(row => !activeRoundKeys.Contains(row.RoundKey))
                .ToArray();
            foreach (var row in inactive)
            {
                using var delete = connection.CreateCommand();
                delete.CommandText = "DELETE FROM progressive_review_glance_suppressions WHERE aggregation_key = $key";
                delete.Parameters.AddWithValue("$key", row.AggregationKey);
                if (delete.ExecuteNonQuery() != 1)
                    throw new InvalidOperationException("progressive glance suppression window disappeared during drain");
            }

            Execute(connection, "COMMIT");
            return inactive.Select(row => row.Aggregate).ToArray();
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

    private SqliteConnection OpenConnection() => OpenConnection(_dbPath, _readOnly);

    private static SqliteConnection OpenConnection(string dbPath, bool readOnly)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
            Cache = readOnly ? SqliteCacheMode.Default : SqliteCacheMode.Shared,
            Pooling = false
        }.ToString());
        connection.Open();
        Execute(connection, "PRAGMA busy_timeout=30000");
        return connection;
    }

    public static void Setup(string dbPath)
    {
        var directory = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
        using var connection = OpenConnection(dbPath, readOnly: false);
        var state = StoreSchemaVersions.Verify(connection, StoreSchemaRegistry.ProgressiveReviewGlanceCircuit);
        if (state is StoreSchemaState.Current or StoreSchemaState.Newer)
            return;

        Execute(connection, "BEGIN IMMEDIATE");
        try
        {
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
            Execute(connection, """
                CREATE TABLE IF NOT EXISTS progressive_review_glance_suppressions (
                    aggregation_key            TEXT PRIMARY KEY,
                    round_key                  TEXT NOT NULL,
                    goal_id                    TEXT NOT NULL,
                    task_id                    TEXT NOT NULL,
                    circuit_identity           TEXT NOT NULL,
                    admission_outcome          TEXT NOT NULL,
                    opening_cause              TEXT NOT NULL,
                    original_reason            TEXT NOT NULL,
                    probe_outcome               TEXT NOT NULL,
                    avoided_call_count          INTEGER NOT NULL,
                    avoided_input_tokens        INTEGER NOT NULL,
                    admission_latency_ms        INTEGER NOT NULL,
                    changed_files_trigger_count INTEGER NOT NULL,
                    elapsed_trigger_count       INTEGER NOT NULL,
                    updated_at                  TEXT NOT NULL
                )
                """);
            StoreSchemaVersions.UpgradeToCurrent(connection, StoreSchemaRegistry.ProgressiveReviewGlanceCircuit);
            Execute(connection, "COMMIT");
        }
        catch
        {
            TryRollback(connection);
            throw;
        }
    }

    private static SuppressionRow? ReadSuppression(SqliteConnection connection, string key)
    {
        using var command = connection.CreateCommand();
        command.CommandText = SuppressionSelect + " WHERE aggregation_key = $key";
        command.Parameters.AddWithValue("$key", key);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadSuppressionRow(reader) : null;
    }

    private static List<SuppressionRow> ReadSuppressions(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = SuppressionSelect + " ORDER BY aggregation_key";
        using var reader = command.ExecuteReader();
        var rows = new List<SuppressionRow>();
        while (reader.Read())
            rows.Add(ReadSuppressionRow(reader));
        return rows;
    }

    private static SuppressionRow ReadSuppressionRow(SqliteDataReader reader) => new(
        reader.GetString(0),
        new ProgressiveReviewGlanceSuppressionAggregate(
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetString(7),
            reader.GetInt32(9),
            reader.GetInt32(10),
            reader.GetInt64(11),
            reader.GetInt32(12),
            reader.GetInt32(13),
            reader.GetString(8)));

    private static void AddSuppressionMetadata(
        SqliteCommand command,
        string key,
        ProgressiveReviewGlanceSuppressionObservation observation)
    {
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$round", observation.RoundKey);
        command.Parameters.AddWithValue("$goal", observation.GoalId);
        command.Parameters.AddWithValue("$task", observation.TaskId);
        command.Parameters.AddWithValue("$circuit", observation.CircuitIdentity);
        command.Parameters.AddWithValue("$admission", observation.AdmissionOutcome);
        command.Parameters.AddWithValue("$cause", observation.OpeningCause);
        command.Parameters.AddWithValue("$reason", observation.OriginalReason);
        command.Parameters.AddWithValue("$probe", observation.ProbeOutcome);
    }

    private static void AddSuppressionIncrement(
        SqliteCommand command,
        ProgressiveReviewGlanceSuppressionObservation observation)
    {
        command.Parameters.AddWithValue("$tokens", observation.AvoidedInputTokens);
        command.Parameters.AddWithValue("$latency", observation.AdmissionLatencyMilliseconds);
        command.Parameters.AddWithValue("$changed", observation.ChangedFilesTrigger ? 1 : 0);
        command.Parameters.AddWithValue("$elapsed", observation.ChangedFilesTrigger ? 0 : 1);
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

    private sealed record SuppressionRow(
        string AggregationKey,
        ProgressiveReviewGlanceSuppressionAggregate Aggregate)
    {
        public string RoundKey => Aggregate.RoundKey;

        public void AssertCompatible(ProgressiveReviewGlanceSuppressionObservation observation)
        {
            if (!Aggregate.GoalId.Equals(observation.GoalId, StringComparison.Ordinal) ||
                !Aggregate.TaskId.Equals(observation.TaskId, StringComparison.Ordinal) ||
                !Aggregate.OpeningCause.Equals(observation.OpeningCause, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("progressive glance suppression window metadata changed");
            }
        }
    }

    private const string SuppressionSelect = """
        SELECT aggregation_key, round_key, goal_id, task_id, circuit_identity,
               admission_outcome, opening_cause, original_reason, probe_outcome,
               avoided_call_count, avoided_input_tokens, admission_latency_ms,
               changed_files_trigger_count, elapsed_trigger_count
        FROM progressive_review_glance_suppressions
        """;
}

internal static class ProgressiveReviewGlanceCircuitSuppression
{
    public static string AggregationKey(ProgressiveReviewGlanceSuppressionObservation observation) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join(
            "\n",
            observation.RoundKey,
            observation.CircuitIdentity,
            observation.AdmissionOutcome,
            observation.ProbeOutcome))));

    public static void Validate(ProgressiveReviewGlanceSuppressionObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        if (string.IsNullOrWhiteSpace(observation.RoundKey) ||
            string.IsNullOrWhiteSpace(observation.GoalId) ||
            string.IsNullOrWhiteSpace(observation.TaskId) ||
            string.IsNullOrWhiteSpace(observation.CircuitIdentity) ||
            string.IsNullOrWhiteSpace(observation.AdmissionOutcome) ||
            string.IsNullOrWhiteSpace(observation.OpeningCause) ||
            string.IsNullOrWhiteSpace(observation.ProbeOutcome) ||
            observation.AvoidedInputTokens < 0 ||
            observation.AdmissionLatencyMilliseconds < 0)
        {
            throw new ArgumentException("progressive glance suppression observation is incomplete or invalid", nameof(observation));
        }
    }
}
