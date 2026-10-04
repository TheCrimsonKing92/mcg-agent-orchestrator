using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record PanelCaseKey(string GoalId, string CandidateSha, string BaseSha,
    string CriteriaVersion, string TriggerId, string TriggerKind, string Packet);
internal sealed record PanelCase(string Id, PanelCaseKey Key, long EnqueueOrder, int? EnrollmentWindow,
    string Status, string? ClaimToken, PanelCaseTerminal? Terminal, string? Reason, string PacketHash);
internal sealed record PanelGoalEnrollment(string GoalId, DateTimeOffset CreatedAt);
internal sealed record PanelJudgeHealth(string Judge, int ConsecutiveFailures, PanelJudgeHealthState State,
    string? SuspendedCaseId);

// The ledger owns claims and call reservations. A reservation is never retried, even after a crash.
internal sealed partial class ConductorJudgePanelCaseStore
{
    private readonly string _connectionString;
    internal ConductorJudgePanelCaseStore(string path) => _connectionString =
        new SqliteConnectionStringBuilder { DataSource = Path.GetFullPath(path), Pooling = false }.ToString();

    internal static string CaseId(PanelCaseKey key) => Hash(JsonSerializer.Serialize(new[]
        { key.GoalId, key.CandidateSha, key.BaseSha, key.CriteriaVersion, key.TriggerId }));
    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    internal void Start(DateTimeOffset now, IEnumerable<PanelGoalEnrollment> goals)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        Execute(connection, transaction, "INSERT OR IGNORE INTO panel_meta VALUES ('first-start', $value)",
            ("$value", now.ToUniversalTime().ToString("O")));
        var first = DateTimeOffset.Parse((string)Scalar(connection, transaction,
            "SELECT value FROM panel_meta WHERE key = 'first-start'")!);
        foreach (var goal in goals.Where(goal => goal.CreatedAt > first)
                     .OrderBy(goal => goal.CreatedAt).ThenBy(goal => goal.GoalId, StringComparer.Ordinal))
            Execute(connection, transaction,
                "INSERT OR IGNORE INTO panel_goals(goal_id, created_at) VALUES ($goal, $created)",
                ("$goal", goal.GoalId), ("$created", goal.CreatedAt.ToUniversalTime().ToString("O")));
        transaction.Commit();
    }

    internal PanelCase Enqueue(PanelCaseKey key)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        var id = CaseId(key);
        var existing = ReadCase(connection, transaction, id);
        if (existing is not null) { transaction.Commit(); return existing; }
        var order = Scalar(connection, transaction, "SELECT ordinal FROM panel_goals WHERE goal_id = $goal", ("$goal", key.GoalId));
        int? window = order is long ordinal ? (int)((ordinal - 1) / ConductorJudgePanelBudgets.EnrollmentWindowGoals) : null;
        string? reason = window is null ? "goal-not-enrolled-after-panel-start" : null;
        if (reason is null && (long)Scalar(connection, transaction,
                "SELECT COUNT(*) FROM panel_cases WHERE enrollment_window = $window AND admitted = 1", ("$window", window))! >=
            ConductorJudgePanelBudgets.MaxCasesPerEnrollmentWindow) reason = "enrollment-window";
        if (reason is null && (long)Scalar(connection, transaction,
                "SELECT COUNT(*) FROM panel_cases WHERE status = 'pending'")! >= ConductorJudgePanelBudgets.MaxQueuedCases)
            reason = "queue-cap";
        Execute(connection, transaction, """
            INSERT INTO panel_cases(case_id, goal_id, candidate_sha, base_sha, criteria_version, trigger_id,
                key_json, packet_hash, enrollment_window, admitted, status, terminal, reason)
            VALUES ($id, $goal, $candidate, $base, $criteria, $trigger, $json, $hash, $window, $admitted, $status, $terminal, $reason)
            """, ("$id", id), ("$goal", key.GoalId), ("$candidate", key.CandidateSha), ("$base", key.BaseSha),
            ("$criteria", key.CriteriaVersion), ("$trigger", key.TriggerId), ("$json", JsonSerializer.Serialize(key)),
            ("$hash", Hash(key.Packet)), ("$window", window), ("$admitted", reason is null ? 1 : 0),
            ("$status", reason is null ? "pending" : "terminal"),
            ("$terminal", reason is null ? null : PanelV0Contract.Token(PanelCaseTerminal.BudgetSkip)), ("$reason", reason));
        var result = ReadCase(connection, transaction, id)!;
        transaction.Commit();
        return result;
    }

    internal PanelCase? ClaimNext(DateTimeOffset now, IReadOnlySet<string>? eligibleGoalIds = null)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        if ((long)Scalar(connection, transaction, "SELECT COUNT(*) FROM panel_cases WHERE status = 'in-flight'")! != 0)
        { transaction.Commit(); return null; }
        string? id = null;
        using (var select = Command(connection, transaction,
                   "SELECT case_id, goal_id FROM panel_cases WHERE status IN ('pending', 'ready') ORDER BY rowid"))
        using (var reader = select.ExecuteReader())
            while (reader.Read())
                if (eligibleGoalIds is null || eligibleGoalIds.Contains(reader.GetString(1)))
                { id = reader.GetString(0); break; }
        if (id is null) { transaction.Commit(); return null; }
        Execute(connection, transaction,
            "UPDATE panel_cases SET status = 'in-flight', claimed_at = $now, claim_token = $token WHERE case_id = $id",
            ("$now", now.ToUniversalTime().ToString("O")), ("$token", Guid.NewGuid().ToString("N")), ("$id", id));
        var result = ReadCase(connection, transaction, id);
        transaction.Commit();
        return result;
    }

    internal PanelCase? Get(string id)
    {
        using var connection = Open();
        return ReadCase(connection, null, id);
    }

    internal IReadOnlyList<PanelCase> Cases()
    {
        using var connection = Open();
        return ReadCases(connection, "SELECT case_id FROM panel_cases ORDER BY rowid");
    }

    internal IReadOnlyList<PanelJudgeResult> Results(string id)
    {
        using var connection = Open();
        return ReadResults(connection, null, id);
    }

    private static PanelCase? ReadCase(SqliteConnection connection, SqliteTransaction? transaction, string id)
    {
        using var command = Command(connection, transaction,
            "SELECT key_json, rowid, enrollment_window, status, claim_token, terminal, reason, packet_hash FROM panel_cases WHERE case_id = $id", ("$id", id));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        var terminal = reader.IsDBNull(5) ? null : reader.GetString(5);
        return new(id, JsonSerializer.Deserialize<PanelCaseKey>(reader.GetString(0))!, reader.GetInt64(1),
            reader.IsDBNull(2) ? null : reader.GetInt32(2), reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4), terminal switch
            {
                "completed" => PanelCaseTerminal.Completed, "superseded" => PanelCaseTerminal.Superseded,
                "suspended-skip" => PanelCaseTerminal.SuspendedSkip, "budget-skip" => PanelCaseTerminal.BudgetSkip,
                _ => null
            }, reader.IsDBNull(6) ? null : reader.GetString(6), reader.GetString(7));
    }

    private static IReadOnlyList<PanelCase> ReadCases(SqliteConnection connection, string sql)
    {
        var ids = new List<string>();
        using (var command = Command(connection, null, sql))
        using (var reader = command.ExecuteReader())
            while (reader.Read()) ids.Add(reader.GetString(0));
        return ids.Select(id => ReadCase(connection, null, id)!).ToArray();
    }

    private static IReadOnlyList<PanelJudgeResult> ReadResults(SqliteConnection connection, SqliteTransaction? transaction, string id)
    {
        var results = new List<PanelJudgeResult>();
        using var command = Command(connection, transaction,
            "SELECT result_json FROM panel_calls WHERE case_id = $id AND result_json IS NOT NULL ORDER BY judge", ("$id", id));
        using var reader = command.ExecuteReader();
        while (reader.Read()) results.Add(JsonSerializer.Deserialize<PanelJudgeResult>(reader.GetString(0))!);
        return results;
    }
}
