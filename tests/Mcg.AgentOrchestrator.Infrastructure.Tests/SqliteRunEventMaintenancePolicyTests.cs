using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

public sealed class SqliteRunEventMaintenancePolicyTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "mcg-run-event-maintenance-" + Guid.NewGuid().ToString("N"));

    [Xunit.Fact]
    public async Task BytePressureUsesPostCheckpointStorageAndCheckpointAloneRelievesBudget()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "checkpoint-relief.db");
        var now = DateTimeOffset.Parse("2026-09-01T12:00:00Z");
        var store = new SqliteRunEventStore(path);
        await AppendGoalOperationAsync(store, "eligible", now.AddDays(-10));

        await using var writer = new SqliteConnection($"Data Source={path};Mode=ReadWrite;Pooling=False");
        await writer.OpenAsync();
        await using (var disableAutoCheckpoint = writer.CreateCommand())
        {
            disableAutoCheckpoint.CommandText = "PRAGMA wal_autocheckpoint=0";
            await disableAutoCheckpoint.ExecuteNonQueryAsync();
        }

        for (var index = 0; index < 96; index++)
        {
            await using var update = writer.CreateCommand();
            update.CommandText = "UPDATE run_events SET payload_json = $payload WHERE goal_id = 'eligible'";
            update.Parameters.AddWithValue("$payload", index.ToString("D3") + new string('w', 16 * 1024));
            await update.ExecuteNonQueryAsync();
        }

        var before = SqliteStorageSnapshot.Measure(path);
        var budget = before.MainDatabaseBytes + before.ShmBytes + (64 * 1024);
        Assert.True(before.WalBytes > 64 * 1024, $"Fixture WAL was only {before.WalBytes} bytes.");
        Assert.True(before.TotalBytes > budget);

        var result = await store.MaintainAsync(RunEventMaintenanceOptions.Default with
        {
            UtcNow = now,
            TerminalGoalIds = ["eligible"],
            TerminalGoalOperationMaxAge = TimeSpan.FromDays(30),
            RecentTerminalGoalProtectionAge = TimeSpan.FromDays(7),
            MaxDatabaseBytes = budget,
            MaxDeleteBatchesPerPass = 1
        });

        Assert.True(result.Checkpoint?.Completed);
        Assert.True(result.StorageAfterConvergence?.TotalBytes <= budget);
        Assert.Equal(0, result.TerminalGoalOperationRowsDeleted);
        Assert.Single(await store.ReadSinceAsync(goalId: "eligible"));
        Assert.Equal(SqliteMaintenanceDisposition.Completed, result.Disposition);
    }

    [Xunit.Fact]
    public async Task BytePressure_PrunesOldRowsAndPreservesRecentRowsAcrossPasses()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "byte-pressure.db");
        var now = DateTimeOffset.Parse("2026-09-01T12:00:00Z");
        var store = new SqliteRunEventStore(path);
        for (var index = 0; index < 10; index++)
            await AppendGoalOperationAsync(store, "terminal", now.AddDays(-10).AddMinutes(index), 128 * 1024);
        var recentTimes = new[] { now.AddDays(-2), now.AddDays(-1) };
        foreach (var occurredAt in recentTimes)
            await AppendGoalOperationAsync(store, "terminal", occurredAt, 128 * 1024);

        await using (var checkpoint = new SqliteConnection($"Data Source={path};Mode=ReadWrite;Pooling=False"))
        {
            await checkpoint.OpenAsync();
            await using var command = checkpoint.CreateCommand();
            command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
            await command.ExecuteNonQueryAsync();
        }

        var before = SqliteStorageSnapshot.Measure(path);
        var budget = before.TotalBytes - (600 * 1024);
        Assert.True(budget > 0);
        Assert.True(before.TotalBytes > budget);
        Assert.Equal(0, before.WalBytes);

        var options = RunEventMaintenanceOptions.Default with
        {
            UtcNow = now,
            TerminalGoalIds = ["terminal"],
            TerminalGoalOperationMaxAge = TimeSpan.FromDays(30),
            RecentTerminalGoalProtectionAge = TimeSpan.FromDays(7),
            MaxDatabaseBytes = budget,
            DeleteBatchSize = 2,
            MaxDeleteBatchesPerPass = 1,
            MaxIncrementalVacuumPagesPerPass = 64
        };

        RunEventMaintenanceResult? result = null;
        var passes = 0;
        do
        {
            result = await store.MaintainAsync(options);
            passes++;
        }
        while (result.Disposition == SqliteMaintenanceDisposition.Incomplete && passes < 10);

        Assert.NotNull(result);
        Assert.True(passes > 1, $"Fixture converged in one pass: {result}.");
        Assert.True(passes < 10, $"Fixture did not converge within the bounded pass count: {result}.");
        Assert.Equal(SqliteMaintenanceDisposition.Completed, result.Disposition);
        Assert.Equal(0, result.RemainingBytesOverBudget);

        var remaining = await store.ReadSinceAsync(goalId: "terminal");
        Assert.True(remaining.Count < 12, "Expected byte pressure to delete at least one older terminal row.");
        Assert.All(recentTimes, expected => Assert.Contains(remaining, row => row.OccurredAt == expected));
        Assert.DoesNotContain(remaining, row => row.OccurredAt == now.AddDays(-10));
    }

    [Xunit.Fact]
    public async Task Vacuum_BusyConvergenceCheckpointCannotReportCompleted()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "vacuum-busy-checkpoint.db");
        var now = DateTimeOffset.Parse("2026-09-01T12:00:00Z");
        var store = new SqliteRunEventStore(path);
        await AppendGoalOperationAsync(store, "protected", now, 1024);
        for (var index = 0; index < 48; index++)
            await AppendGoalOperationAsync(store, "deleted", now.AddDays(-40), 128 * 1024);

        await using (var preparation = new SqliteConnection($"Data Source={path};Mode=ReadWrite;Pooling=False"))
        {
            await preparation.OpenAsync();
            await using (var journalMode = preparation.CreateCommand())
            {
                journalMode.CommandText = "PRAGMA journal_mode";
                Assert.Equal("wal", Convert.ToString(await journalMode.ExecuteScalarAsync()));
            }

            await using (var delete = preparation.CreateCommand())
            {
                delete.CommandText = "DELETE FROM run_events WHERE goal_id = 'deleted'";
                Assert.Equal(48, await delete.ExecuteNonQueryAsync());
            }

            await using (var checkpoint = preparation.CreateCommand())
            {
                checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
                await checkpoint.ExecuteNonQueryAsync();
            }

            await using var freelist = preparation.CreateCommand();
            freelist.CommandText = "PRAGMA freelist_count";
            Assert.True(Convert.ToInt64(await freelist.ExecuteScalarAsync()) > 100);
        }

        var before = SqliteStorageSnapshot.Measure(path);
        Assert.Equal(0, before.WalBytes);

        SqliteConnection? blocker = null;
        SqliteTransaction? transaction = null;
        SqliteCommand? command = null;
        SqliteDataReader? reader = null;
        store = new SqliteRunEventStore(path, async cancellationToken =>
        {
            blocker = new SqliteConnection($"Data Source={path};Mode=ReadWrite;Pooling=False");
            await blocker.OpenAsync(cancellationToken);
            transaction = blocker.BeginTransaction(deferred: true);
            command = blocker.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT seq FROM run_events WHERE goal_id = 'protected'";
            reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                throw new InvalidOperationException("The protected-row reader did not acquire its WAL snapshot.");
        });

        try
        {
            var result = await store.MaintainAsync(RunEventMaintenanceOptions.Default with
            {
                UtcNow = now,
                TerminalGoalIds = [],
                MaxDatabaseBytes = long.MaxValue,
                Vacuum = true,
                OfflineVacuumAuthorized = true,
                MaxNoProgressAttempts = 3
            });

            Assert.True(result.VacuumRequested);
            Assert.False(result.VacuumCompleted);
            Assert.True(result.VacuumDeferred);
            Assert.NotNull(result.StorageAfterMutation);
            Assert.True(result.StorageAfterMutation.WalBytes > 0);
            Assert.NotNull(result.Checkpoint);
            Assert.False(result.Checkpoint.Completed);
            Assert.Equal(SqliteMaintenanceDisposition.Incomplete, result.Disposition);
            Assert.Equal(SqliteMaintenanceReason.CheckpointBusy, result.Reason);
            Assert.NotNull(result.NextAttemptAt);
        }
        finally
        {
            if (reader is not null)
                await reader.DisposeAsync();
            command?.Dispose();
            transaction?.Dispose();
            if (blocker is not null)
                await blocker.DisposeAsync();
        }
    }

    [Xunit.Theory]
    [Xunit.InlineData(-1)]
    [Xunit.InlineData(0)]
    [Xunit.InlineData(6)]
    [Xunit.InlineData(31)]
    public async Task RecentTerminalProtectionRejectsUnsafeDurations(int protectionDays)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, $"unsafe-protection-{protectionDays}.db");
        var store = new SqliteRunEventStore(path);

        await Assert.ThrowsAnyAsync<ArgumentException>(() => store.MaintainAsync(
            RunEventMaintenanceOptions.Default with
            {
                TerminalGoalOperationMaxAge = TimeSpan.FromDays(30),
                RecentTerminalGoalProtectionAge = TimeSpan.FromDays(protectionDays)
            }));
    }

    [Xunit.Fact]
    public void ByteWorkPendingWhenCapIsExhaustedRemainsResumable()
    {
        var storage = new SqliteStorageSnapshot(10_000, 0, 0, 0);
        var continuation = DateTimeOffset.Parse("2026-09-01T12:05:00Z");

        var decision = SqliteMaintenanceClassifier.Classify(
            storage,
            storage,
            new SqliteCheckpointResult(0, 0, 0),
            remainingEligibleRows: 0,
            remainingBytesOverBudget: 1000,
            noProgressAttempts: 0,
            maxNoProgressAttempts: 3,
            autoVacuumMode: 2,
            freelistCount: 0,
            vacuumDeferred: false,
            materialGrowthToleranceBytes: 4096,
            continuationDue: continuation,
            workCapReachedWithByteWorkPending: true);

        Assert.Equal(SqliteMaintenanceDisposition.Incomplete, decision.Disposition);
        Assert.Equal(SqliteMaintenanceReason.WorkCapReached, decision.Reason);
        Assert.Equal(continuation, decision.NextAttemptAt);
    }

    [Xunit.Fact]
    public async Task CappedIncrementalReclaimReturnsIncompleteAndConvergesOnContinuation()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "capped-incremental-reclaim.db");
        var now = DateTimeOffset.Parse("2026-09-01T12:00:00Z");
        var store = new SqliteRunEventStore(path);
        await AppendGoalOperationAsync(store, "protected", now, 1024);
        for (var index = 0; index < 32; index++)
            await AppendGoalOperationAsync(store, "reclaimable", now.AddDays(-40), 64 * 1024);

        long pageSize;
        long freelistCount;
        await using (var preparation = new SqliteConnection($"Data Source={path};Mode=ReadWrite;Pooling=False"))
        {
            await preparation.OpenAsync();
            await using (var delete = preparation.CreateCommand())
            {
                delete.CommandText = "DELETE FROM run_events WHERE goal_id = 'reclaimable'";
                Assert.Equal(32, await delete.ExecuteNonQueryAsync());
            }

            await using (var checkpoint = preparation.CreateCommand())
            {
                checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
                await checkpoint.ExecuteNonQueryAsync();
            }

            await using (var pageSizeCommand = preparation.CreateCommand())
            {
                pageSizeCommand.CommandText = "PRAGMA page_size";
                pageSize = Convert.ToInt64(await pageSizeCommand.ExecuteScalarAsync());
            }

            await using var freelistCommand = preparation.CreateCommand();
            freelistCommand.CommandText = "PRAGMA freelist_count";
            freelistCount = Convert.ToInt64(await freelistCommand.ExecuteScalarAsync());
        }

        Assert.True(freelistCount > 4, $"Fixture created only {freelistCount} reclaimable pages.");
        await using var keeper = new SqliteConnection($"Data Source={path};Mode=ReadWrite;Pooling=False");
        await keeper.OpenAsync();
        await using (var establishWalSnapshot = keeper.CreateCommand())
        {
            establishWalSnapshot.CommandText = "SELECT COUNT(*) FROM run_events";
            Assert.Equal(1L, Convert.ToInt64(await establishWalSnapshot.ExecuteScalarAsync()));
        }
        var before = SqliteStorageSnapshot.Measure(path);
        Assert.True(before.ShmBytes > 0);
        var options = RunEventMaintenanceOptions.Default with
        {
            UtcNow = now,
            TerminalGoalIds = [],
            MaxDatabaseBytes = before.TotalBytes - (2 * pageSize),
            MaxIncrementalVacuumPagesPerPass = 1,
            ConsecutiveNoProgressAttempts = 2,
            MaxNoProgressAttempts = 3,
            ContinuationDelay = TimeSpan.FromMinutes(3)
        };

        var first = await store.MaintainAsync(options);

        Assert.True(first.StorageAfterConvergence?.MainDatabaseBytes < before.MainDatabaseBytes, $"Expected incremental reclaim progress: {first}.");
        Assert.Equal(SqliteMaintenanceDisposition.Incomplete, first.Disposition);
        Assert.Equal(SqliteMaintenanceReason.WorkCapReached, first.Reason);
        Assert.Equal(now.AddMinutes(3), first.NextAttemptAt);
        Assert.Equal(0, first.ConsecutiveNoProgressAttempts);
        Assert.True(first.RemainingBytesOverBudget > 0);

        RunEventMaintenanceResult current = first;
        for (var pass = 0; pass < 4 && current.Disposition == SqliteMaintenanceDisposition.Incomplete; pass++)
        {
            options = options with
            {
                ConsecutiveNoProgressAttempts = current.ConsecutiveNoProgressAttempts
            };
            current = await store.MaintainAsync(options);
        }

        Assert.Equal(SqliteMaintenanceDisposition.Completed, current.Disposition);
        Assert.Equal(0, current.RemainingBytesOverBudget);
        Assert.Single(await store.ReadSinceAsync(goalId: "protected"));
    }

    [Xunit.Fact(DisplayName = "RunEventMaintenance_capped_pass_returns_incomplete_and_converges_after_restart")]
    public async Task CappedPassReturnsIncompleteAndContinuation()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "run-events.db");
        var now = DateTimeOffset.Parse("2026-09-01T12:00:00Z");
        var store = new SqliteRunEventStore(path);
        for (var index = 0; index < 5; index++)
            await AppendGoalOperationAsync(store, "eligible", now.AddDays(-40).AddMinutes(index));
        await AppendGoalOperationAsync(store, "eligible", now.AddDays(-1));
        await AppendGoalOperationAsync(store, "protected", now.AddDays(-90));

        var options = RunEventMaintenanceOptions.Default with
        {
            UtcNow = now,
            TerminalGoalIds = ["eligible"],
            DeleteBatchSize = 2,
            MaxDeleteBatchesPerPass = 1,
            MaxDatabaseBytes = long.MaxValue,
            ContinuationDelay = TimeSpan.FromMinutes(3)
        };

        var first = await store.MaintainAsync(options);

        Assert.True(
            first.Disposition == SqliteMaintenanceDisposition.Incomplete,
            $"Expected Incomplete; actual={first.Disposition}; reason={first.Reason}; before={first.StorageBefore}; after={first.StorageAfterConvergence}; checkpoint={first.Checkpoint}; remainingRows={first.RemainingEligibleRows}; remainingBytes={first.RemainingBytesOverBudget}");
        Assert.Equal(SqliteMaintenanceReason.WorkCapReached, first.Reason);
        Assert.NotNull(first.StorageBefore);
        Assert.NotNull(first.StorageAfterConvergence);
        Assert.True(first.StorageBefore.ShmBytes > 0);
        Assert.Equal(first.StorageBefore.ShmBytes, first.StorageAfterConvergence.ShmBytes);
        Assert.Equal(2, first.TerminalGoalOperationRowsDeleted);
        Assert.True(first.RemainingEligibleRows > 0);
        Assert.Equal(now.AddMinutes(3), first.NextAttemptAt);

        RunEventMaintenanceResult current = first;
        for (var pass = 0; pass < 4 && current.Disposition == SqliteMaintenanceDisposition.Incomplete; pass++)
        {
            store = new SqliteRunEventStore(path);
            current = await store.MaintainAsync(options);
        }

        Assert.Equal(SqliteMaintenanceDisposition.Completed, current.Disposition);
        var eligible = await store.ReadSinceAsync(goalId: "eligible");
        Assert.Single(eligible);
        Assert.Equal(now.AddDays(-1), eligible[0].OccurredAt);
        Assert.Single(await store.ReadSinceAsync(goalId: "protected"));
    }

    [Xunit.Fact(DisplayName = "SqliteMaintenance_growth_cannot_be_classified_as_completed")]
    public void ByteGrowthCannotBeClassifiedAsCompleted()
    {
        var before = new SqliteStorageSnapshot(1000, 0, 0, 0);
        var after = new SqliteStorageSnapshot(1000, 500, 0, 0);

        var decision = SqliteMaintenanceClassifier.Classify(
            before,
            after,
            new SqliteCheckpointResult(0, 0, 0),
            remainingEligibleRows: 0,
            remainingBytesOverBudget: 0,
            noProgressAttempts: 0,
            maxNoProgressAttempts: 3,
            autoVacuumMode: 2,
            freelistCount: 0,
            vacuumDeferred: false,
            materialGrowthToleranceBytes: 4096,
            continuationDue: DateTimeOffset.Parse("2026-09-01T12:05:00Z"));

        Assert.Equal(SqliteMaintenanceDisposition.Completed, decision.Disposition);

        decision = SqliteMaintenanceClassifier.Classify(
            before,
            after with { WalBytes = 5000 },
            new SqliteCheckpointResult(0, 0, 0),
            0,
            0,
            0,
            3,
            2,
            0,
            false,
            4096,
            DateTimeOffset.Parse("2026-09-01T12:05:00Z"));

        Assert.Equal(SqliteMaintenanceDisposition.Stalled, decision.Disposition);
        Assert.Equal(SqliteMaintenanceReason.ByteGrowthExceeded, decision.Reason);
    }

    [Xunit.Fact(DisplayName = "SqliteMaintenance_repeated_no_progress_stalls_with_typed_reason")]
    public void RepeatedNoProgressStallsWithTypedReason()
    {
        var storage = new SqliteStorageSnapshot(10_000, 0, 0, 0);
        var decision = SqliteMaintenanceClassifier.Classify(
            storage,
            storage,
            new SqliteCheckpointResult(0, 0, 0),
            remainingEligibleRows: 0,
            remainingBytesOverBudget: 1000,
            noProgressAttempts: 3,
            maxNoProgressAttempts: 3,
            autoVacuumMode: 2,
            freelistCount: 0,
            vacuumDeferred: false,
            materialGrowthToleranceBytes: 4096,
            continuationDue: DateTimeOffset.Parse("2026-09-01T12:05:00Z"));

        Assert.Equal(SqliteMaintenanceDisposition.Stalled, decision.Disposition);
        Assert.Equal(SqliteMaintenanceReason.NoProgressLimit, decision.Reason);
    }

    [Xunit.Fact(DisplayName = "SqliteMaintenance_repeated_busy_checkpoint_escalates_instead_of_spinning")]
    public void RepeatedBusyCheckpointEscalatesInsteadOfSpinning()
    {
        var storage = new SqliteStorageSnapshot(10_000, 0, 0, 0);

        var decision = SqliteMaintenanceClassifier.Classify(
            storage,
            storage,
            new SqliteCheckpointResult(1, 20, 0),
            remainingEligibleRows: 1,
            remainingBytesOverBudget: 1000,
            noProgressAttempts: 3,
            maxNoProgressAttempts: 3,
            autoVacuumMode: 2,
            freelistCount: 10,
            vacuumDeferred: false,
            materialGrowthToleranceBytes: 4096,
            continuationDue: DateTimeOffset.Parse("2026-09-01T12:05:00Z"));

        Assert.Equal(SqliteMaintenanceDisposition.Stalled, decision.Disposition);
        Assert.Equal(SqliteMaintenanceReason.CheckpointBusyLimit, decision.Reason);
    }

    [Xunit.Fact]
    public async Task BusyCheckpointUnderBudgetEscalatesAcrossCalls()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "busy-checkpoint.db");
        var store = new SqliteRunEventStore(path);
        var now = DateTimeOffset.Parse("2026-09-01T12:00:00Z");
        await AppendGoalOperationAsync(store, "protected", now);

        await using var blocker = new SqliteConnection($"Data Source={path};Mode=ReadWrite;Pooling=False");
        await blocker.OpenAsync();
        await using var transaction = blocker.BeginTransaction(deferred: true);
        await using var command = blocker.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = "SELECT seq FROM run_events ORDER BY seq LIMIT 1";
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());

        await AppendGoalOperationAsync(store, "protected", now.AddMinutes(1));

        var options = RunEventMaintenanceOptions.Default with
        {
            UtcNow = now,
            TerminalGoalIds = [],
            MaxDatabaseBytes = long.MaxValue,
            MaxNoProgressAttempts = 3
        };
        RunEventMaintenanceResult? result = null;
        for (var attempt = 1; attempt <= options.MaxNoProgressAttempts; attempt++)
        {
            result = await store.MaintainAsync(options);
            Assert.NotNull(result.Checkpoint);
            Assert.False(result.Checkpoint.Completed);
            Assert.Equal(attempt, result.ConsecutiveNoProgressAttempts);
            options = options with
            {
                ConsecutiveNoProgressAttempts = result.ConsecutiveNoProgressAttempts
            };
        }

        Assert.NotNull(result);
        Assert.Equal(SqliteMaintenanceDisposition.Stalled, result.Disposition);
        Assert.Equal(SqliteMaintenanceReason.CheckpointBusyLimit, result.Reason);
        Assert.Equal(0, result.RemainingBytesOverBudget);
    }

    [Xunit.Fact]
    public async Task CompletedCheckpointUnderBudgetResetsCounter()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "completed-checkpoint.db");
        var store = new SqliteRunEventStore(path);
        var now = DateTimeOffset.Parse("2026-09-01T12:00:00Z");
        await AppendGoalOperationAsync(store, "protected", now);

        var result = await store.MaintainAsync(RunEventMaintenanceOptions.Default with
        {
            UtcNow = now,
            TerminalGoalIds = [],
            MaxDatabaseBytes = long.MaxValue,
            ConsecutiveNoProgressAttempts = 2,
            MaxNoProgressAttempts = 3
        });

        Assert.NotNull(result.Checkpoint);
        Assert.True(result.Checkpoint.Completed);
        Assert.Equal(SqliteMaintenanceDisposition.Completed, result.Disposition);
        Assert.Equal(SqliteMaintenanceReason.None, result.Reason);
        Assert.Equal(0, result.ConsecutiveNoProgressAttempts);
        Assert.Equal(0, result.RemainingBytesOverBudget);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private static Task<RunEventRecord> AppendGoalOperationAsync(
        SqliteRunEventStore store,
        string goalId,
        DateTimeOffset occurredAt,
        int payloadBytes = 512) =>
        store.AppendAsync(new RunEventAppend(
            RunEventTypes.GoalOperation,
            goalId,
            "conductor:test",
            "Completed",
            "fixture",
            new string('x', payloadBytes),
            OccurredAt: occurredAt));
}
