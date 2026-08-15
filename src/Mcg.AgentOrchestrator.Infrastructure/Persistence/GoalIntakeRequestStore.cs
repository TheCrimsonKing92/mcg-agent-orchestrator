using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.Infrastructure;

public static class GoalIntakeRequestStates
{
    public const string StillCommitting = "still-committing";
    public const string Created = "created";
    public const string Failed = "failed";
}

public enum GoalIntakeReservationKind
{
    Acquired,
    Replay
}

public sealed record GoalIntakeRequestRecord(
    string RequestKey,
    int FingerprintVersion,
    string Fingerprint,
    string State,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? GoalId,
    string? FailureCode,
    string? FailureDetail,
    string? StdoutPath,
    string? StderrPath);

public sealed record GoalIntakeReservation(
    GoalIntakeReservationKind Kind,
    GoalIntakeRequestRecord Record);

public sealed class GoalIntakeRequestStore
{
    public const int CurrentFingerprintVersion = 1;
    public const int MaximumRequestKeyLength = 200;

    private readonly string _dbPath;

    public GoalIntakeRequestStore(string dbPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
        _dbPath = Path.GetFullPath(dbPath);
    }

    public GoalIntakeReservation Reserve(string requestKey, string fingerprint)
    {
        ValidateRequestKey(requestKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint);

        using var connection = StateDbConnectionFactory.Open(_dbPath, StateDbConnectionProfile.ReadWrite);
        Execute(connection, "BEGIN IMMEDIATE");
        try
        {
            var existing = Load(connection, requestKey);
            if (existing is not null)
            {
                EnsureEquivalent(existing, fingerprint);
                Execute(connection, "COMMIT");
                return new GoalIntakeReservation(GoalIntakeReservationKind.Replay, existing);
            }

            var now = DateTimeOffset.UtcNow;
            var record = new GoalIntakeRequestRecord(
                requestKey,
                CurrentFingerprintVersion,
                fingerprint,
                GoalIntakeRequestStates.StillCommitting,
                now,
                now,
                GoalId: null,
                FailureCode: null,
                FailureDetail: null,
                NormalizePath(Environment.GetEnvironmentVariable("MCG_ORCHESTRATOR_STDOUT_LOG_PATH")),
                NormalizePath(Environment.GetEnvironmentVariable("MCG_ORCHESTRATOR_STDERR_LOG_PATH")));
            Insert(connection, record);
            Execute(connection, "COMMIT");
            return new GoalIntakeReservation(GoalIntakeReservationKind.Acquired, record);
        }
        catch
        {
            try { Execute(connection, "ROLLBACK"); } catch { }
            throw;
        }
    }

    public GoalIntakeRequestRecord? Get(string requestKey)
    {
        ValidateRequestKey(requestKey);
        if (!File.Exists(_dbPath))
            return null;

        using var connection = StateDbConnectionFactory.Open(_dbPath, StateDbConnectionProfile.QueryOnlyRead);
        return Load(connection, requestKey);
    }

    public GoalIntakeRequestRecord MarkCreated(string requestKey, string fingerprint, string goalId)
    {
        ValidateRequestKey(requestKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(goalId);

        GoalIntakeRequestRecord? result = null;
        if (!StateDbWriteSession.TryExecute(
                _dbPath,
                connection => result = MarkCreated(connection, requestKey, fingerprint, goalId)))
        {
            throw new InvalidOperationException(
                "GOAL_INTAKE_ATOMIC_BIND_REQUIRED reason=no-active-state-write-transaction");
        }

        return result!;
    }

    public GoalIntakeRequestRecord MarkFailed(
        string requestKey,
        string fingerprint,
        string failureCode,
        string failureDetail)
    {
        ValidateRequestKey(requestKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(failureCode);
        var boundedDetail = (failureDetail ?? string.Empty).ReplaceLineEndings(" ").Trim();
        if (boundedDetail.Length > 1000)
            boundedDetail = boundedDetail[..1000];

        using var connection = StateDbConnectionFactory.Open(_dbPath, StateDbConnectionProfile.ReadWrite);
        Execute(connection, "BEGIN IMMEDIATE");
        try
        {
            var current = Load(connection, requestKey)
                ?? throw new InvalidOperationException($"GOAL_INTAKE_REQUEST_NOT_FOUND requestKey={requestKey}");
            EnsureEquivalent(current, fingerprint);
            if (current.State == GoalIntakeRequestStates.Created)
            {
                Execute(connection, "COMMIT");
                return current;
            }
            if (current.State == GoalIntakeRequestStates.Failed)
            {
                Execute(connection, "COMMIT");
                return current;
            }

            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE goal_intake_requests
                SET state = $state,
                    updated_at = $updated_at,
                    failure_code = $failure_code,
                    failure_detail = $failure_detail
                WHERE request_key = $request_key
                  AND fingerprint_version = $fingerprint_version
                  AND fingerprint = $fingerprint
                  AND state = $expected_state
                """;
            command.Parameters.AddWithValue("$state", GoalIntakeRequestStates.Failed);
            command.Parameters.AddWithValue("$updated_at", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$failure_code", failureCode);
            command.Parameters.AddWithValue("$failure_detail", boundedDetail);
            command.Parameters.AddWithValue("$request_key", requestKey);
            command.Parameters.AddWithValue("$fingerprint_version", CurrentFingerprintVersion);
            command.Parameters.AddWithValue("$fingerprint", fingerprint);
            command.Parameters.AddWithValue("$expected_state", GoalIntakeRequestStates.StillCommitting);
            if (command.ExecuteNonQuery() != 1)
                throw InvalidTransition(requestKey, GoalIntakeRequestStates.StillCommitting, GoalIntakeRequestStates.Failed);

            var updated = Load(connection, requestKey)!;
            Execute(connection, "COMMIT");
            return updated;
        }
        catch
        {
            try { Execute(connection, "ROLLBACK"); } catch { }
            throw;
        }
    }

    public static void ValidateRequestKey(string requestKey)
    {
        if (string.IsNullOrWhiteSpace(requestKey))
            throw new ArgumentException("--request-key requires a non-empty value.");
        if (requestKey.Length > MaximumRequestKeyLength)
            throw new ArgumentException($"--request-key must be at most {MaximumRequestKeyLength} characters.");
        if (requestKey.Any(char.IsControl))
            throw new ArgumentException("--request-key cannot contain control characters.");
    }

    private static GoalIntakeRequestRecord MarkCreated(
        SqliteConnection connection,
        string requestKey,
        string fingerprint,
        string goalId)
    {
        var current = Load(connection, requestKey)
            ?? throw new InvalidOperationException($"GOAL_INTAKE_REQUEST_NOT_FOUND requestKey={requestKey}");
        EnsureEquivalent(current, fingerprint);
        if (current.State != GoalIntakeRequestStates.StillCommitting)
            throw InvalidTransition(requestKey, current.State, GoalIntakeRequestStates.Created);

        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE goal_intake_requests
            SET state = $state,
                updated_at = $updated_at,
                goal_id = $goal_id
            WHERE request_key = $request_key
              AND fingerprint_version = $fingerprint_version
              AND fingerprint = $fingerprint
              AND state = $expected_state
              AND goal_id IS NULL
            """;
        command.Parameters.AddWithValue("$state", GoalIntakeRequestStates.Created);
        command.Parameters.AddWithValue("$updated_at", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$goal_id", goalId);
        command.Parameters.AddWithValue("$request_key", requestKey);
        command.Parameters.AddWithValue("$fingerprint_version", CurrentFingerprintVersion);
        command.Parameters.AddWithValue("$fingerprint", fingerprint);
        command.Parameters.AddWithValue("$expected_state", GoalIntakeRequestStates.StillCommitting);
        if (command.ExecuteNonQuery() != 1)
            throw InvalidTransition(requestKey, GoalIntakeRequestStates.StillCommitting, GoalIntakeRequestStates.Created);

        return Load(connection, requestKey)!;
    }

    private static void EnsureEquivalent(GoalIntakeRequestRecord existing, string fingerprint)
    {
        if (existing.FingerprintVersion != CurrentFingerprintVersion ||
            !string.Equals(existing.Fingerprint, fingerprint, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"GOAL_INTAKE_PAYLOAD_CONFLICT requestKey={existing.RequestKey} existingState={existing.State}");
        }
    }

    private static InvalidOperationException InvalidTransition(string requestKey, string from, string to) =>
        new($"GOAL_INTAKE_INVALID_TRANSITION requestKey={requestKey} from={from} to={to}");

    private static void Insert(SqliteConnection connection, GoalIntakeRequestRecord record)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO goal_intake_requests (
                request_key, fingerprint_version, fingerprint, state, created_at, updated_at,
                goal_id, failure_code, failure_detail, stdout_path, stderr_path)
            VALUES (
                $request_key, $fingerprint_version, $fingerprint, $state, $created_at, $updated_at,
                NULL, NULL, NULL, $stdout_path, $stderr_path)
            """;
        command.Parameters.AddWithValue("$request_key", record.RequestKey);
        command.Parameters.AddWithValue("$fingerprint_version", record.FingerprintVersion);
        command.Parameters.AddWithValue("$fingerprint", record.Fingerprint);
        command.Parameters.AddWithValue("$state", record.State);
        command.Parameters.AddWithValue("$created_at", record.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$updated_at", record.UpdatedAt.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$stdout_path", (object?)record.StdoutPath ?? DBNull.Value);
        command.Parameters.AddWithValue("$stderr_path", (object?)record.StderrPath ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    private static GoalIntakeRequestRecord? Load(SqliteConnection connection, string requestKey)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT request_key, fingerprint_version, fingerprint, state, created_at, updated_at,
                   goal_id, failure_code, failure_detail, stdout_path, stderr_path
            FROM goal_intake_requests
            WHERE request_key = $request_key COLLATE BINARY
            """;
        command.Parameters.AddWithValue("$request_key", requestKey);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return null;

        return new GoalIntakeRequestRecord(
            reader.GetString(0),
            reader.GetInt32(1),
            reader.GetString(2),
            reader.GetString(3),
            DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture),
            DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.IsDBNull(9) ? null : reader.GetString(9),
            reader.IsDBNull(10) ? null : reader.GetString(10));
    }

    private static string? NormalizePath(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : Path.GetFullPath(value);

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
