using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class RunEventMaintenanceCadenceConvergenceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "mcg-cadence-convergence-" + Guid.NewGuid().ToString("N"));

    [Xunit.Fact(DisplayName = "RunEventMaintenanceCadence_incomplete_receipt_schedules_bounded_continuation")]
    public void IncompleteReceiptSchedulesBoundedContinuation()
    {
        Directory.CreateDirectory(_root);
        var databasePath = Path.Combine(_root, "run-events.db");
        var logPath = Path.Combine(_root, "conduct-events.log");
        var now = DateTimeOffset.Parse("2026-09-01T12:00:00Z");
        var calls = 0;
        var storage = new SqliteStorageSnapshot(4096, 0, 0, 0);

        RunEventMaintenanceResult Operation(SqliteRunEventStore _, RunEventMaintenanceOptions __)
        {
            calls++;
            return new RunEventMaintenanceResult(
                Deferred: false,
                DeferredReason: null,
                ConductorTickRowsDeleted: 0,
                AgedConductorTickRowsDeleted: 0,
                OversizedConductorTickRowsDeleted: 0,
                DeletedPayloadBytesEstimate: 0,
                MaxRowsDeletedInTransaction: 0,
                Duration: TimeSpan.Zero,
                BytesBefore: storage.TotalBytes,
                BytesAfter: storage.TotalBytes,
                VacuumRequested: false,
                VacuumCompleted: false,
                VacuumDeferred: false,
                Disposition: SqliteMaintenanceDisposition.Incomplete,
                Reason: SqliteMaintenanceReason.WorkCapReached,
                StorageBefore: storage,
                StorageAfterMutation: storage,
                StorageAfterConvergence: storage,
                Checkpoint: new SqliteCheckpointResult(0, 0, 0),
                RemainingEligibleRows: 1,
                NextAttemptAt: now.AddMinutes(5));
        }

        Assert.True(RunEventMaintenanceCadence.TryRunIfDue(
            databasePath, logPath, () => now, Operation).Attempted);
        Assert.True(RunEventMaintenanceCadence.TryRunIfDue(
            databasePath, logPath, () => now.AddMinutes(1), Operation).Skipped);
        Assert.True(RunEventMaintenanceCadence.TryRunIfDue(
            databasePath, logPath, () => now.AddMinutes(6), Operation).Attempted);
        Assert.Equal(2, calls);
    }

    [Xunit.Fact]
    public void PersistentlyDeferredMaintenanceBacksOffThenEscalatesWithoutUnboundedReceipts()
    {
        Directory.CreateDirectory(_root);
        var databasePath = Path.Combine(_root, "deferred-run-events.db");
        var logPath = Path.Combine(_root, "deferred-conduct-events.log");
        var now = DateTimeOffset.Parse("2026-09-01T12:00:00Z");
        var calls = 0;
        var storage = new SqliteStorageSnapshot(4096, 0, 0, 0);

        RunEventMaintenanceResult Operation(SqliteRunEventStore _, RunEventMaintenanceOptions options)
        {
            calls++;
            return new RunEventMaintenanceResult(
                Deferred: true,
                DeferredReason: "database-busy",
                ConductorTickRowsDeleted: 0,
                AgedConductorTickRowsDeleted: 0,
                OversizedConductorTickRowsDeleted: 0,
                DeletedPayloadBytesEstimate: 0,
                MaxRowsDeletedInTransaction: 0,
                Duration: TimeSpan.Zero,
                BytesBefore: storage.TotalBytes,
                BytesAfter: storage.TotalBytes,
                VacuumRequested: false,
                VacuumCompleted: false,
                VacuumDeferred: false,
                Disposition: SqliteMaintenanceDisposition.Deferred,
                Reason: SqliteMaintenanceReason.DatabaseBusy,
                StorageBefore: storage,
                StorageAfterMutation: storage,
                StorageAfterConvergence: storage,
                RemainingBytesOverBudget: 1,
                NextAttemptAt: now.AddMinutes(5),
                ConsecutiveNoProgressAttempts: options.ConsecutiveNoProgressAttempts);
        }

        var first = RunEventMaintenanceCadence.TryRunIfDue(databasePath, logPath, () => now, Operation);
        var second = RunEventMaintenanceCadence.TryRunIfDue(databasePath, logPath, () => now.AddMinutes(6), Operation);
        var third = RunEventMaintenanceCadence.TryRunIfDue(databasePath, logPath, () => now.AddMinutes(16), Operation);
        var fourth = RunEventMaintenanceCadence.TryRunIfDue(databasePath, logPath, () => now.AddMinutes(30), Operation);

        Assert.Equal(SqliteMaintenanceDisposition.Deferred, first.Maintenance?.Disposition);
        Assert.Equal(SqliteMaintenanceDisposition.Deferred, second.Maintenance?.Disposition);
        Assert.Equal(SqliteMaintenanceDisposition.Stalled, third.Maintenance?.Disposition);
        Assert.Equal("DatabaseBusyLimit", third.Reason);
        Assert.True(fourth.Skipped);
        Assert.Equal(3, calls);
        Assert.Equal(3, new SqliteRunEventStore(databasePath, ensureSchema: false)
            .ReadSinceAsync(maxCount: 100)
            .GetAwaiter().GetResult()
            .Count(record => record.EventType == RunEventTypes.RunEventMaintenance));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
