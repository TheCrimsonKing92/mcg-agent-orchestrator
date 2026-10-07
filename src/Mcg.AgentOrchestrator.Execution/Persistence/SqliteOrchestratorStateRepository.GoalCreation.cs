using System.Text;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed partial class SqliteOrchestratorStateRepository
{
    public async Task<T> TransactGoalCreationWithOutboxAsync<T>(
        GoalCreationLoadScope scope,
        Func<AgentOrchestratorKernel, CancellationToken, Task<(
            bool ShouldSave,
            T Result,
            IReadOnlyList<OrchestratorStateOutboxMessage> OutboxMessages)>> transaction,
        CancellationToken cancellationToken = default)
    {
        var write = await BeginWriteAsync(
            ResolveOperationTag(nameof(TransactGoalCreationWithOutboxAsync)), cancellationToken);
        await using var conn = write.Connection;
        var telemetry = write.Telemetry;
        T result = default!;
        StateDbCommitBeforeRethrowException? commitBeforeRethrow = null;
        try
        {
            var goalIds = await ResolveGoalCreationIdsAsync(conn, scope, cancellationToken);
            var kernel = await LoadFromConnectionAsync(conn, goalIds, cancellationToken);
            // Compare exported snapshots, rather than stored JSON formatting or load-time repairs.
            var baseline = kernel.ExportSnapshot().Goals.ToDictionary(
                goal => goal.Id, goal => JsonSerializer.Serialize(goal, SerializerOptions), StringComparer.Ordinal);
            var shouldSave = false;
            IReadOnlyList<OrchestratorStateOutboxMessage> outboxMessages = [];
            try
            {
                using (StateDbWriteSession.Enter(_dbPath, conn))
                {
                    (shouldSave, result, outboxMessages) = await transaction(kernel, cancellationToken);
                }
            }
            catch (StateDbCommitBeforeRethrowException ex)
            {
                shouldSave = true;
                commitBeforeRethrow = ex;
            }
            if (shouldSave)
                await WriteGoalCreationChangesAsync(conn, kernel, baseline, telemetry, cancellationToken);
            foreach (var message in outboxMessages)
                await InsertOutboxMessageAsync(conn, message, cancellationToken);
            _beforeOutboxCommit?.Invoke();
            await RunNonQueryAsync(conn, "COMMIT", cancellationToken);
            telemetry.Emit("commit");
        }
        catch (Exception ex)
        {
            try { await RunNonQueryAsync(conn, "ROLLBACK", cancellationToken); } catch { }
            telemetry.Emit("rollback", ex);
            throw;
        }
        if (commitBeforeRethrow is not null)
            throw commitBeforeRethrow.CreatePostCommitException();
        return result;
    }

    private static async Task<IReadOnlyCollection<GoalId>> ResolveGoalCreationIdsAsync(
        SqliteConnection conn, GoalCreationLoadScope scope, CancellationToken cancellationToken)
    {
        var goalIds = scope.GoalIds.ToHashSet();
        await AddDependsOnClosureAsync(conn, scope.DependsOnClosureRoots, goalIds, cancellationToken);
        if (scope.BacklogItemIds.Count == 0)
            return goalIds;
        var itemIds = scope.BacklogItemIds.Distinct(StringComparer.Ordinal).ToArray();
        var parameterNames = itemIds.Select((_, index) => $"$item{index}").ToArray();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT owner_goal_id FROM source_backlog_claims WHERE backlog_item_id IN ({string.Join(", ", parameterNames)})
            UNION
            SELECT id FROM goals WHERE source_backlog_item_id IN ({string.Join(", ", parameterNames)})
            """;
        for (var index = 0; index < itemIds.Length; index++)
            cmd.Parameters.AddWithValue(parameterNames[index], itemIds[index]);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            goalIds.Add(new GoalId(reader.GetString(0)));
        return goalIds;
    }

    private static async Task AddDependsOnClosureAsync(
        SqliteConnection conn,
        IReadOnlyCollection<GoalId> roots,
        HashSet<GoalId> goalIds,
        CancellationToken cancellationToken)
    {
        var visited = roots.ToHashSet();
        goalIds.UnionWith(visited);
        var frontier = visited.ToArray();
        while (frontier.Length > 0)
        {
            var next = new List<GoalId>();
            foreach (var batch in frontier.Chunk(500))
            {
                var parameters = batch.Select((_, index) => $"$goal{index}").ToArray();
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = $"SELECT snapshot_json FROM goals WHERE id IN ({string.Join(", ", parameters)})";
                for (var index = 0; index < batch.Length; index++)
                    cmd.Parameters.AddWithValue(parameters[index], batch[index].Value);
                await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    try
                    {
                        using var document = JsonDocument.Parse(reader.GetString(0));
                        if (document.RootElement.ValueKind != JsonValueKind.Object ||
                            !document.RootElement.TryGetProperty(nameof(GoalSnapshot.DependsOn), out var dependencies) ||
                            dependencies.ValueKind != JsonValueKind.Array)
                            continue;
                        foreach (var dependency in dependencies.EnumerateArray())
                        {
                            if (dependency.ValueKind != JsonValueKind.String)
                                continue;
                            var id = new GoalId(dependency.GetString()!);
                            if (visited.Add(id))
                            {
                                goalIds.Add(id);
                                next.Add(id);
                            }
                        }
                    }
                    catch (JsonException)
                    {
                        // The scoped loader retains ownership of malformed-row quarantine.
                    }
                }
            }
            frontier = next.ToArray();
        }
    }

    private static async Task WriteGoalCreationChangesAsync(
        SqliteConnection conn,
        AgentOrchestratorKernel kernel,
        IReadOnlyDictionary<string, string> baseline,
        SqliteWriteTelemetry.WriteTelemetryScope telemetry,
        CancellationToken cancellationToken)
    {
        var snapshot = kernel.ExportSnapshot();
        var changedGoalIds = new HashSet<string>(StringComparer.Ordinal);
        var updatedAt = DateTimeOffset.UtcNow.ToString("O");
        foreach (var goal in snapshot.Goals)
        {
            var json = JsonSerializer.Serialize(goal, SerializerOptions);
            if (baseline.TryGetValue(goal.Id, out var original) && string.Equals(json, original, StringComparison.Ordinal))
                continue;
            changedGoalIds.Add(goal.Id);
            var goalWrite = await UpsertGoalRowAsync(
                conn, goal, updatedAt, versionSql: "goals.version + 1", cancellationToken, skipUnchanged: true);
            telemetry.AddWrite(goalWrite);
            if (goalWrite.RowsWritten > 0)
                telemetry.AddWrite(await UpsertModelFitHistoryRowsAsync(conn, goal, cancellationToken));
        }
        foreach (var request in snapshot.HumanInputRequests.Where(request => changedGoalIds.Contains(request.GoalId)))
        {
            var json = JsonSerializer.Serialize(request, SerializerOptions);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO human_input_requests (id, goal_id, snapshot_json)
                VALUES ($id, $goal_id, $json)
                ON CONFLICT(id) DO UPDATE SET
                    goal_id       = excluded.goal_id,
                    snapshot_json = excluded.snapshot_json
                """;
            cmd.Parameters.AddWithValue("$id", request.Id);
            cmd.Parameters.AddWithValue("$goal_id", request.GoalId);
            cmd.Parameters.AddWithValue("$json", json);
            telemetry.AddWrite(await cmd.ExecuteNonQueryAsync(cancellationToken), Encoding.UTF8.GetByteCount(json));
        }
    }
}
