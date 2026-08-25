using System.Diagnostics;
using System.ComponentModel;
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed record SpawnRegistryEntry(
    long Id,
    string OwnerId,
    int ProcessId,
    DateTimeOffset ProcessStartedAt,
    string ImagePath,
    DateTimeOffset RegisteredAt,
    DateTimeOffset? ReleasedAt,
    string? LastDiagnostic,
    int? OwnerProcessId,
    DateTimeOffset? OwnerProcessStartedAt,
    string? OwnerProcessImagePath,
    SpawnRegistryLifecycle Lifecycle);

internal enum SpawnRegistryLifecycle
{
    Owned,
    GracefullyDetached
}

internal sealed record SpawnProcessIdentity(int ProcessId, DateTimeOffset StartedAt, string ImagePath);

internal sealed record SpawnProcessIdentityReadResult(
    SpawnProcessIdentity? Identity,
    int Attempts,
    string Evidence)
{
    public bool Succeeded => Identity is not null;
}

internal sealed class SpawnRegistry
{
    private readonly string _dbPath;

    public SpawnRegistry(string dbPath)
    {
        _dbPath = dbPath;
    }

    public void Register(string ownerId, SpawnProcessIdentity identity)
    {
        using var ownerProcess = Process.GetCurrentProcess();
        var ownerRead = SpawnProcessIdentityReader.ReadForRegistration(ownerProcess);
        if (!ownerRead.Succeeded)
        {
            throw new InvalidOperationException(
                $"Cannot durably register process {identity.ProcessId}: owner identity unavailable ({ownerRead.Evidence}).");
        }

        Register(ownerId, identity, ownerRead.Identity!);
    }

    internal void Register(
        string ownerId,
        SpawnProcessIdentity identity,
        SpawnProcessIdentity? ownerIdentity)
    {
        WithWriteConnection(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO spawn_registry (
                    owner_id, process_id, process_started_at, image_path, registered_at, released_at, last_diagnostic,
                    owner_process_id, owner_process_started_at, owner_process_image_path
                ) VALUES (
                    $owner_id, $process_id, $process_started_at, $image_path, $registered_at, NULL, NULL,
                    $owner_process_id, $owner_process_started_at, $owner_process_image_path
                )
                """;
            cmd.Parameters.AddWithValue("$owner_id", ownerId);
            cmd.Parameters.AddWithValue("$process_id", identity.ProcessId);
            cmd.Parameters.AddWithValue("$process_started_at", identity.StartedAt.ToString("O"));
            cmd.Parameters.AddWithValue("$image_path", identity.ImagePath);
            cmd.Parameters.AddWithValue("$registered_at", DateTimeOffset.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("$owner_process_id", (object?)ownerIdentity?.ProcessId ?? DBNull.Value);
            cmd.Parameters.AddWithValue(
                "$owner_process_started_at",
                (object?)ownerIdentity?.StartedAt.ToString("O", CultureInfo.InvariantCulture) ?? DBNull.Value);
            cmd.Parameters.AddWithValue(
                "$owner_process_image_path",
                (object?)ownerIdentity?.ImagePath ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        });
    }

    public IReadOnlyList<SpawnRegistryEntry> ListActive()
    {
        if (!File.Exists(_dbPath))
            return [];

        try
        {
            using var conn = OpenReadConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT id, owner_id, process_id, process_started_at, image_path, registered_at, released_at, last_diagnostic,
                       owner_process_id, owner_process_started_at, owner_process_image_path, lifecycle
                FROM spawn_registry
                WHERE released_at IS NULL
                ORDER BY registered_at ASC
                """;
            var result = new List<SpawnRegistryEntry>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                result.Add(ReadEntry(reader));
            }

            return result;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 1)
        {
            return [];
        }
    }

    public void MarkReleased(int processId, string diagnostic)
    {
        WithWriteConnection(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE spawn_registry
                SET released_at = COALESCE(released_at, $released_at),
                    last_diagnostic = $diagnostic
                WHERE process_id = $process_id
                  AND released_at IS NULL
                """;
            cmd.Parameters.AddWithValue("$released_at", DateTimeOffset.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("$diagnostic", diagnostic);
            cmd.Parameters.AddWithValue("$process_id", processId);
            cmd.ExecuteNonQuery();
        });
    }

    public void MarkReleasedEntry(long id, string diagnostic)
    {
        WithWriteConnection(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE spawn_registry
                SET released_at = COALESCE(released_at, $released_at),
                    last_diagnostic = $diagnostic
                WHERE id = $id
                  AND released_at IS NULL
                """;
            cmd.Parameters.AddWithValue("$released_at", DateTimeOffset.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("$diagnostic", diagnostic);
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        });
    }

    public bool TryMarkReleasedEntry(long id, string? expectedDiagnostic, string diagnostic)
    {
        var updated = false;
        WithWriteConnection(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE spawn_registry
                SET released_at = $released_at,
                    last_diagnostic = $diagnostic
                WHERE id = $id
                  AND released_at IS NULL
                  AND (($expected_diagnostic IS NULL AND last_diagnostic IS NULL) OR last_diagnostic = $expected_diagnostic)
                """;
            cmd.Parameters.AddWithValue("$released_at", DateTimeOffset.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("$diagnostic", diagnostic);
            cmd.Parameters.AddWithValue("$expected_diagnostic", (object?)expectedDiagnostic ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$id", id);
            updated = cmd.ExecuteNonQuery() == 1;
        });
        return updated;
    }

    public void RecordDiagnostic(long id, string diagnostic)
    {
        WithWriteConnection(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE spawn_registry
                SET last_diagnostic = $diagnostic
                WHERE id = $id
                  AND released_at IS NULL
                """;
            cmd.Parameters.AddWithValue("$diagnostic", diagnostic);
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        });
    }

    public bool TryRecordDiagnostic(long id, string? expectedDiagnostic, string diagnostic)
    {
        var updated = false;
        WithWriteConnection(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE spawn_registry
                SET last_diagnostic = $diagnostic
                WHERE id = $id
                  AND released_at IS NULL
                  AND (($expected_diagnostic IS NULL AND last_diagnostic IS NULL) OR last_diagnostic = $expected_diagnostic)
                """;
            cmd.Parameters.AddWithValue("$diagnostic", diagnostic);
            cmd.Parameters.AddWithValue("$expected_diagnostic", (object?)expectedDiagnostic ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$id", id);
            updated = cmd.ExecuteNonQuery() == 1;
        });
        return updated;
    }

    public bool WasGracefullyDetached(string ownerId, int processId, DateTimeOffset processRecordedAt)
    {
        if (!File.Exists(_dbPath))
            return false;

        using var conn = OpenReadConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT lifecycle
            FROM spawn_registry
            WHERE owner_id = $owner_id
              AND process_id = $process_id
              AND registered_at <= $process_recorded_at
            ORDER BY registered_at DESC, id DESC
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$owner_id", ownerId);
        cmd.Parameters.AddWithValue("$process_id", processId);
        cmd.Parameters.AddWithValue("$process_recorded_at", processRecordedAt.ToString("O", CultureInfo.InvariantCulture));
        return string.Equals(
            cmd.ExecuteScalar() as string,
            SpawnRegistryLifecycle.GracefullyDetached.ToString(),
            StringComparison.Ordinal);
    }

    public bool TryMarkGracefullyDetached(SpawnRegistryEntry entry, string diagnostic)
    {
        var updated = false;
        WithWriteConnection(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE spawn_registry
                SET lifecycle = $detached,
                    last_diagnostic = $diagnostic
                WHERE id = $id
                  AND process_id = $process_id
                  AND process_started_at = $process_started_at
                  AND image_path = $image_path
                  AND released_at IS NULL
                  AND lifecycle = $owned
                """;
            cmd.Parameters.AddWithValue("$detached", SpawnRegistryLifecycle.GracefullyDetached.ToString());
            cmd.Parameters.AddWithValue("$diagnostic", diagnostic);
            cmd.Parameters.AddWithValue("$id", entry.Id);
            cmd.Parameters.AddWithValue("$process_id", entry.ProcessId);
            cmd.Parameters.AddWithValue("$process_started_at", entry.ProcessStartedAt.ToString("O"));
            cmd.Parameters.AddWithValue("$image_path", entry.ImagePath);
            cmd.Parameters.AddWithValue("$owned", SpawnRegistryLifecycle.Owned.ToString());
            updated = cmd.ExecuteNonQuery() == 1;
        });
        return updated;
    }

    private SqliteConnection OpenConnection() =>
        StateDbConnectionFactory.Open(_dbPath, StateDbConnectionProfile.ReadWrite);

    private void WithWriteConnection(Action<SqliteConnection> action)
    {
        if (StateDbWriteSession.TryExecute(_dbPath, action))
            return;

        using var connection = OpenConnection();
        action(connection);
    }

    private SqliteConnection OpenReadConnection() =>
        StateDbConnectionFactory.Open(_dbPath, StateDbConnectionProfile.QueryOnlyRead);

    private static SpawnRegistryEntry ReadEntry(SqliteDataReader reader)
    {
        return new SpawnRegistryEntry(
            reader.GetInt64(0),
            reader.GetString(1),
            reader.GetInt32(2),
            DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            reader.GetString(4),
            DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            reader.IsDBNull(6)
                ? null
                : DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetInt32(8),
            reader.IsDBNull(9) || !DateTimeOffset.TryParse(
                reader.GetString(9),
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var ownerStartedAt)
                    ? null
                    : ownerStartedAt,
            reader.IsDBNull(10) ? null : reader.GetString(10),
            Enum.TryParse<SpawnRegistryLifecycle>(reader.GetString(11), ignoreCase: false, out var lifecycle)
                ? lifecycle
                : throw new InvalidDataException($"Unknown spawn registry lifecycle '{reader.GetString(11)}'."));
    }

}

internal static class SpawnProcessIdentityReader
{
    private const int RegistrationReadAttempts = 10;
    private const int RegistrationReadDelayMilliseconds = 25;

    public static bool TryRead(Process process, out SpawnProcessIdentity identity)
    {
        try
        {
            var imagePath = ResolveImagePath(process);
            if (string.IsNullOrWhiteSpace(imagePath))
            {
                identity = default!;
                return false;
            }

            identity = new SpawnProcessIdentity(
                process.Id,
                new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero),
                imagePath);
            return true;
        }
        catch
        {
            identity = default!;
            return false;
        }
    }

    public static bool TryReadForRegistration(Process process, out SpawnProcessIdentity identity)
    {
        var result = ReadForRegistration(process);
        identity = result.Identity!;
        return result.Succeeded;
    }

    internal static SpawnProcessIdentityReadResult ReadForRegistration(Process process)
    {
        return ReadForRegistration(
            process,
            static candidate => TryRead(candidate, out var identity) ? identity : null,
            static delayMilliseconds => Thread.Sleep(delayMilliseconds),
            RegistrationReadAttempts,
            RegistrationReadDelayMilliseconds);
    }

    internal static SpawnProcessIdentityReadResult ReadForRegistration(
        Process process,
        Func<Process, SpawnProcessIdentity?> readIdentity,
        Action<int> delay,
        int maxAttempts,
        int delayMilliseconds)
    {
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(readIdentity);
        ArgumentNullException.ThrowIfNull(delay);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAttempts, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(delayMilliseconds);

        var reason = "identity-unavailable";
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                var identity = readIdentity(process);
                if (identity is not null)
                {
                    return new SpawnProcessIdentityReadResult(
                        identity,
                        attempt,
                        $"status=read attempts={attempt.ToString(CultureInfo.InvariantCulture)}");
                }

                reason = "identity-unavailable";
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
            {
                reason = $"reader-{ex.GetType().Name}";
            }

            try
            {
                if (process.HasExited)
                {
                    return new SpawnProcessIdentityReadResult(
                        null,
                        attempt,
                        $"status=unavailable attempts={attempt.ToString(CultureInfo.InvariantCulture)} reason=process-exited");
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
            {
                return new SpawnProcessIdentityReadResult(
                    null,
                    attempt,
                    $"status=unavailable attempts={attempt.ToString(CultureInfo.InvariantCulture)} reason=state-{ex.GetType().Name}");
            }

            if (attempt < maxAttempts)
            {
                delay(delayMilliseconds);
            }
        }

        return new SpawnProcessIdentityReadResult(
            null,
            maxAttempts,
            $"status=unavailable attempts={maxAttempts.ToString(CultureInfo.InvariantCulture)} reason={reason}");
    }

    public static SpawnTrackedProcessStatus EvaluateTrackedProcess(
        SpawnRegistryEntry entry,
        out Process? process,
        out string evidence)
    {
        process = null;
        try
        {
            process = Process.GetProcessById(entry.ProcessId);
            if (process.HasExited)
            {
                evidence = $"victim pid={entry.ProcessId} has exited";
                process.Dispose();
                process = null;
                return SpawnTrackedProcessStatus.DeadOrRecycled;
            }

            var recorded = new SpawnProcessIdentity(entry.ProcessId, entry.ProcessStartedAt, entry.ImagePath);
            var live = TryRead(process, out var identity) ? identity : null;
            var status = EvaluateRecordedIdentity(recorded, live, out evidence);
            if (status != SpawnTrackedProcessStatus.LiveMatch)
            {
                process.Dispose();
                process = null;
            }

            return status;
        }
        catch (ArgumentException)
        {
            evidence = $"victim pid={entry.ProcessId} does not exist";
            process?.Dispose();
            process = null;
            return SpawnTrackedProcessStatus.DeadOrRecycled;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            evidence = $"victim pid={entry.ProcessId} could not be verified ({ex.GetType().Name})";
            process?.Dispose();
            process = null;
            return SpawnTrackedProcessStatus.Unknown;
        }
    }

    public static SpawnOwnerLiveness EvaluateOwner(SpawnRegistryEntry entry, out string evidence)
    {
        if (entry.OwnerProcessId is not { } ownerProcessId ||
            entry.OwnerProcessStartedAt is not { } ownerStartedAt ||
            string.IsNullOrWhiteSpace(entry.OwnerProcessImagePath))
        {
            evidence = "owner identity missing or malformed";
            return SpawnOwnerLiveness.Unknown;
        }

        Process? owner = null;
        try
        {
            owner = Process.GetProcessById(ownerProcessId);
            if (owner.HasExited)
            {
                evidence = $"owner pid={ownerProcessId} has exited";
                return SpawnOwnerLiveness.DeadOrRecycled;
            }

            if (!TryRead(owner, out var liveOwner))
            {
                evidence = $"owner pid={ownerProcessId} identity could not be read";
                return SpawnOwnerLiveness.Unknown;
            }

            if (liveOwner.StartedAt != ownerStartedAt)
            {
                evidence = $"owner pid={ownerProcessId} start-time mismatch recorded={ownerStartedAt:O} observed={liveOwner.StartedAt:O}";
                return SpawnOwnerLiveness.DeadOrRecycled;
            }

            if (!TryNormalizePath(liveOwner.ImagePath, out var observedImagePath) ||
                !TryNormalizePath(entry.OwnerProcessImagePath, out var recordedImagePath))
            {
                evidence = $"owner pid={ownerProcessId} image identity could not be normalized";
                return SpawnOwnerLiveness.Unknown;
            }

            if (!string.Equals(observedImagePath, recordedImagePath, StringComparison.OrdinalIgnoreCase))
            {
                evidence = $"owner pid={ownerProcessId} image mismatch recorded={entry.OwnerProcessImagePath} observed={liveOwner.ImagePath}";
                return SpawnOwnerLiveness.Unknown;
            }

            evidence = $"owner pid={ownerProcessId} identity matches started_at={ownerStartedAt:O} image={liveOwner.ImagePath}";
            return SpawnOwnerLiveness.Live;
        }
        catch (ArgumentException)
        {
            evidence = $"owner pid={ownerProcessId} does not exist";
            return SpawnOwnerLiveness.DeadOrRecycled;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            evidence = $"owner pid={ownerProcessId} could not be verified ({ex.GetType().Name})";
            return SpawnOwnerLiveness.Unknown;
        }
        finally
        {
            owner?.Dispose();
        }
    }

    internal static SpawnTrackedProcessStatus EvaluateRecordedIdentity(
        SpawnProcessIdentity recorded,
        SpawnProcessIdentity? current,
        out string evidence)
    {
        if (current is null)
        {
            evidence = $"victim pid={recorded.ProcessId} identity could not be read";
            return SpawnTrackedProcessStatus.Unknown;
        }

        if (current.StartedAt != recorded.StartedAt)
        {
            evidence = $"victim pid={recorded.ProcessId} start-time mismatch recorded={recorded.StartedAt:O} observed={current.StartedAt:O}";
            return SpawnTrackedProcessStatus.DeadOrRecycled;
        }

        if (!TryNormalizePath(current.ImagePath, out var observedImagePath) ||
            !TryNormalizePath(recorded.ImagePath, out var recordedImagePath))
        {
            evidence = $"victim pid={recorded.ProcessId} image identity could not be normalized";
            return SpawnTrackedProcessStatus.Unknown;
        }

        if (!string.Equals(observedImagePath, recordedImagePath, StringComparison.OrdinalIgnoreCase))
        {
            evidence = $"victim pid={recorded.ProcessId} image mismatch recorded={recorded.ImagePath} observed={current.ImagePath}";
            return SpawnTrackedProcessStatus.Unknown;
        }

        evidence = $"victim pid={recorded.ProcessId} identity matches started_at={recorded.StartedAt:O} image={current.ImagePath}";
        return SpawnTrackedProcessStatus.LiveMatch;
    }

    private static string? ResolveImagePath(Process process)
    {
        try
        {
            return process.MainModule?.FileName;
        }
        catch
        {
            return null;
        }
    }

    private static bool TryNormalizePath(string path, out string normalized)
    {
        try
        {
            normalized = Path.GetFullPath(path);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            normalized = string.Empty;
            return false;
        }
    }
}

internal enum SpawnOwnerLiveness
{
    Live,
    DeadOrRecycled,
    Unknown
}

internal enum SpawnTrackedProcessStatus
{
    LiveMatch,
    DeadOrRecycled,
    Unknown
}
