using System.Text;
using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.Infrastructure;

public static class GoalSnapshotArchiveRecompression
{
    // The caller owns the connection and the write transaction for this entire batch.
    public static async Task<GoalSnapshotArchiveRecompressionResult> RecompressAsync(
        SqliteConnection connection, long maxSourceBytes, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxSourceBytes, 1L);
        var ids = new List<string>();
        await using (var select = connection.CreateCommand())
        {
            select.CommandText = "SELECT goal_id FROM goal_snapshot_archive WHERE typeof(snapshot_json) = 'text' ORDER BY goal_id";
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                ids.Add(reader.GetString(0));
        }

        var rows = 0;
        long sourceBytes = 0;
        long storedBytes = 0;
        foreach (var id in ids)
        {
            if (sourceBytes >= maxSourceBytes)
                break;
            cancellationToken.ThrowIfCancellationRequested();
            await using var read = connection.CreateCommand();
            read.CommandText = "SELECT snapshot_json FROM goal_snapshot_archive WHERE goal_id = $id AND typeof(snapshot_json) = 'text'";
            read.Parameters.AddWithValue("$id", id);
            if (await read.ExecuteScalarAsync(cancellationToken) is not string json)
                continue;

            var encoded = GoalSnapshotArchiveCodec.Encode(json);
            if (!string.Equals(GoalSnapshotArchiveCodec.Decode(encoded), json, StringComparison.Ordinal))
                throw new InvalidOperationException("goal_snapshot_archive recompression did not preserve the snapshot.");
            await using var update = connection.CreateCommand();
            update.CommandText = "UPDATE goal_snapshot_archive SET snapshot_json = $blob WHERE goal_id = $id AND typeof(snapshot_json) = 'text'";
            update.Parameters.AddWithValue("$id", id);
            update.Parameters.Add("$blob", SqliteType.Blob).Value = encoded;
            if (await update.ExecuteNonQueryAsync(cancellationToken) == 0)
                continue;
            rows++;
            sourceBytes += Encoding.UTF8.GetByteCount(json);
            storedBytes += encoded.Length;
        }
        return new(rows, sourceBytes, storedBytes);
    }
}
