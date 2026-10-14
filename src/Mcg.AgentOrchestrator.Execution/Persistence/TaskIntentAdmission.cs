using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.Infrastructure;

// Version means a task's dispatch-admission revision, not the goal snapshot version.
public sealed record TaskIntentAdmissionReference(string StorePath, string GoalId, string TaskId, long Version,
    DateTimeOffset? DispatchedAt = null);

public sealed record TaskIntentSuppressionReceipt(
    string DecisionKey, string GoalId, string TaskId, string IntentId, string Verb,
    long PreparedVersion, long TaskVersion, string IntentStatus, string Reason);

public sealed class TaskIntentDispatchSuppressedException(TaskIntentSuppressionReceipt receipt)
    : InvalidOperationException($"Task dispatch suppressed by operator intent '{receipt.IntentId}'.")
{
    public TaskIntentSuppressionReceipt Receipt { get; } = receipt;
}

public static class TaskIntentAdmission
{
    public const int SuppressedExitCode = 87;

    // Keep ownership on one thread even when the store callback performs asynchronous I/O.
    // The named kernel object is task-scoped and is never inherited by the worker.
    internal static T Serialize<T>(string storePath, string goalId, string? taskId, Func<T> action)
    {
        if (string.IsNullOrWhiteSpace(taskId)) return action();
        var path = Path.GetFullPath(storePath);
        if (OperatingSystem.IsWindows()) path = path.ToUpperInvariant();
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{path}\n{goalId}\n{taskId}")));
        using var mutex = new Mutex(false, (OperatingSystem.IsWindows() ? "Global\\" : "") + "mcg-task-intent-" + key);
        var owned = false;
        try
        {
            try { owned = mutex.WaitOne(TimeSpan.FromSeconds(30)); }
            catch (AbandonedMutexException)
            {
                owned = true;
                // Existing dispatch recovery must reconcile the durable host/process identity first.
                throw new InvalidOperationException("Abandoned task admission requires owned-process recovery before launch.");
            }
            if (!owned) throw new TimeoutException("Task admission serialization did not become available.");
            return action();
        }
        finally { if (owned) mutex.ReleaseMutex(); }
    }

    public static Process Launch(TaskIntentAdmissionReference reference, string decisionKey, Func<Process> start)
        => Serialize(reference.StorePath, reference.GoalId, reference.TaskId, () =>
        {
            var store = SqliteOperatorIntentStore.OpenAdmissionAuthority(reference.StorePath);
            store.RequireAdmissionSchema();
            if (store.GetSuppression(decisionKey) is { } previousDenial)
            {
                if (previousDenial.GoalId != reference.GoalId || previousDenial.TaskId != reference.TaskId ||
                    previousDenial.PreparedVersion != reference.Version)
                    throw new InvalidOperationException("Task admission decision identity was reused for another preparation.");
                throw new TaskIntentDispatchSuppressedException(previousDenial);
            }
            if (store.HasLaunch(decisionKey))
                throw new InvalidOperationException("Task admission launch identity was already consumed.");
            var denial = store.RecordSuppression(reference, decisionKey);
            if (denial is not null) throw new TaskIntentDispatchSuppressedException(denial);
            var process = start();
            try
            {
                store.RecordLaunch(reference, decisionKey, process);
                return process;
            }
            catch
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
                // The caller owns the returned process handle and its output/exit-artifact teardown.
                throw;
            }
        });
}

public sealed partial class SqliteOperatorIntentStore
{
    private static bool HasTaskTarget(OperatorIntentRecord intent) =>
        !string.IsNullOrWhiteSpace(intent.TaskId) && intent.GoalId != Mcg.AgentOrchestrator.Core.OperatorIntentScopes.Workspace;

    [StoreSchemaSource("operator-intents", "src/Mcg.AgentOrchestrator.Execution/Persistence/TaskIntentAdmission.cs")]
    private static void SetupTaskAdmission(SqliteConnection conn)
    {
        RunNonQuery(conn, """
            CREATE TABLE IF NOT EXISTS task_intent_admission (
                goal_id TEXT NOT NULL, task_id TEXT NOT NULL, revision INTEGER NOT NULL,
                latest_intent_id TEXT NOT NULL, PRIMARY KEY(goal_id, task_id));
            CREATE TABLE IF NOT EXISTS task_intent_suppressions (
                decision_key TEXT PRIMARY KEY, goal_id TEXT NOT NULL, task_id TEXT NOT NULL,
                intent_id TEXT NOT NULL, verb TEXT NOT NULL, prepared_version INTEGER NOT NULL,
                task_version INTEGER NOT NULL, intent_status TEXT NOT NULL, reason TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS task_intent_launches (
                decision_key TEXT PRIMARY KEY, goal_id TEXT NOT NULL, task_id TEXT NOT NULL,
                task_version INTEGER NOT NULL, process_id INTEGER NOT NULL,
                process_started_at TEXT NOT NULL, process_path TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS task_intent_preparations (
                goal_id TEXT NOT NULL, task_id TEXT NOT NULL, dispatched_at TEXT NOT NULL,
                task_version INTEGER NOT NULL, PRIMARY KEY(goal_id, task_id, dispatched_at));
            INSERT OR IGNORE INTO task_intent_admission
                SELECT a.goal_id, a.task_id, COUNT(*),
                    (SELECT b.id FROM operator_intents b WHERE b.goal_id = a.goal_id AND b.task_id = a.task_id
                     ORDER BY b.created_at DESC, b.id DESC LIMIT 1)
                FROM operator_intents a
                WHERE a.task_id IS NOT NULL AND a.task_id <> '' AND a.goal_id <> '@workspace'
                GROUP BY a.goal_id, a.task_id;
            UPDATE operator_intents SET admission_expires_at = strftime('%Y-%m-%dT%H:%M:%fZ', 'now', '+5 minutes')
                WHERE admission_expires_at IS NULL AND task_id IS NOT NULL AND task_id <> ''
                  AND goal_id <> '@workspace' AND status IN ('Pending', 'Claimed');
            """);
    }

    private static void AdvanceAdmissionRevision(SqliteConnection conn, SqliteTransaction tx, OperatorIntentRecord intent)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO task_intent_admission VALUES ($goal, $task, 1, $intent)
            ON CONFLICT(goal_id, task_id) DO UPDATE SET revision = revision + 1, latest_intent_id = $intent;
            """;
        BindTarget(cmd, intent.GoalId, intent.TaskId!);
        cmd.Parameters.AddWithValue("$intent", intent.Id);
        cmd.ExecuteNonQuery();
    }

    internal void RequireAdmissionSchema()
    {
        using var conn = OpenConnection();
        var state = StoreSchemaVersions.Verify(conn, StoreSchemaRegistry.OperatorIntents);
        if (state != StoreSchemaState.Current) throw SchemaSetupRequired(_dbPath, state);
    }

    internal static SqliteOperatorIntentStore OpenAdmissionAuthority(string storePath)
    {
        var store = new SqliteOperatorIntentStore(storePath, Path.GetDirectoryName(storePath)!,
            readOnly: false, utcNow: null, initialize: false);
        store.RequireAdmissionSchema();
        return store;
    }

    public void BindPreparedAdmission(TaskIntentAdmissionReference reference, DateTimeOffset dispatchedAt)
    {
        EnsureWritable();
        ValidateAdmissionReference(reference);
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT OR IGNORE INTO task_intent_preparations VALUES ($goal,$task,$at,$version)";
        BindTarget(cmd, reference.GoalId, reference.TaskId);
        cmd.Parameters.AddWithValue("$at", dispatchedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$version", reference.Version);
        cmd.ExecuteNonQuery();
    }

    public TaskIntentAdmissionReference GetPreparedAdmission(string goalId, string taskId, DateTimeOffset dispatchedAt)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT task_version FROM task_intent_preparations WHERE goal_id = $goal AND task_id = $task AND dispatched_at = $at";
        BindTarget(cmd, goalId, taskId);
        cmd.Parameters.AddWithValue("$at", dispatchedAt.ToString("O"));
        // Legacy unversioned preparations are conservatively fenced by any task intent.
        return new(_dbPath, goalId, taskId, Convert.ToInt64(cmd.ExecuteScalar() ?? 0L), dispatchedAt);
    }

    public TaskIntentAdmissionReference CaptureAdmission(string goalId, string taskId)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT revision FROM task_intent_admission WHERE goal_id = $goal AND task_id = $task";
        BindTarget(cmd, goalId, taskId);
        return new(_dbPath, goalId, taskId, Convert.ToInt64(cmd.ExecuteScalar() ?? 0L));
    }

    public IReadOnlySet<string> ListHeldTaskIds(string goalId)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT task_id FROM operator_intents WHERE goal_id = $goal AND task_id IS NOT NULL AND status IN ('Pending', 'Claimed')";
        cmd.Parameters.AddWithValue("$goal", goalId);
        using var reader = cmd.ExecuteReader();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read()) ids.Add(reader.GetString(0));
        return ids;
    }

    // Called under the task mutex. Terminal intent rows remain the source for obsolete-version receipts.
    public TaskIntentSuppressionReceipt? RecordSuppression(TaskIntentAdmissionReference reference, string decisionKey)
    {
        EnsureWritable();
        ValidateAdmissionReference(reference);
        if (reference.DispatchedAt is { } at && GetPreparedAdmission(reference.GoalId, reference.TaskId, at).Version != reference.Version)
            throw new InvalidOperationException("Task admission version does not match its durable preparation.");
        using var conn = OpenAdmissionWriteConnection();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT a.revision, i.id, i.verb, i.status
            FROM task_intent_admission a JOIN operator_intents i ON i.goal_id = a.goal_id AND i.task_id = a.task_id
            WHERE a.goal_id = $goal AND a.task_id = $task
              AND (i.status IN ('Pending', 'Claimed') OR (a.revision <> $version AND i.id = a.latest_intent_id))
            ORDER BY CASE WHEN i.status IN ('Pending', 'Claimed') THEN 0 ELSE 1 END, i.created_at, i.id LIMIT 1;
            """;
        BindTarget(cmd, reference.GoalId, reference.TaskId);
        cmd.Parameters.AddWithValue("$version", reference.Version);
        TaskIntentSuppressionReceipt? receipt = null;
        using (var reader = cmd.ExecuteReader())
        {
            if (reader.Read()) receipt = new(decisionKey, reference.GoalId, reference.TaskId,
                reader.GetString(1), reader.GetString(2), reference.Version, reader.GetInt64(0), reader.GetString(3),
                reader.GetString(3) is "Pending" or "Claimed" ? "pending-task-intent" : "task-admission-version-changed");
        }
        if (receipt is not null)
        {
            using var insert = conn.CreateCommand();
            insert.Transaction = tx;
            insert.CommandText = "INSERT OR IGNORE INTO task_intent_suppressions VALUES ($key,$goal,$task,$intent,$verb,$prepared,$version,$status,$reason)";
            BindTarget(insert, receipt.GoalId, receipt.TaskId);
            insert.Parameters.AddWithValue("$key", decisionKey);
            insert.Parameters.AddWithValue("$intent", receipt.IntentId);
            insert.Parameters.AddWithValue("$verb", receipt.Verb);
            insert.Parameters.AddWithValue("$prepared", receipt.PreparedVersion);
            insert.Parameters.AddWithValue("$version", receipt.TaskVersion);
            insert.Parameters.AddWithValue("$status", receipt.IntentStatus);
            insert.Parameters.AddWithValue("$reason", receipt.Reason);
            insert.ExecuteNonQuery();
        }
        tx.Commit();
        return receipt;
    }

    public TaskIntentSuppressionReceipt? GetSuppression(string decisionKey)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT decision_key,goal_id,task_id,intent_id,verb,prepared_version,task_version,intent_status,reason FROM task_intent_suppressions WHERE decision_key = $key";
        cmd.Parameters.AddWithValue("$key", decisionKey);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? new(reader.GetString(0),reader.GetString(1),reader.GetString(2),reader.GetString(3),
            reader.GetString(4),reader.GetInt64(5),reader.GetInt64(6),reader.GetString(7),reader.GetString(8)) : null;
    }

    internal void RecordLaunch(TaskIntentAdmissionReference reference, string decisionKey, Process process)
    {
        using var conn = OpenAdmissionWriteConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO task_intent_launches VALUES ($key,$goal,$task,$version,$pid,$started,$path)";
        BindTarget(cmd, reference.GoalId, reference.TaskId);
        cmd.Parameters.AddWithValue("$key", decisionKey);
        cmd.Parameters.AddWithValue("$version", reference.Version);
        cmd.Parameters.AddWithValue("$pid", process.Id);
        cmd.Parameters.AddWithValue("$started", process.StartTime.ToUniversalTime().ToString("O"));
        cmd.Parameters.AddWithValue("$path", process.MainModule?.FileName ?? throw new InvalidOperationException("Launched worker image identity is unavailable."));
        cmd.ExecuteNonQuery();
    }

    internal bool HasLaunch(string decisionKey)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM task_intent_launches WHERE decision_key = $key";
        cmd.Parameters.AddWithValue("$key", decisionKey);
        return cmd.ExecuteScalar() is not null;
    }

    private SqliteConnection OpenAdmissionWriteConnection()
    {
        EnsureWritable();
        // Dispatch hosts open existing schema only; they never migrate/create an absent authority.
        RequireAdmissionSchema();
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = _dbPath, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
        conn.Open();
        RunNonQuery(conn, "PRAGMA busy_timeout=30000");
        return conn;
    }

    private static void BindTarget(SqliteCommand cmd, string goalId, string taskId)
    {
        cmd.Parameters.AddWithValue("$goal", goalId);
        cmd.Parameters.AddWithValue("$task", taskId);
    }

    private void ValidateAdmissionReference(TaskIntentAdmissionReference reference)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference.GoalId);
        ArgumentException.ThrowIfNullOrWhiteSpace(reference.TaskId);
        if (reference.Version < 0) throw new ArgumentOutOfRangeException(nameof(reference));
        if (!string.Equals(Path.GetFullPath(reference.StorePath), _dbPath, OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidOperationException("Task admission reference belongs to another store.");
    }
}
