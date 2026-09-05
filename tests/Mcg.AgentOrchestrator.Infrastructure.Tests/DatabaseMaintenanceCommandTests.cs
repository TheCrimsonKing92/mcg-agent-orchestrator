using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

public sealed class DatabaseMaintenanceCommandTests : IDisposable
{
    // Parallel-safe: every test owns a unique directory, database, conversion files, and lease.
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "mcg-db-conversion-" + Guid.NewGuid().ToString("N"));

    [Xunit.Fact]
    public void ConvertLive_MissingLiveConfirmation_RejectsCommand()
    {
        var error = Assert.Throws<ArgumentException>(() =>
            CliCommandHandlers.ResolveStateDatabaseConversionOptions(
                ["state-db-maintenance", "convert-live", "--confirm-offline"],
                activeDispatchOrGate: false));

        Assert.Equal(CliCommandHelp.StateDatabaseMaintenanceUsage, error.Message);
    }

    [Xunit.Fact]
    public void ConvertCopy_OutputAndConfirmation_SelectCopyMode()
    {
        var options = CliCommandHandlers.ResolveStateDatabaseConversionOptions(
            ["state-db-maintenance", "convert-copy", "--output", "copy.db", "--confirm-offline"],
            activeDispatchOrGate: true);

        Assert.Equal(StateDatabaseOfflineConversionMode.CreateCopy, options.Mode);
        Assert.Equal("copy.db", options.CopyOutputPath);
        Assert.True(options.ExplicitlyAuthorized);
        Assert.True(options.ActiveDispatchOrGate);
    }

    [Xunit.Fact]
    public async Task ConvertCopy_ActiveLease_DefersWithoutCandidate()
    {
        var source = CreateDatabase();
        var output = Path.Combine(_root, "converted.db");
        using var activeLease = SqliteMaintenanceLease.AcquireSharedOrThrow(
            SqliteMaintenanceLease.ForDatabase(source));

        var result = await ConvertAsync(source, StateDatabaseOfflineConversionMode.CreateCopy, output);

        Assert.Equal(SqliteMaintenanceDisposition.Deferred, result.Disposition);
        Assert.Equal(SqliteMaintenanceReason.ActiveWork, result.Reason);
        Assert.False(File.Exists(output));
        Assert.Equal(150, ReadRowCount(source));
    }

    [Xunit.Fact]
    public void ConvertCopy_LiveDispatchWithoutGate_DefersWithoutCandidate()
    {
        var workspace = OrchestratorWorkspace.ForDirectory(_root);
        var source = CreateDatabase(workspace.SqliteStatePath);
        var output = Path.Combine(_root, "converted.db");
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Keep dispatch active", AgentRole.Developer);
        var goal = kernel.CreateGoal("Protect live dispatch from offline maintenance", [task]);
        var agents = AgentCatalog.Default().Agents;
        kernel.ActivateGoal(goal.Id, agents);
        kernel.RecordTaskDispatch(
            goal.Id,
            task.Id,
            new TaskDispatchRecord("codex-cli", "codex exec", _root, DateTimeOffset.UtcNow));
        var context = new CliExecutionContext(
            kernel,
            workspace,
            new InMemoryModelProviderRegistry([]),
            agents,
            WorkerProfileCatalog.Default(),
            goal);

        CliCommandHandlers.HandleStateDatabaseMaintenance(
            ["state-db-maintenance", "convert-copy", "--output", output, "--confirm-offline"],
            context,
            activeGate: false,
            conversionUtcNow: DateTimeOffset.Parse("2026-09-06T03:00:00Z"));

        Assert.False(File.Exists(output));
        Assert.Equal(150, ReadRowCount(source));
    }

    [Xunit.Fact]
    public async Task ConvertCopy_CandidateMismatch_PreservesSource()
    {
        var source = CreateDatabase();
        var output = Path.Combine(_root, "converted.db");
        var hooks = new StateDatabaseOfflineConversionHooks(
            AvailableFreeBytes: () => long.MaxValue,
            AfterCandidateBuilt: candidate => Execute(candidate, "PRAGMA user_version=99"));

        var result = await StateDatabaseOfflineConversion.ExecuteAsync(
            source,
            Options(StateDatabaseOfflineConversionMode.CreateCopy, output),
            hooks);

        Assert.Equal(SqliteMaintenanceDisposition.Stalled, result.Disposition);
        Assert.Equal(SqliteMaintenanceReason.CandidateSchemaMismatch, result.Reason);
        Assert.False(result.SourceReplaced);
        Assert.False(File.Exists(output));
        Assert.Equal(7, ReadLong(source, "PRAGMA user_version"));
        Assert.Equal(150, ReadRowCount(source));
    }

    [Xunit.Fact]
    public async Task ConvertCopy_CorruptCandidate_PreservesSource()
    {
        var source = CreateDatabase();
        var output = Path.Combine(_root, "converted.db");
        var hooks = new StateDatabaseOfflineConversionHooks(
            AvailableFreeBytes: () => long.MaxValue,
            AfterCandidateBuilt: candidate =>
            {
                using var stream = new FileStream(candidate, FileMode.Open, FileAccess.Write, FileShare.None);
                stream.SetLength(100);
            });

        var result = await StateDatabaseOfflineConversion.ExecuteAsync(
            source,
            Options(StateDatabaseOfflineConversionMode.CreateCopy, output),
            hooks);

        Assert.Equal(SqliteMaintenanceDisposition.Stalled, result.Disposition);
        Assert.Equal(SqliteMaintenanceReason.CandidateIntegrityCheckFailed, result.Reason);
        Assert.False(result.SourceReplaced);
        Assert.False(File.Exists(output));
        Assert.Equal(150, ReadRowCount(source));
    }

    [Xunit.Fact]
    public async Task ConvertCopy_ValidCandidate_ReportsAmplification()
    {
        var source = CreateDatabase();
        var output = Path.Combine(_root, "converted.db");

        var result = await ConvertAsync(source, StateDatabaseOfflineConversionMode.CreateCopy, output);

        Assert.Equal(SqliteMaintenanceDisposition.Completed, result.Disposition);
        Assert.False(result.SourceReplaced);
        Assert.True(File.Exists(output));
        Assert.True(result.CandidateStorage.MainDatabaseBytes > 0);
        Assert.True(result.AggregateFinalBytes > result.FinalStorage.TotalBytes);
        Assert.Equal(2, ReadLong(output, "PRAGMA auto_vacuum"));
        Assert.Equal("wal", ReadText(output, "PRAGMA journal_mode"));
        Assert.Equal("ok", ReadText(output, "PRAGMA integrity_check"));
        Assert.Equal(150, ReadRowCount(output));
        Assert.Contains("candidateMain=", result.FormatReceipt(), StringComparison.Ordinal);
        Assert.Contains("aggregateFinalTotal=", result.FormatReceipt(), StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public async Task ConvertLive_ValidCandidate_ReopensConvertedDatabase()
    {
        var source = CreateDatabase();

        var result = await ConvertAsync(source, StateDatabaseOfflineConversionMode.ReplaceLive);

        Assert.Equal(SqliteMaintenanceDisposition.Completed, result.Disposition);
        Assert.Equal(SqliteMaintenanceReason.None, result.Reason);
        Assert.True(result.SourceReplaced);
        Assert.False(result.RollbackPerformed);
        Assert.True(result.CandidateStorage.MainDatabaseBytes > 0);
        Assert.True(result.BackupBytes > 0);
        Assert.True(result.FinalStorage.TotalBytes <= result.BeforeStorage.TotalBytes + 4096);
        Assert.Equal(2, ReadLong(source, "PRAGMA auto_vacuum"));
        Assert.Equal("wal", ReadText(source, "PRAGMA journal_mode"));
        Assert.Equal("ok", ReadText(source, "PRAGMA integrity_check"));
        Assert.Equal(150, ReadRowCount(source));
        Assert.Empty(Directory.EnumerateFiles(_root, "state.db-maintenance-*"));
    }

    [Xunit.Fact]
    public async Task ConvertLive_InstallFailure_RestoresOriginal()
    {
        var source = CreateDatabase();
        var hooks = new StateDatabaseOfflineConversionHooks(
            AvailableFreeBytes: () => long.MaxValue,
            AfterSourceBackedUp: () =>
            {
                Assert.True(File.Exists(source));
                throw new IOException("Injected install failure.");
            });

        var result = await StateDatabaseOfflineConversion.ExecuteAsync(
            source,
            Options(StateDatabaseOfflineConversionMode.ReplaceLive),
            hooks);

        Assert.Equal(SqliteMaintenanceDisposition.Stalled, result.Disposition);
        Assert.Equal(SqliteMaintenanceReason.ReplacementFailed, result.Reason);
        Assert.False(result.SourceReplaced);
        Assert.True(result.RollbackPerformed);
        Assert.True(result.BackupBytes > 0);
        Assert.Equal(0, ReadLong(source, "PRAGMA auto_vacuum"));
        Assert.Equal(7, ReadLong(source, "PRAGMA user_version"));
        Assert.Equal(150, ReadRowCount(source));
        Assert.Empty(Directory.EnumerateFiles(_root, "state.db-maintenance-*"));
    }

    [Xunit.Fact]
    public async Task ConvertLive_FinalByteGrowth_RestoresOriginalBeforeDeletingBackup()
    {
        var source = CreateDatabase();
        var hooks = new StateDatabaseOfflineConversionHooks(
            AvailableFreeBytes: () => long.MaxValue,
            BeforeFinalStorageValidation: installedPath =>
            {
                using var amplification = new FileStream(
                    installedPath + "-wal",
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None);
                amplification.SetLength(2 * 1024 * 1024);
            });

        var result = await StateDatabaseOfflineConversion.ExecuteAsync(
            source,
            Options(StateDatabaseOfflineConversionMode.ReplaceLive),
            hooks);

        Assert.Equal(SqliteMaintenanceDisposition.Stalled, result.Disposition);
        Assert.Equal(SqliteMaintenanceReason.ByteGrowthExceeded, result.Reason);
        Assert.False(result.SourceReplaced);
        Assert.True(result.RollbackPerformed);
        Assert.Equal(0, ReadLong(source, "PRAGMA auto_vacuum"));
        Assert.Equal(7, ReadLong(source, "PRAGMA user_version"));
        Assert.Equal(150, ReadRowCount(source));
        Assert.Empty(Directory.EnumerateFiles(_root, "state.db-maintenance-*"));
    }

    [Xunit.Fact]
    public async Task ConvertLive_BackupCleanupFailure_IsStalledAndCountsRetainedBackup()
    {
        var source = CreateDatabase();
        var hooks = new StateDatabaseOfflineConversionHooks(
            AvailableFreeBytes: () => long.MaxValue,
            TryDeleteDatabaseFamily: _ => false);

        var result = await StateDatabaseOfflineConversion.ExecuteAsync(
            source,
            Options(StateDatabaseOfflineConversionMode.ReplaceLive),
            hooks);

        Assert.Equal(SqliteMaintenanceDisposition.Stalled, result.Disposition);
        Assert.Equal("BackupCleanupFailed", result.Reason.ToString());
        Assert.True(result.SourceReplaced);
        Assert.False(result.RollbackPerformed);
        Assert.True(result.RetainedBackupBytes > 0);
        Assert.Equal(result.FinalStorage.TotalBytes + result.RetainedBackupBytes, result.AggregateFinalBytes);
        Assert.True(result.AggregateFinalBytes > result.FinalStorage.TotalBytes);
        Assert.Contains($"retainedBackupBytes={result.RetainedBackupBytes}", result.FormatReceipt(), StringComparison.Ordinal);
        Assert.Equal(150, ReadRowCount(source));
        Assert.NotEmpty(Directory.EnumerateFiles(_root, "state.db-maintenance-*.backup"));
    }

    [Xunit.Fact]
    public void ConductorLoop_ExclusiveMaintenanceLeaseDefersRetriesAndWritesDiagnostic()
    {
        var workspace = OrchestratorWorkspace.ForDirectory(_root);
        Directory.CreateDirectory(workspace.OrchestratorDirectory);
        var retryDelays = new List<TimeSpan>();
        var rootStateLease = SqliteMaintenanceLease.ForDatabase(Path.Combine(_root, "state.db"));
        var rootRunEventLease = SqliteMaintenanceLease.ForDatabase(Path.Combine(_root, "run-events.db"));
        using (AssertMaintenanceLeaseAcquired(workspace.SqliteStatePath))
        {
            RunConductor(workspace, retryDelays.Add);
        }

        Assert.Equal(2, retryDelays.Count);
        var diagnostic = File.ReadAllText(workspace.ConductEventsLogPath);
        Assert.Contains("loop-start-deferred", diagnostic, StringComparison.Ordinal);
        Assert.Contains("state.db.maintenance.lease", diagnostic, StringComparison.Ordinal);
        Assert.False(File.Exists(rootStateLease));
        Assert.False(File.Exists(rootRunEventLease));
    }

    [Xunit.Fact]
    public void InterruptedLiveConversionRestoresDiscoverableBackupBeforeReadWriteStartup()
    {
        var source = CreateDatabase();
        var backup = source + "-maintenance-interrupted.backup";
        File.Move(source, backup);
        Assert.False(File.Exists(source));

        using (StateDbConnectionFactory.Open(source, StateDbConnectionProfile.ReadWrite))
        {
        }

        Assert.True(File.Exists(source));
        Assert.False(File.Exists(backup));
        Assert.Equal(150, ReadRowCount(source));
        Assert.Equal(7, ReadLong(source, "PRAGMA user_version"));
    }

    [Xunit.Fact]
    public async Task ConvertLive_WithoutAuthorization_RefusesBeforeMutation()
    {
        var source = CreateDatabase();

        var result = await StateDatabaseOfflineConversion.ExecuteAsync(
            source,
            Options(StateDatabaseOfflineConversionMode.ReplaceLive) with { ExplicitlyAuthorized = false },
            new StateDatabaseOfflineConversionHooks(AvailableFreeBytes: () => long.MaxValue));

        Assert.Equal(SqliteMaintenanceDisposition.Deferred, result.Disposition);
        Assert.Equal(SqliteMaintenanceReason.ExplicitAuthorizationRequired, result.Reason);
        Assert.False(result.SourceReplaced);
        Assert.Equal(0, ReadLong(source, "PRAGMA auto_vacuum"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private Task<StateDatabaseOfflineConversionResult> ConvertAsync(
        string source,
        StateDatabaseOfflineConversionMode mode,
        string? output = null) =>
        StateDatabaseOfflineConversion.ExecuteAsync(
            source,
            Options(mode, output),
            new StateDatabaseOfflineConversionHooks(AvailableFreeBytes: () => long.MaxValue));

    private static IDisposable AssertMaintenanceLeaseAcquired(string databasePath)
    {
        Assert.True(SqliteMaintenanceLease.TryAcquireExclusive(
            SqliteMaintenanceLease.ForDatabase(databasePath),
            out var lease));
        return lease!;
    }

    private static void RunConductor(OrchestratorWorkspace workspace, Action<TimeSpan> retryDelay) =>
        new ConductorBatchLoop(
            workspace: workspace,
            conductEventLogWriter: new ConductEventLogWriter(workspace.ConductEventsLogPath)).Run(
            new AgentOrchestratorKernel(),
            driver: null!,
            ConductorAutonomyPolicy.Conservative,
            Path.Combine(workspace.RootDirectory, ConductorBatchLoop.StopFileName),
            maxIterations: 1,
            busyWriteDelay: retryDelay);

    private static StateDatabaseOfflineConversionOptions Options(
        StateDatabaseOfflineConversionMode mode,
        string? output = null) =>
        new(
            mode,
            CopyOutputPath: output,
            ExplicitlyAuthorized: true,
            ActiveDispatchOrGate: false,
            UtcNow: DateTimeOffset.Parse("2026-09-06T03:00:00Z"));

    private string CreateDatabase(string? databasePath = null)
    {
        var path = databasePath ?? Path.Combine(_root, "state.db");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var connection = Open(path);
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA user_version=7;
            CREATE TABLE fixture(id INTEGER PRIMARY KEY, value TEXT NOT NULL);
            WITH RECURSIVE rows(id) AS (
                SELECT 1 UNION ALL SELECT id + 1 FROM rows WHERE id < 300
            )
            INSERT INTO fixture(id, value)
            SELECT id, printf('%.*c', 2048, 'x') FROM rows;
            DELETE FROM fixture WHERE id % 2 = 0;
            PRAGMA wal_checkpoint(TRUNCATE);
            """;
        command.ExecuteNonQuery();
        return path;
    }

    private static int ReadRowCount(string path) => checked((int)ReadLong(
        path,
        "SELECT count(*) FROM fixture"));

    private static long ReadLong(string path, string sql)
    {
        using var connection = Open(path);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string ReadText(string path, string sql)
    {
        using var connection = Open(path);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static void Execute(string path, string sql)
    {
        using var connection = Open(path);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        return connection;
    }
}
