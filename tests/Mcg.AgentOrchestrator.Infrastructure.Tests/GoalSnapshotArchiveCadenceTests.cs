using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each cadence owns its workspace, stores and journal; all clocks and sweeps are injected.
public sealed class GoalSnapshotArchiveCadenceTests
{
    private static readonly DateTimeOffset Now = new(2020, 3, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter() }
    };
    private static readonly RunEventMaintenanceResult CompletedMaintenance = new(
        Deferred: false, DeferredReason: null, ConductorTickRowsDeleted: 0,
        AgedConductorTickRowsDeleted: 0, OversizedConductorTickRowsDeleted: 0,
        DeletedPayloadBytesEstimate: 0, MaxRowsDeletedInTransaction: 0, Duration: TimeSpan.Zero,
        BytesBefore: 0, BytesAfter: 0, VacuumRequested: false, VacuumCompleted: false, VacuumDeferred: false);
    private static readonly StorageRetentionResult SuccessfulSweep = new(0, 0, 0, 0, 0, 0, "fixture-sweep", []);

    [Xunit.Fact]
    public void Run_ArchiveSucceeds_UsesExactCutoffAndCarriesCount()
    {
        var now = Now.ToOffset(TimeSpan.FromHours(5)).AddTicks(123);
        DateTimeOffset? observedCutoff = null;
        var calls = 0;

        var result = GoalSnapshotArchiveCadence.Run(cutoff =>
        {
            calls++;
            observedCutoff = cutoff;
            return 17;
        }, now);

        Assert.Equal(1, calls);
        Assert.Equal(now - TimeSpan.FromDays(14), observedCutoff);
        Assert.Equal(observedCutoff, result.Cutoff);
        Assert.Equal(17, result.Archived);
        Assert.Equal("completed", result.Status);
        Assert.Null(result.ExceptionType);
        Assert.Equal(
            $"GOAL_SNAPSHOT_ARCHIVE status=completed archived=17 cutoff={result.Cutoff.ToString("O", CultureInfo.InvariantCulture)} ageDays=14",
            GoalSnapshotArchiveCadence.Format(result));
    }

    [Xunit.Fact]
    public void Run_ArchiveThrows_ReturnsFailureWithExceptionType()
    {
        var result = GoalSnapshotArchiveCadence.Run(_ => throw new InvalidOperationException("fixture failure"), Now);

        Assert.Equal(Now.AddDays(-14), result.Cutoff);
        Assert.Equal(0, result.Archived);
        Assert.Equal("failed", result.Status);
        Assert.Equal(nameof(InvalidOperationException), result.ExceptionType);
        Assert.Equal(
            "GOAL_SNAPSHOT_ARCHIVE status=failed archived=0 cutoff=2020-02-16T12:00:00.0000000+00:00 ageDays=14 exception=InvalidOperationException",
            GoalSnapshotArchiveCadence.Format(result));
    }

    [Xunit.Fact]
    public void Run_ArchiveCancels_PropagatesOriginalException()
    {
        var exception = new OperationCanceledException("fixture cancellation");

        Assert.Same(exception, Assert.Throws<OperationCanceledException>(() =>
            GoalSnapshotArchiveCadence.Run(_ => throw exception, Now)));
    }

    [Xunit.Fact]
    public void Run_TaskCancels_PropagatesOriginalException()
    {
        var exception = new TaskCanceledException("fixture task cancellation");

        Assert.Same(exception, Assert.Throws<TaskCanceledException>(() =>
            GoalSnapshotArchiveCadence.Run(_ => throw exception, Now)));
    }

    [Xunit.Fact]
    public void DueWorkspace_Run_ArchivesOnceAfterSweepReceipt()
    {
        using var fixture = new Fixture();
        var calls = 0;
        DateTimeOffset? observedCutoff = null;
        RunEventRecord? sweepAtArchiveCall = null;

        var result = fixture.Run(Now, cutoff =>
        {
            calls++;
            observedCutoff = cutoff;
            sweepAtArchiveCall = fixture.ReadSweepReceipt();
            return 3;
        });

        Assert.True(result.Attempted);
        Assert.False(result.Failed);
        Assert.Equal(1, calls);
        Assert.Equal(Now.AddDays(-14), observedCutoff);
        Assert.NotNull(sweepAtArchiveCall);
        Assert.Equal(RunEventMaintenanceCadence.FormatArtifactRetentionReceipt(SuccessfulSweep), sweepAtArchiveCall.Detail);
        var line = Assert.Single(File.ReadAllLines(fixture.JournalPath), line => line.Contains("GOAL_SNAPSHOT_ARCHIVE"));
        using var record = JsonDocument.Parse(line);
        Assert.Equal("goal-snapshot-archive", record.RootElement.GetProperty("eventKind").GetString());
        Assert.Equal("GOAL_SNAPSHOT_ARCHIVE status=completed archived=3 cutoff=2020-02-16T12:00:00.0000000+00:00 ageDays=14",
            record.RootElement.GetProperty("detail").GetString());
    }

    [Xunit.Fact]
    public void WithinDailyWindow_SecondCall_DoesNotArchiveAgain()
    {
        using var fixture = new Fixture();
        var calls = 0;
        int Archive(DateTimeOffset _) { calls++; return 0; }

        Assert.True(fixture.Run(Now, Archive).Attempted);
        var second = fixture.Run(Now.AddHours(1), Archive);

        Assert.False(second.Attempted);
        Assert.True(second.Skipped);
        Assert.Equal(1, calls);
        Assert.Single(File.ReadAllLines(fixture.JournalPath), line => line.Contains("GOAL_SNAPSHOT_ARCHIVE"));
    }

    [Xunit.Fact]
    public void NoWorkspace_DueRun_DoesNotInvokeArchive()
    {
        using var fixture = new Fixture();
        var calls = 0;

        var result = RunEventMaintenanceCadence.TryRunIfDue(
            fixture.RunEventsPath, fixture.JournalPath, () => Now,
            maintenanceOperation: (_, _) => CompletedMaintenance,
            goalSnapshotArchiveOperation: _ => { calls++; return 0; });

        Assert.True(result.Attempted);
        Assert.False(result.Failed);
        Assert.Null(result.ArtifactRetention);
        Assert.Equal(0, calls);
        Assert.DoesNotContain("GOAL_SNAPSHOT_ARCHIVE", File.ReadAllText(fixture.JournalPath));
    }

    [Xunit.Fact]
    public void FreshSweep_MaintenanceContinuation_DoesNotArchive()
    {
        using var fixture = new Fixture();
        var calls = 0;
        int Archive(DateTimeOffset _) { calls++; return 0; }
        var continuation = CompletedMaintenance with
        {
            Disposition = SqliteMaintenanceDisposition.Incomplete,
            NextAttemptAt = Now.AddMinutes(5)
        };

        Assert.True(fixture.Run(Now, Archive, maintenance: continuation).Attempted);
        var second = fixture.Run(Now.AddMinutes(5), Archive);

        Assert.True(second.Attempted);
        Assert.Null(second.ArtifactRetention);
        Assert.Equal(1, calls);
    }

    [Xunit.Fact]
    public void ArchiveThrows_PreservesCadenceSweepReceiptAndDailySchedule()
    {
        using var success = new Fixture();
        using var failure = new Fixture();
        var succeeded = success.Run(Now, _ => 2);
        var calls = 0;
        RunEventRecord? sweepAtArchiveCall = null;
        var failed = failure.Run(Now, _ =>
        {
            calls++;
            sweepAtArchiveCall = failure.ReadSweepReceipt();
            throw new InvalidOperationException("fixture failure");
        });

        Assert.True(succeeded.Attempted);
        Assert.False(succeeded.Failed);
        Assert.Null(succeeded.Reason);
        Assert.Equal(1, calls);
        Assert.Equal(succeeded.Attempted, failed.Attempted);
        Assert.Equal(succeeded.Failed, failed.Failed);
        Assert.Equal(succeeded.Reason, failed.Reason);
        Assert.Same(SuccessfulSweep, succeeded.ArtifactRetention);
        Assert.Same(SuccessfulSweep, failed.ArtifactRetention);
        var successReceipt = Assert.IsType<RunEventRecord>(success.ReadSweepReceipt());
        var failureReceipt = Assert.IsType<RunEventRecord>(failure.ReadSweepReceipt());
        Assert.Equal(failureReceipt, sweepAtArchiveCall);
        // Only store-assigned identity differs between these otherwise identical receipts.
        Assert.Equal(successReceipt with { Sequence = 0, EventId = "" },
            failureReceipt with { Sequence = 0, EventId = "" });
        foreach (var fixture in new[] { success, failure })
        {
            Assert.True(RunEventMaintenanceCadence.IsKnownFresh(fixture.RunEventsPath, Now.AddHours(23)));
            Assert.False(RunEventMaintenanceCadence.IsKnownFresh(fixture.RunEventsPath, Now.AddHours(24)));
        }
        var journal = File.ReadAllLines(failure.JournalPath);
        var line = Assert.Single(journal, line => line.Contains("GOAL_SNAPSHOT_ARCHIVE"));
        using var record = JsonDocument.Parse(line);
        Assert.Equal("goal-snapshot-archive", record.RootElement.GetProperty("eventKind").GetString());
        Assert.Equal("GOAL_SNAPSHOT_ARCHIVE status=failed archived=0 cutoff=2020-02-16T12:00:00.0000000+00:00 ageDays=14 exception=InvalidOperationException",
            record.RootElement.GetProperty("detail").GetString());
        Assert.DoesNotContain(journal, line => line.Contains("RUN_EVENTS_MAINTENANCE_FAILED"));
    }

    [Xunit.Fact]
    public void FailedSweep_DueWorkspace_StillInvokesArchive()
    {
        using var fixture = new Fixture();
        var sweep = SuccessfulSweep with
        {
            Decisions = [new EvidenceRetentionDecision(EvidenceArtifactFamily.RunEvents,
                EvidenceRetentionAction.Failed, fixture.RunEventsPath, null,
                EvidenceOwnerResolution.Unrecorded, "fixture-sweep-failed")]
        };
        var calls = 0;

        var result = fixture.Run(Now, _ => { calls++; return 0; }, sweep);

        Assert.True(result.Attempted);
        Assert.True(result.Failed);
        Assert.Equal("storage-retention-failed", result.Reason);
        Assert.Equal(1, calls);
        Assert.Equal("Failed", Assert.IsType<RunEventRecord>(fixture.ReadSweepReceipt()).Status);
    }

    [Xunit.Fact]
    public async Task RealStore_InjectedPastClock_ArchivesOnlyOldCompletedTerminalGoal()
    {
        using var fixture = new Fixture();
        var old = MakeSnapshot(GoalStatus.Completed);
        var recent = MakeSnapshot(GoalStatus.Completed);
        recent = recent with
        {
            Timeline = [.. recent.Timeline, new ProgressEventSnapshot(recent.Id, null,
                ProgressKind.GoalPolicyDecision, "Recent terminal evidence", Now.AddDays(-7))]
        };
        var active = MakeSnapshot(GoalStatus.Active);
        var unfinished = MakeSnapshot(GoalStatus.Completed);
        unfinished = unfinished with
        {
            Tasks = [unfinished.Tasks[0], unfinished.Tasks[0] with
                { Id = TaskId.New().Value, Status = WorkTaskStatus.Running }]
        };
        await fixture.Repository.SaveGoalSnapshotsAsync([old, recent, active, unfinished], Xunit.TestContext.Current.CancellationToken);
        foreach (var snapshot in new[] { old, active, unfinished })
            fixture.SetUpdatedAt(snapshot.Id, Now.AddDays(-30));
        fixture.SetUpdatedAt(recent.Id, Now.AddDays(-7));
        var before = fixture.GoalRows();
        Assert.Equal(4, before.Count);
        Assert.All(before.Values, row => Assert.False(Deserialize(row.Snapshot).IsMetadataOnly));
        Assert.Empty(fixture.ArchiveRows());

        // No archive seam: this must reach the real repository with the 2020 clock.
        // A wall-clock cutoff would also archive 'recent', which is recent only to this clock.
        var result = fixture.Run(Now);

        Assert.True(result.Attempted);
        Assert.False(result.Failed);
        var after = fixture.GoalRows();
        Assert.Equal(4, after.Count);
        var archived = Assert.Single(fixture.ArchiveRows());
        Assert.Equal(old.Id, archived.Key);
        Assert.Equal(before[old.Id].Snapshot, archived.Value);
        Assert.Equal(old.Id, Assert.Single(after, row => Deserialize(row.Value.Snapshot).IsMetadataOnly).Key);
        var stub = Deserialize(after[old.Id].Snapshot);
        Assert.Empty(stub.Tasks);
        Assert.Empty(stub.Timeline);
        foreach (var snapshot in new[] { recent, active, unfinished })
            Assert.Equal(before[snapshot.Id], after[snapshot.Id]);
        var hydrated = await fixture.Repository.LoadGoalAsync(new GoalId(old.Id), Xunit.TestContext.Current.CancellationToken);
        Assert.Equal(Json(old), Json(hydrated));
        Assert.Equal(Json(Deserialize(before[old.Id].Snapshot)), Json(hydrated));
    }

    private static GoalSnapshot MakeSnapshot(GoalStatus status)
    {
        var id = GoalId.New().Value;
        var task = new TaskSnapshot(TaskId.New().Value, "Completed implementation", AgentRole.Developer,
            WorkTaskStatus.Completed, null, null, null, null,
            new TaskDispatchSnapshot("fixture", "fixture", "fixture", Now.AddDays(-31), ResultCommit: "last-commit"), null);
        var snapshot = new GoalSnapshot(id, "Archive cadence fixture", status, [task],
        [
            new ProgressEventSnapshot(id, null, ProgressKind.GoalCreated, "Created", Now.AddDays(-32)),
            new ProgressEventSnapshot(id, null, ProgressKind.GoalPolicyDecision, "Terminal evidence", Now.AddDays(-30))
        ], SourceBacklogItemId: "source-item");
        return AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot([snapshot], []))
            .ExportGoalSnapshot(new GoalId(id));
    }

    private static string Json<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);
    private static GoalSnapshot Deserialize(string json) => JsonSerializer.Deserialize<GoalSnapshot>(json, JsonOptions)!;
    private sealed record GoalRow(string Status, string Objective, string? Source, string UpdatedAt, string Snapshot, long Version);

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "goal-snapshot-archive-cadence-tests", Guid.NewGuid().ToString("N"));
        public OrchestratorWorkspace Workspace { get; }
        public SqliteOrchestratorStateRepository Repository { get; }
        public string RunEventsPath => Workspace.RunEventStorePath;
        public string JournalPath => Workspace.ConductEventsLogPath;

        public Fixture()
        {
            Directory.CreateDirectory(_root);
            Workspace = OrchestratorWorkspace.ForDirectory(_root);
            Repository = InfrastructureTestSupport.CreateMigratedStateRepository(Workspace.SqliteStatePath);
        }

        public RunEventMaintenanceCadenceResult Run(DateTimeOffset now,
            Func<DateTimeOffset, int>? archive = null, StorageRetentionResult? sweep = null,
            RunEventMaintenanceResult? maintenance = null) =>
            RunEventMaintenanceCadence.TryRunIfDue(RunEventsPath, JournalPath, () => now,
                maintenanceOperation: (_, _) => maintenance ?? CompletedMaintenance,
                workspace: Workspace,
                artifactRetentionOperation: (_, _, _, _) => sweep ?? SuccessfulSweep,
                goalSnapshotArchiveOperation: archive);

        public RunEventRecord? ReadSweepReceipt() => new SqliteRunEventStore(RunEventsPath)
            .ReadLatestAsync(RunEventTypes.EvidenceRetention, RunEventMaintenanceCadence.ArtifactRetentionOperation)
            .GetAwaiter().GetResult();

        public void SetUpdatedAt(string id, DateTimeOffset at)
        {
            using var connection = StateDbConnectionFactory.Open(Workspace.SqliteStatePath, StateDbConnectionProfile.ReadWrite);
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE goals SET updated_at = $at WHERE id = $id";
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$at", at.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            Assert.Equal(1, command.ExecuteNonQuery());
        }

        public Dictionary<string, GoalRow> GoalRows()
        {
            using var connection = StateDbConnectionFactory.Open(Workspace.SqliteStatePath, StateDbConnectionProfile.ReadWrite);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT id, status, objective, source_backlog_item_id, updated_at, snapshot_json, version FROM goals ORDER BY id";
            using var reader = command.ExecuteReader();
            var rows = new Dictionary<string, GoalRow>();
            while (reader.Read())
                rows.Add(reader.GetString(0), new(reader.GetString(1), reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetInt64(6)));
            return rows;
        }

        public Dictionary<string, string> ArchiveRows()
        {
            using var connection = StateDbConnectionFactory.Open(Workspace.SqliteStatePath, StateDbConnectionProfile.ReadWrite);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT goal_id, snapshot_json FROM goal_snapshot_archive ORDER BY goal_id";
            using var reader = command.ExecuteReader();
            var rows = new Dictionary<string, string>();
            while (reader.Read()) rows.Add(reader.GetString(0), reader.GetString(1));
            return rows;
        }

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
