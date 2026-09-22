using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal enum OwnedRunRootPurpose
{
    RunAttempt,
    StableSlot
}

public enum OwnedRunRootReleaseOutcome
{
    Succeeded,
    Failed,
    Cancelled,
    Abandoned
}

internal enum OwnedRunRootCleanupState
{
    Pending,
    Released,
    Blocked,
    Removed
}

internal sealed record OwnedRunRootEntry(
    long Id,
    string CanonicalPath,
    OwnedRunRootPurpose Purpose,
    SpawnProcessIdentity Owner,
    string? GoalId,
    string? DispatchId,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastUsedAt,
    DateTimeOffset? ReleasedAt,
    OwnedRunRootReleaseOutcome? ReleaseOutcome,
    OwnedRunRootCleanupState CleanupState,
    int CleanupAttemptCount,
    string? LastCleanupHolder,
    DateTimeOffset? NextAttemptAfter);

internal interface IOwnedRunRootRegistrar
{
    void Register(
        string canonicalPath,
        OwnedRunRootPurpose purpose,
        SpawnProcessIdentity owner,
        string? goalId,
        string? dispatchId,
        DateTimeOffset now);

    void Release(string canonicalPath, OwnedRunRootReleaseOutcome outcome, DateTimeOffset now);

    void MarkRemoved(string canonicalPath, DateTimeOffset now);

    void MarkCleanupFailure(
        string canonicalPath,
        string holderEvidence,
        DateTimeOffset nextAttemptAfter,
        DateTimeOffset now);

    void RecordRetention(string canonicalPath, string evidence, DateTimeOffset now);
}

internal interface IOwnedRunRootStore : IOwnedRunRootRegistrar
{
    IReadOnlyList<OwnedRunRootEntry> ReadBatch(long afterId, int maxCount, DateTimeOffset now);
    IReadOnlySet<string> ReadRegisteredPaths(IReadOnlyList<string> canonicalPaths);
}

internal sealed class OwnedRunRootRegistry(string dbPath) : IOwnedRunRootStore
{
    private readonly string _dbPath = Path.GetFullPath(dbPath);

    public void Register(
        string canonicalPath,
        OwnedRunRootPurpose purpose,
        SpawnProcessIdentity owner,
        string? goalId,
        string? dispatchId,
        DateTimeOffset now)
    {
        canonicalPath = NormalizePath(canonicalPath);
        WithWriteConnection(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO owned_roots (
                    canonical_path, purpose, owner_process_id, owner_process_started_at,
                    owner_process_image_path, goal_id, dispatch_id, created_at, last_used_at,
                    released_at, release_outcome, cleanup_state, cleanup_attempt_count,
                    last_cleanup_holder, next_attempt_after)
                VALUES (
                    $path, $purpose, $pid, $started_at, $image_path, $goal_id, $dispatch_id,
                    $now, $now, NULL, NULL, $pending, 0, NULL, NULL)
                ON CONFLICT(canonical_path) DO UPDATE SET
                    purpose = excluded.purpose,
                    owner_process_id = excluded.owner_process_id,
                    owner_process_started_at = excluded.owner_process_started_at,
                    owner_process_image_path = excluded.owner_process_image_path,
                    goal_id = excluded.goal_id,
                    dispatch_id = excluded.dispatch_id,
                    last_used_at = excluded.last_used_at,
                    released_at = NULL,
                    release_outcome = NULL,
                    cleanup_state = $pending,
                    cleanup_attempt_count = 0,
                    last_cleanup_holder = NULL,
                    next_attempt_after = NULL
                """;
            command.Parameters.AddWithValue("$path", canonicalPath);
            command.Parameters.AddWithValue("$purpose", purpose.ToString());
            command.Parameters.AddWithValue("$pid", owner.ProcessId);
            command.Parameters.AddWithValue("$started_at", Format(owner.StartedAt));
            command.Parameters.AddWithValue("$image_path", owner.ImagePath);
            command.Parameters.AddWithValue("$goal_id", (object?)goalId ?? DBNull.Value);
            command.Parameters.AddWithValue("$dispatch_id", (object?)dispatchId ?? DBNull.Value);
            command.Parameters.AddWithValue("$now", Format(now));
            command.Parameters.AddWithValue("$pending", OwnedRunRootCleanupState.Pending.ToString());
            command.ExecuteNonQuery();
        });
    }

    public void Release(string canonicalPath, OwnedRunRootReleaseOutcome outcome, DateTimeOffset now)
    {
        WithWriteConnection(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE owned_roots
                SET released_at = $now,
                    release_outcome = $outcome,
                    last_used_at = $now,
                    cleanup_state = CASE WHEN purpose = $stable_slot THEN $pending ELSE $released END,
                    next_attempt_after = NULL
                WHERE canonical_path = $path
                  AND cleanup_state <> $removed
                """;
            command.Parameters.AddWithValue("$now", Format(now));
            command.Parameters.AddWithValue("$outcome", outcome.ToString());
            command.Parameters.AddWithValue("$stable_slot", OwnedRunRootPurpose.StableSlot.ToString());
            command.Parameters.AddWithValue("$pending", OwnedRunRootCleanupState.Pending.ToString());
            command.Parameters.AddWithValue("$released", OwnedRunRootCleanupState.Released.ToString());
            command.Parameters.AddWithValue("$removed", OwnedRunRootCleanupState.Removed.ToString());
            command.Parameters.AddWithValue("$path", NormalizePath(canonicalPath));
            command.ExecuteNonQuery();
        });
    }

    public void MarkRemoved(string canonicalPath, DateTimeOffset now)
    {
        WithWriteConnection(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE owned_roots
                SET cleanup_state = $removed,
                    last_used_at = $now,
                    next_attempt_after = NULL,
                    last_cleanup_holder = NULL
                WHERE canonical_path = $path
                """;
            command.Parameters.AddWithValue("$removed", OwnedRunRootCleanupState.Removed.ToString());
            command.Parameters.AddWithValue("$now", Format(now));
            command.Parameters.AddWithValue("$path", NormalizePath(canonicalPath));
            command.ExecuteNonQuery();
        });
    }

    public void MarkCleanupFailure(
        string canonicalPath,
        string holderEvidence,
        DateTimeOffset nextAttemptAfter,
        DateTimeOffset now)
    {
        WithWriteConnection(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE owned_roots
                SET cleanup_state = $blocked,
                    cleanup_attempt_count = cleanup_attempt_count + 1,
                    last_cleanup_holder = $holder,
                    next_attempt_after = $next_attempt_after,
                    last_used_at = $now
                WHERE canonical_path = $path
                  AND cleanup_state <> $removed
                """;
            command.Parameters.AddWithValue("$blocked", OwnedRunRootCleanupState.Blocked.ToString());
            command.Parameters.AddWithValue("$holder", holderEvidence);
            command.Parameters.AddWithValue("$next_attempt_after", Format(nextAttemptAfter));
            command.Parameters.AddWithValue("$now", Format(now));
            command.Parameters.AddWithValue("$removed", OwnedRunRootCleanupState.Removed.ToString());
            command.Parameters.AddWithValue("$path", NormalizePath(canonicalPath));
            command.ExecuteNonQuery();
        });
    }

    public void RecordRetention(string canonicalPath, string evidence, DateTimeOffset now)
    {
        WithWriteConnection(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE owned_roots
                SET last_cleanup_holder = $evidence,
                    last_used_at = $now
                WHERE canonical_path = $path
                  AND cleanup_state <> $removed
                """;
            command.Parameters.AddWithValue("$evidence", evidence);
            command.Parameters.AddWithValue("$now", Format(now));
            command.Parameters.AddWithValue("$removed", OwnedRunRootCleanupState.Removed.ToString());
            command.Parameters.AddWithValue("$path", NormalizePath(canonicalPath));
            command.ExecuteNonQuery();
        });
    }

    public IReadOnlyList<OwnedRunRootEntry> ReadBatch(long afterId, int maxCount, DateTimeOffset now)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(afterId);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxCount, 1);
        using var connection = OpenReadConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, canonical_path, purpose, owner_process_id, owner_process_started_at,
                   owner_process_image_path, goal_id, dispatch_id, created_at, last_used_at,
                   released_at, release_outcome, cleanup_state, cleanup_attempt_count,
                   last_cleanup_holder, next_attempt_after
            FROM owned_roots
            WHERE cleanup_state <> $removed
              AND (id > $after_id OR cleanup_state = $blocked)
              AND (next_attempt_after IS NULL OR next_attempt_after <= $now)
            ORDER BY CASE WHEN cleanup_state = $blocked THEN 0 ELSE 1 END,
                     CASE WHEN cleanup_state = $blocked THEN next_attempt_after END,
                     id
            LIMIT $max_count
            """;
        command.Parameters.AddWithValue("$after_id", afterId);
        command.Parameters.AddWithValue("$blocked", OwnedRunRootCleanupState.Blocked.ToString());
        command.Parameters.AddWithValue("$removed", OwnedRunRootCleanupState.Removed.ToString());
        command.Parameters.AddWithValue("$now", Format(now));
        command.Parameters.AddWithValue("$max_count", maxCount);
        using var reader = command.ExecuteReader();
        var entries = new List<OwnedRunRootEntry>();
        while (reader.Read())
            entries.Add(ReadEntry(reader));
        return entries;
    }

    public IReadOnlySet<string> ReadRegisteredPaths(IReadOnlyList<string> canonicalPaths)
    {
        ArgumentNullException.ThrowIfNull(canonicalPaths);
        var paths = new HashSet<string>(PathComparer);
        if (canonicalPaths.Count == 0)
            return paths;

        using var connection = OpenReadConnection();
        using var command = connection.CreateCommand();
        var parameterNames = new string[canonicalPaths.Count];
        for (var index = 0; index < canonicalPaths.Count; index++)
        {
            parameterNames[index] = $"$path_{index}";
            command.Parameters.AddWithValue(parameterNames[index], NormalizePath(canonicalPaths[index]));
        }
        command.CommandText = $"SELECT canonical_path FROM owned_roots WHERE canonical_path IN ({string.Join(", ", parameterNames)})";
        using var reader = command.ExecuteReader();
        while (reader.Read())
            paths.Add(reader.GetString(0));
        return paths;
    }

    private void WithWriteConnection(Action<SqliteConnection> action)
    {
        if (StateDbWriteSession.TryExecute(_dbPath, action))
            return;

        using var connection = StateDbConnectionFactory.Open(_dbPath, StateDbConnectionProfile.ReadWrite);
        action(connection);
    }

    private SqliteConnection OpenReadConnection() =>
        StateDbConnectionFactory.Open(_dbPath, StateDbConnectionProfile.QueryOnlyRead);

    private static OwnedRunRootEntry ReadEntry(SqliteDataReader reader) =>
        new(
            reader.GetInt64(0),
            reader.GetString(1),
            ParseEnum<OwnedRunRootPurpose>(reader.GetString(2)),
            new SpawnProcessIdentity(reader.GetInt32(3), ParseDate(reader.GetString(4)), reader.GetString(5)),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            ParseDate(reader.GetString(8)),
            ParseDate(reader.GetString(9)),
            reader.IsDBNull(10) ? null : ParseDate(reader.GetString(10)),
            reader.IsDBNull(11) ? null : ParseEnum<OwnedRunRootReleaseOutcome>(reader.GetString(11)),
            ParseEnum<OwnedRunRootCleanupState>(reader.GetString(12)),
            reader.GetInt32(13),
            reader.IsDBNull(14) ? null : reader.GetString(14),
            reader.IsDBNull(15) ? null : ParseDate(reader.GetString(15)));

    private static T ParseEnum<T>(string value) where T : struct, Enum =>
        Enum.TryParse<T>(value, ignoreCase: false, out var parsed)
            ? parsed
            : throw new InvalidDataException($"Unknown {typeof(T).Name} value '{value}'.");

    private static DateTimeOffset ParseDate(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static string Format(DateTimeOffset value) =>
        value.ToString("O", CultureInfo.InvariantCulture);

    private static string NormalizePath(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
}
