using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorJudgePanelCaseStore
{
    internal bool ReserveCall(PanelCase claim, string judge, PanelJudgeResult? skip = null)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        if (!OwnsClaim(connection, transaction, claim)) { transaction.Commit(); return false; }
        if (skip is null && (long)Scalar(connection, transaction,
                "SELECT COUNT(*) FROM panel_calls WHERE case_id = $id AND launched = 1", ("$id", claim.Id))! >=
            ConductorJudgePanelBudgets.MaxJudgeCallsPerCase) { transaction.Commit(); return false; }
        var inserted = Execute(connection, transaction, """
            INSERT OR IGNORE INTO panel_calls(case_id, judge, launched, outcome, result_json)
            VALUES ($id, $judge, $launched, $outcome, $json)
            """, ("$id", claim.Id), ("$judge", judge), ("$launched", skip is null ? 1 : 0),
            ("$outcome", skip is null ? null : PanelV0Contract.Token(skip.Outcome)),
            ("$json", skip is null ? null : JsonSerializer.Serialize(skip)));
        transaction.Commit();
        return inserted == 1 && skip is null;
    }

    internal void RecordResult(PanelCase claim, PanelJudgeResult result)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        if (OwnsClaim(connection, transaction, claim))
            Execute(connection, transaction, """
                UPDATE panel_calls SET result_json = $json, outcome = $outcome
                WHERE case_id = $id AND judge = $judge AND result_json IS NULL
                """, ("$json", JsonSerializer.Serialize(result)), ("$outcome", PanelV0Contract.Token(result.Outcome)),
                ("$id", claim.Id), ("$judge", result.Judge));
        transaction.Commit();
    }

    // Used for clean shutdown and expired ownership. Preserve partial receipts; never re-launch reserved calls.
    internal void Preserve(PanelCase claim, string reason)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        if (!OwnsClaim(connection, transaction, claim)) { transaction.Commit(); return; }
        var judges = new List<string>();
        using (var select = Command(connection, transaction,
                   "SELECT judge FROM panel_calls WHERE case_id = $id AND result_json IS NULL", ("$id", claim.Id)))
        using (var reader = select.ExecuteReader())
            while (reader.Read()) judges.Add(reader.GetString(0));
        foreach (var judge in judges)
        {
            var result = new PanelJudgeResult(judge, PanelJudgeOutcome.TimedOut, null, "", "", Reason: reason);
            Execute(connection, transaction, """
                UPDATE panel_calls SET result_json = $json, outcome = 'timed-out'
                WHERE case_id = $id AND judge = $judge AND result_json IS NULL
                """, ("$json", JsonSerializer.Serialize(result)), ("$id", claim.Id), ("$judge", judge));
        }
        Execute(connection, transaction,
            "UPDATE panel_cases SET status = 'ready', claim_token = NULL, claimed_at = NULL WHERE case_id = $id",
            ("$id", claim.Id));
        transaction.Commit();
    }

    internal void ExpireStale(DateTimeOffset before, string? activeId)
    {
        using var connection = Open();
        var expired = ReadCases(connection, "SELECT case_id FROM panel_cases WHERE status = 'in-flight'")
            .Where(item => item.Id != activeId).ToArray();
        foreach (var claim in expired)
        {
            var at = Scalar(connection, null, "SELECT claimed_at FROM panel_cases WHERE case_id = $id", ("$id", claim.Id));
            if (at is string text && DateTimeOffset.Parse(text) < before) Preserve(claim, "claim-expired");
        }
    }

    internal PanelJudgeHealth Health(string judge)
    {
        using var connection = Open();
        return ReadHealth(connection, null, judge);
    }

    internal void Complete(PanelCase claim, PanelCaseTerminal terminal, string? reason)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        if (!OwnsClaim(connection, transaction, claim)) { transaction.Commit(); return; }
        foreach (var result in ReadResults(connection, transaction, claim.Id))
        {
            var health = ReadHealth(connection, transaction, result.Judge);
            if (health.State == PanelJudgeHealthState.Suspended || result.Outcome == PanelJudgeOutcome.Skipped) continue;
            var failures = result.Outcome switch
            {
                PanelJudgeOutcome.Valid => 0,
                PanelJudgeOutcome.InvalidOutput or PanelJudgeOutcome.EmptyOutput or PanelJudgeOutcome.InvocationFailed =>
                    health.ConsecutiveFailures + 1,
                _ => health.ConsecutiveFailures
            };
            var suspended = failures >= ConductorJudgePanelBudgets.SuspendAfterConsecutiveProtocolFailures;
            Execute(connection, transaction, """
                INSERT INTO panel_judge_health VALUES ($judge, $failures, $state, $id)
                ON CONFLICT(judge) DO UPDATE SET consecutive_failures = excluded.consecutive_failures,
                    state = excluded.state, suspended_case_id = excluded.suspended_case_id
                """, ("$judge", result.Judge), ("$failures", failures), ("$state", suspended ? "suspended" : "active"),
                ("$id", suspended ? claim.Id : null));
        }
        Execute(connection, transaction, """
            UPDATE panel_cases SET status = 'terminal', terminal = $terminal, reason = $reason,
                claim_token = NULL, claimed_at = NULL WHERE case_id = $id
            """, ("$terminal", PanelV0Contract.Token(terminal)), ("$reason", reason), ("$id", claim.Id));
        transaction.Commit();
    }

    internal IReadOnlyList<PanelCase> Unreported()
    {
        using var connection = Open();
        return ReadCases(connection, "SELECT case_id FROM panel_cases WHERE status = 'terminal' AND event_written = 0 ORDER BY rowid");
    }
    internal void MarkReported(string id)
    {
        using var connection = Open();
        Execute(connection, null, "UPDATE panel_cases SET event_written = 1 WHERE case_id = $id", ("$id", id));
    }
    internal void SetIdleReason(string reason)
    {
        using var connection = Open();
        Execute(connection, null,
            "INSERT INTO panel_meta VALUES ('idle-reason', $value) ON CONFLICT(key) DO UPDATE SET value = excluded.value",
            ("$value", reason));
    }
    internal string? IdleReason()
    {
        using var connection = Open();
        return Scalar(connection, null, "SELECT value FROM panel_meta WHERE key = 'idle-reason'") as string;
    }
    private static bool OwnsClaim(SqliteConnection connection, SqliteTransaction transaction, PanelCase claim) =>
        claim.ClaimToken is not null && (long)Scalar(connection, transaction,
            "SELECT COUNT(*) FROM panel_cases WHERE case_id = $id AND status = 'in-flight' AND claim_token = $token",
            ("$id", claim.Id), ("$token", claim.ClaimToken))! == 1;

    private static PanelJudgeHealth ReadHealth(SqliteConnection connection, SqliteTransaction? transaction, string judge)
    {
        using var select = Command(connection, transaction,
            "SELECT consecutive_failures, state, suspended_case_id FROM panel_judge_health WHERE judge = $judge", ("$judge", judge));
        using var reader = select.ExecuteReader();
        return reader.Read() ? new(judge, reader.GetInt32(0),
            reader.GetString(1) == "suspended" ? PanelJudgeHealthState.Suspended : PanelJudgeHealthState.Active,
            reader.IsDBNull(2) ? null : reader.GetString(2)) : new(judge, 0, PanelJudgeHealthState.Active, null);
    }
}
