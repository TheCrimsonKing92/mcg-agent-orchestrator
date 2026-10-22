using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed partial class SqliteOperatorIntentStore
{
    // One SELECT fixes membership, including recoverable claims, before any application or reload.
    public async Task<IReadOnlyList<OperatorIntentRecord>> CaptureGoalBatchAsync(
        string claimOwner, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(claimOwner);
        await using var conn = OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            {SelectColumns}
            WHERE goal_id != $workspace
              AND (status = $pending OR (status = $claimed AND claim_owner = $owner))
            ORDER BY created_at, id
            """;
        cmd.Parameters.AddWithValue("$workspace", OperatorIntentScopes.Workspace);
        cmd.Parameters.AddWithValue("$pending", OperatorIntentStatus.Pending.ToString());
        cmd.Parameters.AddWithValue("$claimed", OperatorIntentStatus.Claimed.ToString());
        cmd.Parameters.AddWithValue("$owner", claimOwner);
        var batch = new List<OperatorIntentRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            batch.Add(ReadIntent(reader));
        return batch;
    }

    public Task<OperatorIntentRecord?> ClaimNextInBatchAsync(
        string goalId, string claimOwner, IReadOnlySet<string> intentIds,
        bool pendingOnly = false, string? verb = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(intentIds);
        return ClaimNextCoreAsync(goalId, verb, claimOwner, cancellationToken, pendingOnly, intentIds);
    }
}
