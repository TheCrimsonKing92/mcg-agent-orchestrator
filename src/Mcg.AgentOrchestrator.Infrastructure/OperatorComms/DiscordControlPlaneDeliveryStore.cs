using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.Infrastructure;

public interface IControlPlaneDeliveryStore
{
    Task<ControlPlaneDeliveryMark?> TryGetAsync(string dedupKey, CancellationToken cancellationToken = default);

    Task UpsertAsync(ControlPlaneDeliveryMark mark, CancellationToken cancellationToken = default);

    Task<int> CountPushesAsync(
        ControlPlaneDeliveryChannel channel,
        DateTimeOffset since,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SystemicStormDeliveryState>> ListSystemicStormsAsync(
        CancellationToken cancellationToken = default);
}

public sealed class InMemoryControlPlaneDeliveryStore : IControlPlaneDeliveryStore
{
    private readonly Dictionary<string, ControlPlaneDeliveryMark> _marks = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<ControlPlaneDeliveryMark> Marks => _marks.Values;

    public Task<ControlPlaneDeliveryMark?> TryGetAsync(string dedupKey, CancellationToken cancellationToken = default) =>
        Task.FromResult(_marks.TryGetValue(dedupKey, out var mark) ? mark : null);

    public Task UpsertAsync(ControlPlaneDeliveryMark mark, CancellationToken cancellationToken = default)
    {
        _marks[mark.DedupKey] = mark;
        return Task.CompletedTask;
    }

    public Task<int> CountPushesAsync(
        ControlPlaneDeliveryChannel channel,
        DateTimeOffset since,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(_marks.Values.Count(mark => mark.Channel == channel && mark.FirstDeliveredAt >= since));

    public Task<IReadOnlyList<SystemicStormDeliveryState>> ListSystemicStormsAsync(
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<SystemicStormDeliveryState>>(
            _marks.Values
                .Where(mark => !mark.Resolved)
                .Select(ControlPlaneDeliveryStoreSystemicStorms.TryRead)
                .Where(item => item is not null)
                .Select(item => item!)
                .ToList());
}

public sealed class SqliteControlPlaneDeliveryStore : IControlPlaneDeliveryStore
{
    private readonly string _dbPath;

    public SqliteControlPlaneDeliveryStore(string dbPath)
    {
        _dbPath = dbPath;
        EnsureSchema();
    }

    public static SqliteControlPlaneDeliveryStore ForDirectory(string orchestratorDirectory) =>
        new(Path.Combine(orchestratorDirectory, "control-plane-delivery.db"));

    private string ConnectionString => $"Data Source={_dbPath};Mode=ReadWriteCreate;Pooling=False;";

    public async Task<ControlPlaneDeliveryMark?> TryGetAsync(string dedupKey, CancellationToken cancellationToken = default)
    {
        await using var conn = OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT dedup_key, channel, message_id, first_delivered_at, last_delivered_at, last_reminder_at, content_hash, resolved
            FROM control_plane_delivery_marks
            WHERE dedup_key = $key
            """;
        cmd.Parameters.AddWithValue("$key", dedupKey);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadMark(reader) : null;
    }

    public async Task UpsertAsync(ControlPlaneDeliveryMark mark, CancellationToken cancellationToken = default)
    {
        await using var conn = OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO control_plane_delivery_marks (
                dedup_key, channel, message_id, first_delivered_at, last_delivered_at,
                last_reminder_at, content_hash, resolved
            )
            VALUES ($dedup_key, $channel, $message_id, $first_delivered_at, $last_delivered_at,
                $last_reminder_at, $content_hash, $resolved)
            ON CONFLICT(dedup_key) DO UPDATE SET
                channel = excluded.channel,
                message_id = excluded.message_id,
                last_delivered_at = excluded.last_delivered_at,
                last_reminder_at = excluded.last_reminder_at,
                content_hash = excluded.content_hash,
                resolved = excluded.resolved
            """;
        BindMark(cmd, mark);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<int> CountPushesAsync(
        ControlPlaneDeliveryChannel channel,
        DateTimeOffset since,
        CancellationToken cancellationToken = default)
    {
        await using var conn = OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*)
            FROM control_plane_delivery_marks
            WHERE channel = $channel AND first_delivered_at >= $since
            """;
        cmd.Parameters.AddWithValue("$channel", channel.ToString());
        cmd.Parameters.AddWithValue("$since", since.ToString("O"));
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(cancellationToken));
    }

    public async Task<IReadOnlyList<SystemicStormDeliveryState>> ListSystemicStormsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var conn = OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT dedup_key
            FROM control_plane_delivery_marks
            WHERE dedup_key LIKE 'system:systemic%:storm-window:%' AND resolved = 0
            """;
        var states = new List<SystemicStormDeliveryState>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var mark = new ControlPlaneDeliveryMark(
                reader.GetString(0),
                ControlPlaneDeliveryChannel.Decisions,
                0,
                DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch,
                null,
                string.Empty,
                false);
            if (ControlPlaneDeliveryStoreSystemicStorms.TryRead(mark) is { } state)
                states.Add(state);
        }

        return states;
    }

    private SqliteConnection OpenConnection()
    {
        var conn = new SqliteConnection(ConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA busy_timeout=30000";
        cmd.ExecuteNonQuery();
        return conn;
    }

    private void EnsureSchema()
    {
        var directory = Path.GetDirectoryName(_dbPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS control_plane_delivery_marks (
                dedup_key          TEXT PRIMARY KEY,
                channel            TEXT NOT NULL,
                message_id         INTEGER NOT NULL,
                first_delivered_at TEXT NOT NULL,
                last_delivered_at  TEXT NOT NULL,
                last_reminder_at   TEXT,
                content_hash       TEXT NOT NULL,
                resolved           INTEGER NOT NULL
            )
            """;
        cmd.ExecuteNonQuery();
    }

    private static void BindMark(SqliteCommand cmd, ControlPlaneDeliveryMark mark)
    {
        cmd.Parameters.AddWithValue("$dedup_key", mark.DedupKey);
        cmd.Parameters.AddWithValue("$channel", mark.Channel.ToString());
        cmd.Parameters.AddWithValue("$message_id", (long)mark.MessageId);
        cmd.Parameters.AddWithValue("$first_delivered_at", mark.FirstDeliveredAt.ToString("O"));
        cmd.Parameters.AddWithValue("$last_delivered_at", mark.LastDeliveredAt.ToString("O"));
        cmd.Parameters.AddWithValue("$last_reminder_at", mark.LastReminderAt?.ToString("O") ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$content_hash", mark.ContentHash);
        cmd.Parameters.AddWithValue("$resolved", mark.Resolved ? 1 : 0);
    }

    private static ControlPlaneDeliveryMark ReadMark(SqliteDataReader reader) =>
        new(
            reader.GetString(0),
            Enum.Parse<ControlPlaneDeliveryChannel>(reader.GetString(1), ignoreCase: true),
            (ulong)reader.GetInt64(2),
            DateTimeOffset.Parse(reader.GetString(3)),
            DateTimeOffset.Parse(reader.GetString(4)),
            reader.IsDBNull(5) ? null : DateTimeOffset.Parse(reader.GetString(5)),
            reader.GetString(6),
            reader.GetInt32(7) != 0);

}

internal static class ControlPlaneDeliveryStoreSystemicStorms
{
    public static SystemicStormDeliveryState? TryRead(ControlPlaneDeliveryMark mark)
    {
        var parts = mark.DedupKey.Split(':');
        if (parts.Length != 4 ||
            !parts[0].Equals("system", StringComparison.OrdinalIgnoreCase) ||
            !parts[1].StartsWith("systemic", StringComparison.OrdinalIgnoreCase) ||
            !parts[2].Equals("storm-window", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var kind = parts[1]["systemic".Length..];
        return kind.Length == 0 ? null : new SystemicStormDeliveryState(kind, mark.DedupKey);
    }
}
