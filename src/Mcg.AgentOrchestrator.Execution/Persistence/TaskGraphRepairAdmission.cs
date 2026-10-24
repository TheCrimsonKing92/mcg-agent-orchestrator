using Mcg.AgentOrchestrator.Core;
using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed partial class SqliteOperatorIntentStore
{
    public T SerializeGoalAdmission<T>(string goalId, Func<T> action) =>
        TaskIntentAdmission.SerializeGoal(_dbPath, goalId, action);

    public IReadOnlySet<string> ListAdmittedTaskIds(Goal goal)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT task_id,dispatched_at FROM task_intent_launches WHERE goal_id=$goal";
        cmd.Parameters.AddWithValue("$goal", goal.Id.Value);
        var currentAttempts = goal.Tasks.Where(task => task.LastDispatch is not null)
            .ToDictionary(task => task.Id.Value, task => task.LastDispatch!.DispatchedAt, StringComparer.Ordinal);
        using var reader = cmd.ExecuteReader();
        var result = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read())
        {
            var taskId = reader.GetString(0);
            // Launch history is replay evidence, not perpetual ownership. Retry clears
            // LastDispatch, so only the current attempt may fence graph repair.
            // Legacy rows lack an attempt identity: conservatively protect an existing
            // dispatch, but never resurrect ownership after its retry reset.
            if (currentAttempts.TryGetValue(taskId, out var at) &&
                (reader.IsDBNull(1) || DateTimeOffset.Parse(reader.GetString(1)) == at))
                result.Add(taskId);
        }
        return result;
    }

    private static TaskIntentSuppressionReceipt? ReadGraphSuppression(SqliteConnection conn,
        SqliteTransaction tx, TaskIntentAdmissionReference reference, string decisionKey, bool validateCommittedGraph)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT id,status FROM operator_intents WHERE goal_id=$goal AND verb=$verb AND status IN ('Pending','Claimed') ORDER BY created_at,id LIMIT 1";
        cmd.Parameters.AddWithValue("$goal", reference.GoalId);
        cmd.Parameters.AddWithValue("$verb", OperatorIntentVerbs.TaskGraphRepair);
        using (var reader = cmd.ExecuteReader())
            if (reader.Read()) return new(decisionKey, reference.GoalId, reference.TaskId,
                reader.GetString(0), OperatorIntentVerbs.TaskGraphRepair, reference.Version, reference.Version,
                reader.GetString(1), "pending-graph-repair");
        if (!validateCommittedGraph) return null;
        if (reference.GraphIdentity is not null)
        {
            var state = reference.StatePath is null ? null :
                SqliteOrchestratorStateRepository.OpenReadOnly(reference.StatePath)
                    .LoadGoalAsync(new GoalId(reference.GoalId)).GetAwaiter().GetResult();
            if (state is null || TaskGraphIdentity.Compute(state) != reference.GraphIdentity)
                return new(decisionKey, reference.GoalId, reference.TaskId, "graph-changed",
                    OperatorIntentVerbs.TaskGraphRepair, reference.Version, reference.Version,
                    "NotApplicable", "prepared-graph-changed");
        }
        else
        {
            cmd.CommandText = "SELECT id FROM operator_intents WHERE goal_id=$goal AND verb=$verb AND status='Applied' LIMIT 1";
            if (cmd.ExecuteScalar() is string id)
                return new(decisionKey, reference.GoalId, reference.TaskId, id, OperatorIntentVerbs.TaskGraphRepair,
                    reference.Version, reference.Version, "Applied", "unbound-prepared-graph");
        }
        return null;
    }
}
