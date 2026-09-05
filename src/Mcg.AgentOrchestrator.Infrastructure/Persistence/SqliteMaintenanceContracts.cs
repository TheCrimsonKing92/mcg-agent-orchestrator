namespace Mcg.AgentOrchestrator.Infrastructure;

public enum SqliteMaintenanceDisposition
{
    Completed,
    Incomplete,
    Deferred,
    Stalled
}

public enum SqliteMaintenanceReason
{
    None,
    DatabaseBusy,
    WorkCapReached,
    CheckpointBusy,
    CheckpointBusyLimit,
    ProtectedEvidenceFloor,
    IncrementalVacuumUnavailable,
    ByteGrowthExceeded,
    NoProgressLimit,
    ActiveWork,
    OutsideMaintenanceWindow,
    BelowThreshold,
    MeasurementUnavailable,
    ExplicitAuthorizationRequired,
    InsufficientFreeSpace,
    OpenTransaction,
    CandidateIntegrityCheckFailed,
    CandidateSchemaMismatch,
    CandidateJournalModeMismatch,
    CandidateAutoVacuumMismatch,
    ReplacementFailed,
    ReplacementRollbackFailed,
    InstalledValidationFailed,
    DatabaseBusyLimit,
    BackupCleanupFailed
}

public sealed record SqliteStorageSnapshot(
    long MainDatabaseBytes,
    long WalBytes,
    long ShmBytes,
    long OtherTransientBytes)
{
    public long TotalBytes => checked(MainDatabaseBytes + WalBytes + ShmBytes + OtherTransientBytes);

    public static SqliteStorageSnapshot Measure(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        if (!File.Exists(databasePath))
        {
            throw new FileNotFoundException("The SQLite database could not be measured.", databasePath);
        }

        var fullPath = Path.GetFullPath(databasePath);
        var directory = Path.GetDirectoryName(fullPath) ?? Directory.GetCurrentDirectory();
        var fileName = Path.GetFileName(fullPath);
        var walPath = fullPath + "-wal";
        var shmPath = fullPath + "-shm";
        long other = 0;
        foreach (var candidate in Directory.EnumerateFiles(directory, fileName + "-*", SearchOption.TopDirectoryOnly))
        {
            if (candidate.Equals(walPath, StringComparison.OrdinalIgnoreCase) ||
                candidate.Equals(shmPath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            other = checked(other + new FileInfo(candidate).Length);
        }

        return new SqliteStorageSnapshot(
            new FileInfo(fullPath).Length,
            File.Exists(walPath) ? new FileInfo(walPath).Length : 0,
            File.Exists(shmPath) ? new FileInfo(shmPath).Length : 0,
            other);
    }
}

public sealed record SqliteCheckpointResult(int Busy, int LogPages, int CheckpointedPages)
{
    public bool Completed => Busy == 0;
}

public sealed record SqlitePageMetrics(
    long PageCount,
    long PageSize,
    long FreelistCount,
    string JournalMode,
    int AutoVacuumMode)
{
    public long DatabaseBytes => checked(PageCount * PageSize);

    public long ReclaimableBytes => checked(FreelistCount * PageSize);
}

public sealed record SqliteMaintenanceDecision(
    SqliteMaintenanceDisposition Disposition,
    SqliteMaintenanceReason Reason,
    DateTimeOffset? NextAttemptAt);

public static class SqliteMaintenanceClassifier
{
    public static SqliteMaintenanceDecision Classify(
        SqliteStorageSnapshot before,
        SqliteStorageSnapshot after,
        SqliteCheckpointResult checkpoint,
        int remainingEligibleRows,
        long remainingBytesOverBudget,
        int noProgressAttempts,
        int maxNoProgressAttempts,
        int autoVacuumMode,
        long freelistCount,
        bool vacuumDeferred,
        long materialGrowthToleranceBytes,
        DateTimeOffset continuationDue,
        bool workCapReachedWithByteWorkPending = false)
    {
        if (!checkpoint.Completed || vacuumDeferred)
        {
            return noProgressAttempts >= maxNoProgressAttempts
                ? new(SqliteMaintenanceDisposition.Stalled, SqliteMaintenanceReason.CheckpointBusyLimit, null)
                : Incomplete(SqliteMaintenanceReason.CheckpointBusy, continuationDue);
        }

        if (after.TotalBytes > before.TotalBytes + Math.Max(0, materialGrowthToleranceBytes))
            return new(SqliteMaintenanceDisposition.Stalled, SqliteMaintenanceReason.ByteGrowthExceeded, null);

        if (remainingBytesOverBudget > 0 && noProgressAttempts >= maxNoProgressAttempts)
            return new(SqliteMaintenanceDisposition.Stalled, SqliteMaintenanceReason.NoProgressLimit, null);

        if (remainingEligibleRows > 0 || workCapReachedWithByteWorkPending)
            return Incomplete(SqliteMaintenanceReason.WorkCapReached, continuationDue);

        if (remainingBytesOverBudget > 0 && autoVacuumMode != 2 && freelistCount > 0)
            return new(SqliteMaintenanceDisposition.Stalled, SqliteMaintenanceReason.IncrementalVacuumUnavailable, null);

        if (remainingBytesOverBudget > 0)
            return new(SqliteMaintenanceDisposition.Stalled, SqliteMaintenanceReason.ProtectedEvidenceFloor, null);

        return new(SqliteMaintenanceDisposition.Completed, SqliteMaintenanceReason.None, null);
    }

    private static SqliteMaintenanceDecision Incomplete(
        SqliteMaintenanceReason reason,
        DateTimeOffset continuationDue) =>
        new(SqliteMaintenanceDisposition.Incomplete, reason, continuationDue);
}
