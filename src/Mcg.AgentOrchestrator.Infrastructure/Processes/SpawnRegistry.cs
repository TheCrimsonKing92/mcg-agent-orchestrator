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
    DateTimeOffset? OwnerProcessStartedAt);

internal sealed record SpawnProcessIdentity(int ProcessId, DateTimeOffset StartedAt, string ImagePath);

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
        Register(
            ownerId,
            identity,
            SpawnProcessIdentityReader.TryRead(ownerProcess, out var ownerIdentity) ? ownerIdentity : null);
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
                    owner_process_id, owner_process_started_at
                ) VALUES (
                    $owner_id, $process_id, $process_started_at, $image_path, $registered_at, NULL, NULL,
                    $owner_process_id, $owner_process_started_at
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
                       owner_process_id, owner_process_started_at
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

    public void RecordDiagnostic(long id, string diagnostic)
    {
        WithWriteConnection(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE spawn_registry
                SET last_diagnostic = $diagnostic
                WHERE id = $id
                """;
            cmd.Parameters.AddWithValue("$diagnostic", diagnostic);
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        });
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
                    : ownerStartedAt);
    }

}

internal static class SpawnProcessIdentityReader
{
    public static bool TryRead(Process process, out SpawnProcessIdentity identity)
    {
        try
        {
            identity = new SpawnProcessIdentity(
                process.Id,
                new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero),
                ResolveImagePath(process));
            return !string.IsNullOrWhiteSpace(identity.ImagePath);
        }
        catch
        {
            identity = default!;
            return false;
        }
    }

    public static bool MatchesLiveProcess(SpawnRegistryEntry entry, out Process? process)
    {
        process = null;
        try
        {
            process = Process.GetProcessById(entry.ProcessId);
            if (process.HasExited)
            {
                process.Dispose();
                process = null;
                return false;
            }

            if (!TryRead(process, out var live))
            {
                process.Dispose();
                process = null;
                return false;
            }

            var matches = live.StartedAt == entry.ProcessStartedAt &&
                string.Equals(Path.GetFullPath(live.ImagePath), Path.GetFullPath(entry.ImagePath), StringComparison.OrdinalIgnoreCase);
            if (!matches)
            {
                process.Dispose();
                process = null;
            }

            return matches;
        }
        catch
        {
            process?.Dispose();
            process = null;
            return false;
        }
    }

    public static SpawnOwnerLiveness EvaluateOwner(SpawnRegistryEntry entry, out string evidence)
    {
        if (entry.OwnerProcessId is not { } ownerProcessId || entry.OwnerProcessStartedAt is not { } ownerStartedAt)
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

            evidence = $"owner pid={ownerProcessId} start-time matches recorded={ownerStartedAt:O}";
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

    private static string ResolveImagePath(Process process)
    {
        try
        {
            return process.MainModule?.FileName ?? process.ProcessName;
        }
        catch
        {
            return process.ProcessName;
        }
    }
}

internal enum SpawnOwnerLiveness
{
    Live,
    DeadOrRecycled,
    Unknown
}
