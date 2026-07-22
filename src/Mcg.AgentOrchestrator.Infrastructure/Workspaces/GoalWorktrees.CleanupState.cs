using System.Globalization;
using Mcg.AgentOrchestrator.Core;
using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.Infrastructure;

public static partial class GoalWorktrees
{
    public static GoalWorktreeCleanupBackoff? TryGetCleanupBackoff(string executionDirectory, GoalId goalId) =>
        TryGetCleanupBackoff(WorktreePath(executionDirectory, goalId));

    public static GoalWorktreeCleanupBackoff? TryGetCleanupBackoff(string path) =>
        TryReadOrphanCleanupBackoff(path, out var entry) ? ToCleanupBackoff(entry) : null;

    public static IReadOnlyList<GoalWorktreeCleanupDebt> ListCleanupDebt(string executionDirectory)
    {
        var statePath = CleanupStateStorePathForRoot(executionDirectory);
        if (!File.Exists(statePath))
        {
            return [];
        }

        var debts = new List<GoalWorktreeCleanupDebt>();
        try
        {
            using var conn = OpenCleanupStateConnection(statePath);
            using var command = conn.CreateCommand();
            command.CommandText = """
                SELECT b.path, b.reason, b.skip_until_utc,
                       j.first_seen_utc, j.last_seen_utc, j.last_operation, j.skip_count, j.escalated_at_utc
                FROM worktree_cleanup_backoff b
                LEFT JOIN worktree_cleanup_journal j ON j.path = b.path
                ORDER BY b.path;
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var path = reader.GetString(0);
                var reason = reader.GetString(1);
                if (!DateTimeOffset.TryParse(reader.GetString(2), out var skipUntilUtc))
                {
                    continue;
                }

                var firstSeenUtc = ReadOptionalDateTimeOffset(reader, 3) ??
                    skipUntilUtc - CleanupBackoffDurationFor(reason);
                var lastSeenUtc = ReadOptionalDateTimeOffset(reader, 4) ?? firstSeenUtc;
                var lastOperation = reader.IsDBNull(5) ? "(recorded)" : reader.GetString(5);
                var skipCount = reader.IsDBNull(6) ? 0 : reader.GetInt32(6);
                var escalatedAtUtc = ReadOptionalDateTimeOffset(reader, 7);
                var now = CleanupUtcNow();
                var age = now - firstSeenUtc;
                if (age < TimeSpan.Zero)
                {
                    age = TimeSpan.Zero;
                }

                var remaining = skipUntilUtc - now;
                if (remaining < TimeSpan.Zero)
                {
                    remaining = TimeSpan.Zero;
                }

                debts.Add(new GoalWorktreeCleanupDebt(
                    path,
                    reason,
                    lastOperation,
                    firstSeenUtc,
                    lastSeenUtc,
                    skipUntilUtc,
                    age,
                    remaining,
                    skipCount,
                    escalatedAtUtc));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException)
        {
            WarnCleanupFailure(executionDirectory, "cleanup-status:read", ex);
        }

        return debts;
    }

    public static GoalWorktreeCleanupBackoff? RecordGoalCleanupNeeded(
        string executionDirectory,
        GoalId goalId,
        string reason)
    {
        var path = WorktreePath(executionDirectory, goalId);
        RecordCleanupNeeded(path, reason);
        return TryGetCleanupBackoff(path);
    }


    private static string BuildCleanupDebtEscalationMessage(
        string path,
        string reason,
        GoalWorktreeCleanupDebt debt,
        IReadOnlyList<WorktreeLockHolder> lockHolders)
    {
        return "cleanup-debt escalation: " +
            $"path='{path}' reason={reason} age={FormatRemainingWait(debt.Age)} " +
            $"skip_count={debt.SkipCount} holders={FormatLockHolders(lockHolders)}";
    }

    private static string FormatLockHolders(IReadOnlyList<WorktreeLockHolder> lockHolders) =>
        lockHolders.Count == 0
            ? "none"
            : string.Join(
                ",",
                lockHolders.Select(holder =>
                    $"{holder.ProcessName}[pid={holder.ProcessId}]{(string.IsNullOrWhiteSpace(holder.CommandLine) ? string.Empty : $" command='{holder.CommandLine}'")}"));

    private static DateTimeOffset? ReadOptionalDateTimeOffset(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        return DateTimeOffset.TryParse(reader.GetString(ordinal), out var value)
            ? value
            : null;
    }

    private static string CleanupBackoffStorePath(string orphanPath)
    {
        var root = LocateCleanupStateRoot(orphanPath);
        return Path.Combine(root, ".orchestrator", "state.db");
    }

    private static string LocateCleanupStateRoot(string orphanPath)
    {
        var directory = new DirectoryInfo(Path.GetFullPath(orphanPath));
        while (directory.Parent is not null)
        {
            if (directory.Parent.Name.Equals(DirectoryName, StringComparison.OrdinalIgnoreCase))
            {
                return directory.Parent.Parent?.FullName ?? directory.Parent.FullName;
            }

            if (Directory.Exists(Path.Combine(directory.FullName, ".git")) ||
                File.Exists(Path.Combine(directory.FullName, ".git")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return Path.GetDirectoryName(Path.GetFullPath(orphanPath)) ?? Path.GetFullPath(orphanPath);
    }

    private static void TryDeleteEmptyDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
            {
                Directory.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            WarnCleanupFailure(path, "owned-ephemeral-sweep:empty-root", ex);
        }
    }

    private static bool IsCleanupBackedOff(
        string path,
        string operation,
        out OrphanCleanupBackoffEntry entry,
        string? cleanupStateRoot = null)
    {
        entry = default!;
        if (!operation.Equals("orphan-sweep", StringComparison.OrdinalIgnoreCase) &&
            !operation.Equals("owned-ephemeral-sweep", StringComparison.OrdinalIgnoreCase) &&
            !operation.Equals("remove", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!TryReadOrphanCleanupBackoff(path, out var existingEntry, cleanupStateRoot))
        {
            return false;
        }

        entry = existingEntry;
        if (entry.SkipUntilUtc > CleanupUtcNow())
        {
            return true;
        }

        ClearOrphanCleanupBackoff(path, cleanupStateRoot);
        return false;
    }

    private static void RecordCleanupNeeded(string path, string reason, string? cleanupStateRoot = null) =>
        RecordOrphanCleanupBackoff(path, reason, "remove:cleanup-needed", cleanupStateRoot);

    private static void ClearCleanupNeeded(string path, string? cleanupStateRoot = null) =>
        ClearOrphanCleanupBackoff(path, cleanupStateRoot);

    private static bool IsLockHeldCleanupNeededReason(string reason) =>
        reason.EndsWith(":lock-held", StringComparison.OrdinalIgnoreCase);

    private static bool IsBudgetExhaustedCleanupNeededReason(string reason) =>
        reason.Equals("remove:cleanup-budget-exhausted", StringComparison.OrdinalIgnoreCase);

    private static void RecordOrphanCleanupBackoff(
        string path,
        string reason,
        string warningOperation = "orphan-sweep:backoff",
        string? cleanupStateRoot = null)
    {
        try
        {
            using var conn = OpenCleanupBackoffConnection(path, cleanupStateRoot);
            using var command = conn.CreateCommand();
            command.CommandText = """
                INSERT INTO worktree_cleanup_backoff(path, skip_until_utc, reason)
                VALUES ($path, $skipUntilUtc, $reason)
                ON CONFLICT(path) DO UPDATE SET
                    skip_until_utc = excluded.skip_until_utc,
                    reason = excluded.reason;
                """;
            command.Parameters.AddWithValue("$path", NormalizePath(path));
            command.Parameters.AddWithValue("$skipUntilUtc", CleanupUtcNow().Add(CleanupBackoffDurationFor(reason)).ToString("O"));
            command.Parameters.AddWithValue("$reason", reason);
            command.ExecuteNonQuery();
            _ = RecordCleanupDebtObserved(conn, path, warningOperation, reason, incrementSkipCount: false);
            WarnCleanupFailure(path, warningOperation, new TimeoutException(BuildCleanupRetryMessage(path, reason, TryGetCleanupBackoff(path, cleanupStateRoot))));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException)
        {
            WarnCleanupFailure(path, "orphan-sweep:backoff-write", ex);
        }
    }

    private static bool TryReadOrphanCleanupBackoff(
        string path,
        out OrphanCleanupBackoffEntry entry,
        string? cleanupStateRoot = null)
    {
        entry = default!;
        try
        {
            var statePath = CleanupBackoffStorePath(path, cleanupStateRoot);
            if (!File.Exists(statePath))
            {
                return false;
            }

            using var conn = OpenCleanupBackoffConnection(path, cleanupStateRoot);
            using var command = conn.CreateCommand();
            command.CommandText = "SELECT skip_until_utc, reason FROM worktree_cleanup_backoff WHERE path = $path";
            command.Parameters.AddWithValue("$path", NormalizePath(path));
            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                return false;
            }

            if (!DateTimeOffset.TryParse(reader.GetString(0), out var skipUntilUtc))
            {
                return false;
            }

            entry = new OrphanCleanupBackoffEntry(skipUntilUtc, reader.GetString(1));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException)
        {
            WarnCleanupFailure(path, "orphan-sweep:backoff-read", ex);
            return false;
        }
    }

    private static void ClearOrphanCleanupBackoff(string path, string? cleanupStateRoot = null)
    {
        try
        {
            var statePath = CleanupBackoffStorePath(path, cleanupStateRoot);
            if (!File.Exists(statePath))
                return;

            using var conn = OpenCleanupBackoffConnection(path, cleanupStateRoot);
            using var command = conn.CreateCommand();
            command.CommandText = "DELETE FROM worktree_cleanup_backoff WHERE path = $path";
            command.Parameters.AddWithValue("$path", NormalizePath(path));
            command.ExecuteNonQuery();
            using var journalCommand = conn.CreateCommand();
            journalCommand.CommandText = "DELETE FROM worktree_cleanup_journal WHERE path = $path";
            journalCommand.Parameters.AddWithValue("$path", NormalizePath(path));
            journalCommand.ExecuteNonQuery();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException)
        {
            WarnCleanupFailure(path, "orphan-sweep:backoff-clear", ex);
        }
    }

    private static string CleanupBackoffStorePath(string path, string? cleanupStateRoot) =>
        cleanupStateRoot is null
            ? CleanupBackoffStorePath(path)
            : Path.Combine(Path.GetFullPath(cleanupStateRoot), ".orchestrator", "state.db");

    private static string CleanupStateStorePathForRoot(string executionDirectory) =>
        Path.Combine(Path.GetFullPath(executionDirectory), ".orchestrator", "state.db");

    private static SqliteConnection OpenCleanupBackoffConnection(string path, string? cleanupStateRoot = null)
    {
        var statePath = CleanupBackoffStorePath(path, cleanupStateRoot);
        return OpenCleanupStateConnection(statePath);
    }

    private static SqliteConnection OpenCleanupStateConnection(string statePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = statePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString());
        conn.Open();
        using var command = conn.CreateCommand();
        command.CommandText = CleanupBackoffTableSql + Environment.NewLine + CleanupDebtJournalTableSql;
        command.ExecuteNonQuery();
        return conn;
    }

    private static void JournalCleanupBackoffSkip(
        string path,
        string operation,
        OrphanCleanupBackoffEntry backoff,
        string? cleanupStateRoot = null)
    {
        try
        {
            using var conn = OpenCleanupBackoffConnection(path, cleanupStateRoot);
            var observation = RecordCleanupDebtObserved(conn, path, operation, backoff.Reason, incrementSkipCount: true);
            if (observation.EscalatedNow)
            {
                var lockHolders = FindLockHoldersForCleanup(path);
                WarnCleanupFailure(
                    path,
                    operation.Replace(":skip-backoff", ":cleanup-debt-escalated", StringComparison.OrdinalIgnoreCase),
                    new IOException(BuildCleanupDebtEscalationMessage(path, backoff.Reason, observation.Debt, lockHolders)));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException)
        {
            WarnCleanupFailure(path, "orphan-sweep:journal-write", ex);
        }
    }

    private static CleanupDebtObservation RecordCleanupDebtObserved(
        SqliteConnection conn,
        string path,
        string operation,
        string reason,
        bool incrementSkipCount)
    {
        var normalizedPath = NormalizePath(path);
        var now = CleanupUtcNow();
        var wasEscalated = HasCleanupDebtEscalation(conn, normalizedPath);
        using var command = conn.CreateCommand();
        command.CommandText = """
            INSERT INTO worktree_cleanup_journal(
                path,
                first_seen_utc,
                last_seen_utc,
                last_operation,
                last_reason,
                skip_count,
                escalated_at_utc)
            VALUES (
                $path,
                $now,
                $now,
                $operation,
                $reason,
                $skipIncrement,
                NULL)
            ON CONFLICT(path) DO UPDATE SET
                last_seen_utc = excluded.last_seen_utc,
                last_operation = excluded.last_operation,
                last_reason = excluded.last_reason,
                skip_count = worktree_cleanup_journal.skip_count + $skipIncrement,
                escalated_at_utc = CASE
                    WHEN worktree_cleanup_journal.escalated_at_utc IS NULL
                         AND worktree_cleanup_journal.skip_count + $skipIncrement >= $escalationThreshold
                    THEN $now
                    ELSE worktree_cleanup_journal.escalated_at_utc
                END;
            """;
        command.Parameters.AddWithValue("$path", normalizedPath);
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        command.Parameters.AddWithValue("$operation", operation);
        command.Parameters.AddWithValue("$reason", reason);
        command.Parameters.AddWithValue("$skipIncrement", incrementSkipCount ? 1 : 0);
        command.Parameters.AddWithValue("$escalationThreshold", CleanupDebtEscalationSkipThreshold);
        command.ExecuteNonQuery();

        using var read = conn.CreateCommand();
        read.CommandText = """
            SELECT first_seen_utc, last_seen_utc, last_operation, skip_count, escalated_at_utc
            FROM worktree_cleanup_journal
            WHERE path = $path;
            """;
        read.Parameters.AddWithValue("$path", normalizedPath);
        using var reader = read.ExecuteReader();
        if (!reader.Read())
        {
            throw new InvalidOperationException("Cleanup journal write succeeded but no row was readable.");
        }

        var firstSeen = DateTimeOffset.Parse(reader.GetString(0), CultureInfo.InvariantCulture);
        var lastSeen = DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture);
        var skipCount = reader.GetInt32(3);
        var escalatedAt = ReadOptionalDateTimeOffset(reader, 4);
        var age = now - firstSeen;
        if (age < TimeSpan.Zero)
        {
            age = TimeSpan.Zero;
        }

        var debt = new GoalWorktreeCleanupDebt(
            normalizedPath,
            reason,
            reader.GetString(2),
            firstSeen,
            lastSeen,
            now.Add(CleanupBackoffDurationFor(reason)),
            age,
            CleanupBackoffDurationFor(reason),
            skipCount,
            escalatedAt);
        var escalatedNow = incrementSkipCount &&
            !wasEscalated &&
            escalatedAt is not null &&
            skipCount >= CleanupDebtEscalationSkipThreshold;
        return new CleanupDebtObservation(debt, escalatedNow);
    }

    private static bool HasCleanupDebtEscalation(SqliteConnection conn, string normalizedPath)
    {
        using var command = conn.CreateCommand();
        command.CommandText = "SELECT escalated_at_utc FROM worktree_cleanup_journal WHERE path = $path";
        command.Parameters.AddWithValue("$path", normalizedPath);
        var value = command.ExecuteScalar();
        return value is not null && value != DBNull.Value && !string.IsNullOrWhiteSpace(Convert.ToString(value, CultureInfo.InvariantCulture));
    }

    private sealed record OrphanCleanupBackoffEntry(DateTimeOffset SkipUntilUtc, string Reason);

    private sealed record CleanupDebtObservation(GoalWorktreeCleanupDebt Debt, bool EscalatedNow);

    private static TimeSpan CleanupBackoffDurationFor(string reason) =>
        IsBudgetExhaustedCleanupNeededReason(reason) ? CleanupBudgetExhaustedBackoffDuration : CleanupBackoffDuration;

    private static GoalWorktreeCleanupBackoff ToCleanupBackoff(OrphanCleanupBackoffEntry entry)
    {
        var remaining = entry.SkipUntilUtc - CleanupUtcNow();
        if (remaining < TimeSpan.Zero)
        {
            remaining = TimeSpan.Zero;
        }

        return new GoalWorktreeCleanupBackoff(entry.Reason, entry.SkipUntilUtc, remaining);
    }

    private static GoalWorktreeCleanupBackoff? TryGetCleanupBackoff(string path, string? cleanupStateRoot) =>
        TryReadOrphanCleanupBackoff(path, out var entry, cleanupStateRoot) ? ToCleanupBackoff(entry) : null;

    public static string FormatCleanupBackoff(GoalWorktreeCleanupBackoff backoff) =>
        $"reason={backoff.Reason} skip_until_utc={backoff.SkipUntilUtc:O} remaining_wait={FormatRemainingWait(backoff.RemainingWait)}";

    private static string FormatRemainingWait(TimeSpan wait) =>
        wait.TotalSeconds < 1
            ? "00:00:00"
            : wait.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);

}
