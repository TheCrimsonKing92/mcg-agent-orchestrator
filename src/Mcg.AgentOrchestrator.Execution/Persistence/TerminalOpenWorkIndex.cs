using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class TerminalOpenWorkIndex
{
    internal const string ExistsSql = "SELECT EXISTS (SELECT 1 FROM sqlite_schema WHERE type = 'table' AND name = 'goal_open_work')";
    internal const string ListSql = "SELECT goal_id FROM goal_open_work WHERE terminal_open_work = 1 ORDER BY goal_id";

    internal static string OpenWorkFlagSql(string statusExpression, string snapshotExpression) => $"""
        CASE WHEN {statusExpression} IN ('{GoalStatus.Superseded}', '{GoalStatus.Cancelled}', '{GoalStatus.Completed}')
          AND json_valid({snapshotExpression}) THEN EXISTS (
            SELECT 1 FROM json_each({snapshotExpression}, '$.Tasks') AS task
            WHERE json_extract(task.value, '$.Status') NOT IN ('{WorkTaskStatus.Completed}', '{WorkTaskStatus.Cancelled}')
              OR EXISTS (
                  SELECT 1 FROM json_each(task.value, '$.WorkerBuildInvocations') AS invocation
                  WHERE json_extract(invocation.value, '$.Outcome') IS NULL
                     OR COALESCE(json_extract(invocation.value, '$.CustodyReleased'), 0) = 0)
        ) ELSE 0 END
        """;

    internal static readonly string ScanSql = $"SELECT id FROM goals WHERE {OpenWorkFlagSql("status", "goals.snapshot_json")} = 1 ORDER BY id";

    internal static readonly string SchemaSql = $"""
        CREATE TABLE goal_open_work (goal_id TEXT PRIMARY KEY, terminal_open_work INTEGER NOT NULL);
        CREATE INDEX ix_goal_open_work_terminal ON goal_open_work(goal_id) WHERE terminal_open_work = 1;
        CREATE TRIGGER goal_open_work_after_insert AFTER INSERT ON goals
        BEGIN
            INSERT OR REPLACE INTO goal_open_work (goal_id, terminal_open_work)
            VALUES (NEW.id, {OpenWorkFlagSql("NEW.status", "NEW.snapshot_json")});
        END;
        CREATE TRIGGER goal_open_work_after_update AFTER UPDATE OF status, snapshot_json ON goals
        BEGIN
            INSERT OR REPLACE INTO goal_open_work (goal_id, terminal_open_work)
            VALUES (NEW.id, {OpenWorkFlagSql("NEW.status", "NEW.snapshot_json")});
        END;
        """;

    internal static readonly string BackfillSql = $"""
        INSERT OR REPLACE INTO goal_open_work (goal_id, terminal_open_work)
        SELECT id, {OpenWorkFlagSql("status", "goals.snapshot_json")} FROM goals
        """;
}
