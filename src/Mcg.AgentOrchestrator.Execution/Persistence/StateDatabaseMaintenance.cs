using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.Infrastructure;

public enum StateDatabaseMaintenanceDecisionKind
{
    SkippedBelowThreshold,
    DeferredActiveWork,
    DeferredOutsideWindow,
    CheckpointOnly,
    BoundedIncrementalReclaim,
    OfflineConversionRequired
}

public sealed record StateDatabaseMaintenanceOptions(
    long MinimumDatabaseBytes = 256L * 1024 * 1024,
    long MinimumReclaimableBytes = 64L * 1024 * 1024,
    double MinimumFreelistRatio = 0.20,
    int MaxIncrementalVacuumPages = 4096,
    DateTimeOffset? UtcNow = null,
    DayOfWeek OffPeakDay = DayOfWeek.Sunday,
    int OffPeakStartHourUtc = 2,
    int OffPeakEndHourUtc = 5);

public sealed record StateDatabaseMaintenancePlan(
    StateDatabaseMaintenanceDecisionKind Decision,
    SqliteMaintenanceReason Reason,
    SqlitePageMetrics Metrics);

public sealed record StateDatabaseMaintenanceResult(
    StateDatabaseMaintenancePlan Plan,
    SqliteMaintenanceDisposition Disposition,
    SqliteMaintenanceReason Reason,
    SqlitePageMetrics BeforePages,
    SqlitePageMetrics AfterPages,
    SqliteStorageSnapshot BeforeStorage,
    SqliteStorageSnapshot AfterStorage,
    SqliteCheckpointResult? Checkpoint)
{
    public string FormatReceipt() => string.Create(
        CultureInfo.InvariantCulture,
        $"STATE_DB_MAINTENANCE decision={Plan.Decision} status={Disposition.ToString().ToLowerInvariant()} reason={Reason} beforePages={BeforePages.PageCount} beforeFreelist={BeforePages.FreelistCount} beforePageSize={BeforePages.PageSize} afterPages={AfterPages.PageCount} afterFreelist={AfterPages.FreelistCount} afterPageSize={AfterPages.PageSize} beforeMain={BeforeStorage.MainDatabaseBytes} beforeWal={BeforeStorage.WalBytes} beforeShm={BeforeStorage.ShmBytes} beforeOther={BeforeStorage.OtherTransientBytes} beforeTotal={BeforeStorage.TotalBytes} afterMain={AfterStorage.MainDatabaseBytes} afterWal={AfterStorage.WalBytes} afterShm={AfterStorage.ShmBytes} afterOther={AfterStorage.OtherTransientBytes} afterTotal={AfterStorage.TotalBytes} checkpointBusy={Checkpoint?.Busy ?? -1} checkpointLogPages={Checkpoint?.LogPages ?? -1} checkpointedPages={Checkpoint?.CheckpointedPages ?? -1}");
}

public static class StateDatabaseMaintenance
{
    public static StateDatabaseMaintenancePlan Plan(
        SqlitePageMetrics metrics,
        bool activeConductor,
        bool activeDispatchOrGate,
        StateDatabaseMaintenanceOptions? options = null)
    {
        options ??= new StateDatabaseMaintenanceOptions();
        if (activeConductor || activeDispatchOrGate)
        {
            return new StateDatabaseMaintenancePlan(
                StateDatabaseMaintenanceDecisionKind.DeferredActiveWork,
                SqliteMaintenanceReason.ActiveWork,
                metrics);
        }

        var ratio = metrics.PageCount == 0 ? 0 : (double)metrics.FreelistCount / metrics.PageCount;
        if (metrics.DatabaseBytes < options.MinimumDatabaseBytes ||
            metrics.ReclaimableBytes < options.MinimumReclaimableBytes ||
            ratio < options.MinimumFreelistRatio)
        {
            return new StateDatabaseMaintenancePlan(
                StateDatabaseMaintenanceDecisionKind.SkippedBelowThreshold,
                SqliteMaintenanceReason.BelowThreshold,
                metrics);
        }

        var now = options.UtcNow ?? DateTimeOffset.UtcNow;
        if (now.DayOfWeek != options.OffPeakDay ||
            now.Hour < options.OffPeakStartHourUtc ||
            now.Hour >= options.OffPeakEndHourUtc)
        {
            return new StateDatabaseMaintenancePlan(
                StateDatabaseMaintenanceDecisionKind.DeferredOutsideWindow,
                SqliteMaintenanceReason.OutsideMaintenanceWindow,
                metrics);
        }

        if (metrics.AutoVacuumMode != 2)
        {
            return new StateDatabaseMaintenancePlan(
                StateDatabaseMaintenanceDecisionKind.OfflineConversionRequired,
                SqliteMaintenanceReason.IncrementalVacuumUnavailable,
                metrics);
        }

        return new StateDatabaseMaintenancePlan(
            StateDatabaseMaintenanceDecisionKind.BoundedIncrementalReclaim,
            SqliteMaintenanceReason.None,
            metrics);
    }

    public static SqlitePageMetrics Probe(string databasePath)
    {
        using var connection = StateDbConnectionFactory.Open(
            databasePath,
            StateDbConnectionProfile.FastFailRead);
        return ReadMetrics(connection);
    }

    public static async Task<StateDatabaseMaintenanceResult> ExecuteAsync(
        string databasePath,
        bool activeDispatchOrGate,
        StateDatabaseMaintenanceOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new StateDatabaseMaintenanceOptions();
        var leasePath = SqliteMaintenanceLease.ForDatabase(databasePath);
        if (!SqliteMaintenanceLease.TryAcquireExclusive(leasePath, out var lease))
        {
            var blockedPages = Probe(databasePath);
            var blockedStorage = SqliteStorageSnapshot.Measure(databasePath);
            var blockedPlan = Plan(blockedPages, activeConductor: true, activeDispatchOrGate, options);
            return new StateDatabaseMaintenanceResult(
                blockedPlan,
                SqliteMaintenanceDisposition.Deferred,
                SqliteMaintenanceReason.ActiveWork,
                blockedPages,
                blockedPages,
                blockedStorage,
                blockedStorage,
                Checkpoint: null);
        }

        using (lease)
        {
            using var connection = StateDbConnectionFactory.Open(
                databasePath,
                StateDbConnectionProfile.ReadWrite,
                busyTimeoutMilliseconds: 0);
            var beforePages = ReadMetrics(connection);
            var beforeStorage = SqliteStorageSnapshot.Measure(databasePath);
            var plan = Plan(beforePages, activeConductor: false, activeDispatchOrGate, options);
            if (plan.Decision is StateDatabaseMaintenanceDecisionKind.DeferredActiveWork or
                StateDatabaseMaintenanceDecisionKind.DeferredOutsideWindow or
                StateDatabaseMaintenanceDecisionKind.SkippedBelowThreshold or
                StateDatabaseMaintenanceDecisionKind.OfflineConversionRequired)
            {
                var earlyDisposition = plan.Decision is StateDatabaseMaintenanceDecisionKind.DeferredActiveWork or
                    StateDatabaseMaintenanceDecisionKind.DeferredOutsideWindow
                    ? SqliteMaintenanceDisposition.Deferred
                    : plan.Decision == StateDatabaseMaintenanceDecisionKind.SkippedBelowThreshold
                        ? SqliteMaintenanceDisposition.Completed
                        : SqliteMaintenanceDisposition.Stalled;
                return new StateDatabaseMaintenanceResult(
                    plan,
                    earlyDisposition,
                    plan.Reason,
                    beforePages,
                    beforePages,
                    beforeStorage,
                    beforeStorage,
                    Checkpoint: null);
            }

            var checkpoint = await ReadCheckpointAsync(connection, cancellationToken).ConfigureAwait(false);
            if (!checkpoint.Completed)
            {
                return new StateDatabaseMaintenanceResult(
                    plan,
                    SqliteMaintenanceDisposition.Deferred,
                    SqliteMaintenanceReason.CheckpointBusy,
                    beforePages,
                    beforePages,
                    beforeStorage,
                    SqliteStorageSnapshot.Measure(databasePath),
                    checkpoint);
            }

            var pages = Math.Min(beforePages.FreelistCount, Math.Max(1, options.MaxIncrementalVacuumPages));
            await using (var reclaim = connection.CreateCommand())
            {
                reclaim.CommandText = $"PRAGMA incremental_vacuum({pages.ToString(CultureInfo.InvariantCulture)})";
                await reclaim.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            checkpoint = await ReadCheckpointAsync(connection, cancellationToken).ConfigureAwait(false);
            var afterPages = ReadMetrics(connection);
            var afterStorage = SqliteStorageSnapshot.Measure(databasePath);
            var grewMaterially = afterStorage.TotalBytes > beforeStorage.TotalBytes + beforePages.PageSize;
            var disposition = grewMaterially
                ? SqliteMaintenanceDisposition.Stalled
                : checkpoint.Completed &&
                    (afterPages.FreelistCount < beforePages.FreelistCount || afterStorage.TotalBytes < beforeStorage.TotalBytes)
                    ? SqliteMaintenanceDisposition.Completed
                    : checkpoint.Completed
                        ? SqliteMaintenanceDisposition.Stalled
                        : SqliteMaintenanceDisposition.Deferred;
            var reason = disposition switch
            {
                SqliteMaintenanceDisposition.Completed => SqliteMaintenanceReason.None,
                SqliteMaintenanceDisposition.Deferred => SqliteMaintenanceReason.CheckpointBusy,
                _ when grewMaterially => SqliteMaintenanceReason.ByteGrowthExceeded,
                _ => SqliteMaintenanceReason.NoProgressLimit
            };
            return new StateDatabaseMaintenanceResult(
                plan,
                disposition,
                reason,
                beforePages,
                afterPages,
                beforeStorage,
                afterStorage,
                checkpoint);
        }
    }

    private static SqlitePageMetrics ReadMetrics(SqliteConnection connection) => new(
        ReadLong(connection, "page_count"),
        ReadLong(connection, "page_size"),
        ReadLong(connection, "freelist_count"),
        StateDbConnectionFactory.ReadJournalMode(connection),
        checked((int)ReadLong(connection, "auto_vacuum")));

    private static long ReadLong(SqliteConnection connection, string pragma)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA " + pragma;
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static async Task<SqliteCheckpointResult> ReadCheckpointAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("SQLite returned no wal_checkpoint receipt.");

        return new SqliteCheckpointResult(
            Convert.ToInt32(reader.GetInt64(0), CultureInfo.InvariantCulture),
            Convert.ToInt32(reader.GetInt64(1), CultureInfo.InvariantCulture),
            Convert.ToInt32(reader.GetInt64(2), CultureInfo.InvariantCulture));
    }
}
