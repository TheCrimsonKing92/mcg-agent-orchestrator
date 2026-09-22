using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

public sealed class StateDatabaseMaintenanceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "mcg-state-maintenance-" + Guid.NewGuid().ToString("N"));

    [Xunit.Fact(DisplayName = "StateDatabaseMaintenance_active_conductor_defers_before_compaction")]
    public async Task ActiveConductorLeaseDefersBeforeCompaction()
    {
        var path = CreateStateDatabase();
        using var activeLease = SqliteMaintenanceLease.AcquireSharedOrThrow(
            SqliteMaintenanceLease.ForDatabase(path));

        var result = await StateDatabaseMaintenance.ExecuteAsync(
            path,
            activeDispatchOrGate: false,
            new StateDatabaseMaintenanceOptions(
                MinimumDatabaseBytes: 1,
                MinimumReclaimableBytes: 1,
                MinimumFreelistRatio: 0,
                MaxIncrementalVacuumPages: 1));

        Assert.Equal(SqliteMaintenanceDisposition.Deferred, result.Disposition);
        Assert.Equal(SqliteMaintenanceReason.ActiveWork, result.Reason);
        Assert.Equal(StateDatabaseMaintenanceDecisionKind.DeferredActiveWork, result.Plan.Decision);
    }

    [Xunit.Fact(DisplayName = "StateDatabaseMaintenance_existing_non_incremental_database_requires_offline_conversion")]
    public void ExistingDatabaseRequiresOfflineConversion()
    {
        var metrics = new SqlitePageMetrics(
            PageCount: 100,
            PageSize: 4096,
            FreelistCount: 50,
            JournalMode: "wal",
            AutoVacuumMode: 0);

        var plan = StateDatabaseMaintenance.Plan(
            metrics,
            activeConductor: false,
            activeDispatchOrGate: false,
            new StateDatabaseMaintenanceOptions(
                MinimumDatabaseBytes: 1,
                MinimumReclaimableBytes: 1,
                MinimumFreelistRatio: 0.1,
                UtcNow: DateTimeOffset.Parse("2026-09-06T03:00:00Z")));

        Assert.Equal(StateDatabaseMaintenanceDecisionKind.OfflineConversionRequired, plan.Decision);
        Assert.Equal(SqliteMaintenanceReason.IncrementalVacuumUnavailable, plan.Reason);
    }

    [Xunit.Fact(DisplayName = "StateDatabaseMaintenance_outside_off_peak_window_is_deferred")]
    public void OutsideOffPeakWindowIsDeferred()
    {
        var metrics = new SqlitePageMetrics(100, 4096, 50, "wal", 2);

        var plan = StateDatabaseMaintenance.Plan(
            metrics,
            activeConductor: false,
            activeDispatchOrGate: false,
            new StateDatabaseMaintenanceOptions(
                MinimumDatabaseBytes: 1,
                MinimumReclaimableBytes: 1,
                MinimumFreelistRatio: 0.1,
                UtcNow: DateTimeOffset.Parse("2026-09-01T12:00:00Z")));

        Assert.Equal(StateDatabaseMaintenanceDecisionKind.DeferredOutsideWindow, plan.Decision);
        Assert.Equal(SqliteMaintenanceReason.OutsideMaintenanceWindow, plan.Reason);
    }

    [Xunit.Fact(DisplayName = "StateDatabaseMaintenance_incremental_reclaim_reports_symmetric_storage_and_page_deltas")]
    public async Task IncrementalReclaimReportsSymmetricStorageAndPageDeltas()
    {
        var path = CreateIncrementalStateDatabase();

        var result = await StateDatabaseMaintenance.ExecuteAsync(
            path,
            activeDispatchOrGate: false,
            new StateDatabaseMaintenanceOptions(
                MinimumDatabaseBytes: 1,
                MinimumReclaimableBytes: 1,
                MinimumFreelistRatio: 0,
                MaxIncrementalVacuumPages: 1,
                UtcNow: DateTimeOffset.Parse("2026-09-06T03:00:00Z")));

        Assert.Equal(StateDatabaseMaintenanceDecisionKind.BoundedIncrementalReclaim, result.Plan.Decision);
        Assert.Equal(SqliteMaintenanceDisposition.Completed, result.Disposition);
        Assert.Equal(SqliteMaintenanceReason.None, result.Reason);
        Assert.True(result.BeforeStorage.ShmBytes > 0);
        Assert.Equal(result.BeforeStorage.ShmBytes, result.AfterStorage.ShmBytes);
        Assert.True(result.AfterPages.PageCount < result.BeforePages.PageCount);
        Assert.True(result.AfterPages.FreelistCount < result.BeforePages.FreelistCount);
        var receipt = result.FormatReceipt();
        Assert.Contains($"beforePages={result.BeforePages.PageCount}", receipt, StringComparison.Ordinal);
        Assert.Contains($"afterPages={result.AfterPages.PageCount}", receipt, StringComparison.Ordinal);
        Assert.Contains($"beforeTotal={result.BeforeStorage.TotalBytes}", receipt, StringComparison.Ordinal);
        Assert.Contains($"afterTotal={result.AfterStorage.TotalBytes}", receipt, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private string CreateStateDatabase()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "state.db");
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL; CREATE TABLE fixture(id INTEGER PRIMARY KEY, value TEXT);";
        command.ExecuteNonQuery();
        return path;
    }

    private string CreateIncrementalStateDatabase()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "incremental-state.db");
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA auto_vacuum=INCREMENTAL;
            VACUUM;
            PRAGMA journal_mode=WAL;
            CREATE TABLE fixture(id INTEGER PRIMARY KEY, value TEXT);
            WITH RECURSIVE rows(id) AS (
                SELECT 1 UNION ALL SELECT id + 1 FROM rows WHERE id < 300
            )
            INSERT INTO fixture(id, value)
            SELECT id, printf('%.*c', 2048, 'x') FROM rows;
            DELETE FROM fixture WHERE id > 25;
            PRAGMA wal_checkpoint(TRUNCATE);
            """;
        command.ExecuteNonQuery();
        return path;
    }
}
