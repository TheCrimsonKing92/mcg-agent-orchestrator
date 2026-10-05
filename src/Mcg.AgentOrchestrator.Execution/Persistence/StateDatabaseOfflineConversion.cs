using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.Infrastructure;

public enum StateDatabaseOfflineConversionMode
{
    CreateCopy,
    ReplaceLive
}

public sealed record StateDatabaseOfflineConversionOptions(
    StateDatabaseOfflineConversionMode Mode,
    string? CopyOutputPath = null,
    bool ExplicitlyAuthorized = false,
    bool ActiveDispatchOrGate = false,
    DateTimeOffset? UtcNow = null,
    DayOfWeek OffPeakDay = DayOfWeek.Sunday,
    int OffPeakStartHourUtc = 2,
    int OffPeakEndHourUtc = 5,
    long MaterialGrowthToleranceBytes = 4096);

public sealed record StateDatabaseCandidateValidation(
    bool IntegrityOk,
    string IntegrityResult,
    long SchemaVersion,
    long UserVersion,
    string SchemaFingerprint,
    string JournalMode,
    int AutoVacuumMode);

public sealed record StateDatabaseOfflineConversionResult(
    StateDatabaseOfflineConversionMode Mode,
    SqliteMaintenanceDisposition Disposition,
    SqliteMaintenanceReason Reason,
    SqliteStorageSnapshot BeforeStorage,
    SqliteStorageSnapshot CandidateStorage,
    SqliteStorageSnapshot PeakStorage,
    SqliteStorageSnapshot FinalStorage,
    SqlitePageMetrics BeforePages,
    SqlitePageMetrics FinalPages,
    StateDatabaseCandidateValidation? CandidateValidation,
    long RequiredFreeBytes,
    long AvailableFreeBytes,
    long BackupBytes,
    bool SourceReplaced,
    bool RollbackPerformed,
    string? Detail,
    long RetainedBackupBytes = 0)
{
    public long AggregateFinalBytes => checked(
        FinalStorage.TotalBytes +
        (Mode == StateDatabaseOfflineConversionMode.CreateCopy ? CandidateStorage.TotalBytes : 0) +
        RetainedBackupBytes);

    public string FormatReceipt() => string.Create(
        CultureInfo.InvariantCulture,
        $"STATE_DB_OFFLINE_CONVERSION mode={Mode} status={Disposition.ToString().ToLowerInvariant()} reason={Reason} sourceReplaced={SourceReplaced} rollbackPerformed={RollbackPerformed} requiredFree={RequiredFreeBytes} availableFree={AvailableFreeBytes} beforeMain={BeforeStorage.MainDatabaseBytes} beforeWal={BeforeStorage.WalBytes} beforeShm={BeforeStorage.ShmBytes} beforeOther={BeforeStorage.OtherTransientBytes} beforeTotal={BeforeStorage.TotalBytes} candidateMain={CandidateStorage.MainDatabaseBytes} candidateWal={CandidateStorage.WalBytes} candidateShm={CandidateStorage.ShmBytes} candidateOther={CandidateStorage.OtherTransientBytes} candidateTotal={CandidateStorage.TotalBytes} peakMain={PeakStorage.MainDatabaseBytes} peakWal={PeakStorage.WalBytes} peakShm={PeakStorage.ShmBytes} peakOther={PeakStorage.OtherTransientBytes} peakTotal={PeakStorage.TotalBytes} backupBytes={BackupBytes} retainedBackupBytes={RetainedBackupBytes} finalMain={FinalStorage.MainDatabaseBytes} finalWal={FinalStorage.WalBytes} finalShm={FinalStorage.ShmBytes} finalOther={FinalStorage.OtherTransientBytes} finalTotal={FinalStorage.TotalBytes} aggregateFinalTotal={AggregateFinalBytes} beforePages={BeforePages.PageCount} beforeFreelist={BeforePages.FreelistCount} finalPages={FinalPages.PageCount} finalFreelist={FinalPages.FreelistCount} integrity={CandidateValidation?.IntegrityResult ?? "not-run"} schemaVersion={CandidateValidation?.SchemaVersion ?? -1} userVersion={CandidateValidation?.UserVersion ?? -1} journalMode={CandidateValidation?.JournalMode ?? "not-run"} autoVacuum={CandidateValidation?.AutoVacuumMode ?? -1} detail={Sanitize(Detail)}");

    private static string Sanitize(string? value) => string.IsNullOrWhiteSpace(value)
        ? "none"
        : value.Replace('\r', ' ').Replace('\n', ' ');
}

internal sealed record StateDatabaseOfflineConversionHooks(
    Func<long>? AvailableFreeBytes = null,
    Action<string>? AfterCandidateBuilt = null,
    Action? AfterSourceBackedUp = null,
    Action<string>? BeforeFinalStorageValidation = null,
    Func<string, bool>? TryDeleteDatabaseFamily = null);

public static class StateDatabaseOfflineConversion
{
    private static readonly SqliteStorageSnapshot EmptyStorage = new(0, 0, 0, 0);
    private static readonly SqlitePageMetrics EmptyPages = new(0, 0, 0, string.Empty, 0);

    public static Task<StateDatabaseOfflineConversionResult> ExecuteAsync(
        string databasePath,
        StateDatabaseOfflineConversionOptions options,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(databasePath, options, hooks: null, cancellationToken);

    internal static bool RecoverInterruptedReplacement(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        var sourcePath = Path.GetFullPath(databasePath);
        if (File.Exists(sourcePath))
            return false;

        var backups = FindRecoverableBackups(sourcePath);
        if (backups.Length == 0)
            return false;
        if (backups.Length > 1)
        {
            throw new InvalidOperationException(
                $"State database recovery found multiple rollback backups for '{sourcePath}'; refusing to guess: {string.Join(", ", backups)}");
        }

        var leasePath = SqliteMaintenanceLease.ForDatabase(sourcePath);
        if (!SqliteMaintenanceLease.TryAcquireExclusive(leasePath, out var lease))
        {
            throw new IOException(
                $"State database recovery is pending while SQLite maintenance holds '{leasePath}'.");
        }

        using (lease)
        {
            if (File.Exists(sourcePath))
                return false;

            backups = FindRecoverableBackups(sourcePath);
            if (backups.Length != 1)
            {
                throw new InvalidOperationException(
                    $"State database recovery backup set changed for '{sourcePath}'; expected one backup, found {backups.Length}.");
            }

            MoveDatabaseFamily(backups[0], sourcePath);
            return true;
        }
    }

    internal static async Task<StateDatabaseOfflineConversionResult> ExecuteAsync(
        string databasePath,
        StateDatabaseOfflineConversionOptions options,
        StateDatabaseOfflineConversionHooks? hooks,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentNullException.ThrowIfNull(options);
        var sourcePath = Path.GetFullPath(databasePath);
        var beforeStorage = SqliteStorageSnapshot.Measure(sourcePath);
        var beforePages = StateDatabaseMaintenance.Probe(sourcePath);
        var now = options.UtcNow ?? DateTimeOffset.UtcNow;

        if (!options.ExplicitlyAuthorized)
            return Early(SqliteMaintenanceReason.ExplicitAuthorizationRequired, SqliteMaintenanceDisposition.Deferred);
        if (options.ActiveDispatchOrGate)
            return Early(SqliteMaintenanceReason.ActiveWork, SqliteMaintenanceDisposition.Deferred);
        if (!IsWithinWindow(now, options))
            return Early(SqliteMaintenanceReason.OutsideMaintenanceWindow, SqliteMaintenanceDisposition.Deferred);

        var leasePath = SqliteMaintenanceLease.ForDatabase(sourcePath);
        if (!SqliteMaintenanceLease.TryAcquireExclusive(leasePath, out var lease))
            return Early(SqliteMaintenanceReason.ActiveWork, SqliteMaintenanceDisposition.Deferred);

        using (lease)
        {
            var operationId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
            var candidatePath = options.Mode == StateDatabaseOfflineConversionMode.CreateCopy
                ? ResolveCopyOutput(sourcePath, options.CopyOutputPath)
                : sourcePath + "-maintenance-" + operationId + ".candidate";
            var backupPath = sourcePath + "-maintenance-" + operationId + ".backup";
            if (File.Exists(candidatePath))
            {
                return OperationResult(
                    SqliteMaintenanceReason.ReplacementFailed,
                    SqliteMaintenanceDisposition.Stalled,
                    $"Candidate path already exists: {candidatePath}");
            }

            var requiredFreeBytes = RequiredFreeBytes(beforeStorage);
            var availableFreeBytes = hooks?.AvailableFreeBytes?.Invoke() ?? ReadAvailableFreeBytes(candidatePath);
            if (availableFreeBytes < requiredFreeBytes)
            {
                return OperationResult(
                    SqliteMaintenanceReason.InsufficientFreeSpace,
                    SqliteMaintenanceDisposition.Deferred,
                    $"required={requiredFreeBytes};available={availableFreeBytes}",
                    requiredFreeBytes,
                    availableFreeBytes);
            }

            StateDatabaseCandidateValidation? expected = null;
            StateDatabaseCandidateValidation? candidateValidation = null;
            var candidateStorage = EmptyStorage;
            var peakStorage = beforeStorage;
            long backupBytes = 0;
            var sourceReplaced = false;
            var rollbackPerformed = false;
            var keepCandidateCopy = false;
            try
            {
                await using (var source = OpenDirect(sourcePath, readOnly: false))
                {
                    if (!await CanAcquireWriteTransactionAsync(source, cancellationToken).ConfigureAwait(false))
                    {
                        return OperationResult(
                            SqliteMaintenanceReason.OpenTransaction,
                            SqliteMaintenanceDisposition.Deferred,
                            requiredFreeBytes: requiredFreeBytes,
                            availableFreeBytes: availableFreeBytes);
                    }

                    var checkpoint = await ReadCheckpointAsync(source, cancellationToken).ConfigureAwait(false);
                    if (!checkpoint.Completed)
                    {
                        return OperationResult(
                            SqliteMaintenanceReason.CheckpointBusy,
                            SqliteMaintenanceDisposition.Deferred,
                            requiredFreeBytes: requiredFreeBytes,
                            availableFreeBytes: availableFreeBytes);
                    }

                    expected = await ValidateAsync(source, cancellationToken).ConfigureAwait(false);
                    await using var vacuum = source.CreateCommand();
                    vacuum.CommandText = "VACUUM INTO $candidate";
                    vacuum.Parameters.AddWithValue("$candidate", candidatePath);
                    await vacuum.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                await ConfigureCandidateAsync(candidatePath, cancellationToken).ConfigureAwait(false);
                hooks?.AfterCandidateBuilt?.Invoke(candidatePath);
                candidateValidation = await ValidatePathAsync(candidatePath, cancellationToken).ConfigureAwait(false);
                candidateStorage = SqliteStorageSnapshot.Measure(candidatePath);
                var validationReason = ClassifyValidation(expected, candidateValidation);
                if (validationReason != SqliteMaintenanceReason.None)
                {
                    return Failure(validationReason, "Candidate validation failed before replacement.");
                }

                peakStorage = MeasureAggregate(beforeStorage, candidateStorage);
                if (options.Mode == StateDatabaseOfflineConversionMode.CreateCopy)
                {
                    keepCandidateCopy = true;
                    return new StateDatabaseOfflineConversionResult(
                        options.Mode,
                        SqliteMaintenanceDisposition.Completed,
                        SqliteMaintenanceReason.None,
                        beforeStorage,
                        candidateStorage,
                        peakStorage,
                        SqliteStorageSnapshot.Measure(sourcePath),
                        beforePages,
                        StateDatabaseMaintenance.Probe(candidatePath),
                        candidateValidation,
                        requiredFreeBytes,
                        availableFreeBytes,
                        BackupBytes: 0,
                        SourceReplaced: false,
                        RollbackPerformed: false,
                        Detail: "Candidate copy created and validated; source was not replaced.");
                }

                MoveIfExists(sourcePath + "-wal", backupPath + "-wal");
                MoveIfExists(sourcePath + "-shm", backupPath + "-shm");
                File.Replace(candidatePath, sourcePath, backupPath, ignoreMetadataErrors: true);
                backupBytes = MeasureExistingFamilyBytes(backupPath);
                sourceReplaced = true;
                try
                {
                    hooks?.AfterSourceBackedUp?.Invoke();
                }
                catch (Exception exception)
                {
                    rollbackPerformed = RollbackReplacement(sourcePath, backupPath, candidatePath);
                    sourceReplaced = !rollbackPerformed;
                    return Failure(
                        rollbackPerformed
                            ? SqliteMaintenanceReason.ReplacementFailed
                            : SqliteMaintenanceReason.ReplacementRollbackFailed,
                        exception.Message);
                }

                var installedValidation = await ValidatePathAsync(sourcePath, cancellationToken).ConfigureAwait(false);
                var installedReason = ClassifyValidation(expected, installedValidation);
                if (installedReason != SqliteMaintenanceReason.None)
                {
                    rollbackPerformed = RollbackReplacement(sourcePath, backupPath, candidatePath);
                    sourceReplaced = !rollbackPerformed;
                    return Failure(
                        rollbackPerformed
                            ? SqliteMaintenanceReason.InstalledValidationFailed
                            : SqliteMaintenanceReason.ReplacementRollbackFailed,
                        "Installed database failed validation.");
                }

                await CheckpointInstalledAsync(sourcePath, cancellationToken).ConfigureAwait(false);
                var installedStorage = MeasureWithoutOperationFiles(sourcePath, candidatePath, backupPath);
                var grewMaterially = installedStorage.TotalBytes >
                    beforeStorage.TotalBytes + Math.Max(0, options.MaterialGrowthToleranceBytes);
                if (grewMaterially)
                {
                    rollbackPerformed = RollbackReplacement(sourcePath, backupPath, candidatePath);
                    sourceReplaced = !rollbackPerformed;
                    return Failure(
                        rollbackPerformed
                            ? SqliteMaintenanceReason.ByteGrowthExceeded
                            : SqliteMaintenanceReason.ReplacementRollbackFailed,
                        "Installed database grew beyond the configured total-byte tolerance.");
                }

                hooks?.BeforeFinalStorageValidation?.Invoke(sourcePath);
                var finalInstalledStorage = MeasureWithoutOperationFiles(sourcePath, candidatePath, backupPath);
                if (finalInstalledStorage.TotalBytes >
                    beforeStorage.TotalBytes + Math.Max(0, options.MaterialGrowthToleranceBytes))
                {
                    rollbackPerformed = RollbackReplacement(sourcePath, backupPath, candidatePath);
                    sourceReplaced = !rollbackPerformed;
                    return Failure(
                        rollbackPerformed
                            ? SqliteMaintenanceReason.ByteGrowthExceeded
                            : SqliteMaintenanceReason.ReplacementRollbackFailed,
                        "Final total bytes grew beyond tolerance.");
                }

                var backupCleanupSucceeded = hooks?.TryDeleteDatabaseFamily?.Invoke(backupPath)
                    ?? TryDeleteDatabaseFamily(backupPath);
                var retainedBackupBytes = MeasureExistingFamilyBytes(backupPath);
                var finalStorage = SqliteStorageSnapshot.Measure(sourcePath);
                if (!backupCleanupSucceeded || retainedBackupBytes > 0)
                {
                    return new StateDatabaseOfflineConversionResult(
                        options.Mode,
                        SqliteMaintenanceDisposition.Stalled,
                        SqliteMaintenanceReason.BackupCleanupFailed,
                        beforeStorage,
                        candidateStorage,
                        peakStorage,
                        finalStorage,
                        beforePages,
                        StateDatabaseMaintenance.Probe(sourcePath),
                        installedValidation,
                        requiredFreeBytes,
                        availableFreeBytes,
                        backupBytes,
                        SourceReplaced: true,
                        RollbackPerformed: false,
                        Detail: "Installed database is valid, but the rollback backup could not be removed; retry cleanup before declaring convergence.",
                        RetainedBackupBytes: retainedBackupBytes);
                }

                return new StateDatabaseOfflineConversionResult(
                    options.Mode,
                    SqliteMaintenanceDisposition.Completed,
                    SqliteMaintenanceReason.None,
                    beforeStorage,
                    candidateStorage,
                    peakStorage,
                    finalStorage,
                    beforePages,
                    StateDatabaseMaintenance.Probe(sourcePath),
                    installedValidation,
                    requiredFreeBytes,
                    availableFreeBytes,
                    backupBytes,
                    SourceReplaced: true,
                    RollbackPerformed: false,
                    Detail: "Candidate validated, installed, reopened, checkpointed, and remeasured.");
            }
            catch (SqliteException exception)
            {
                if (File.Exists(backupPath))
                {
                    rollbackPerformed = RollbackReplacement(sourcePath, backupPath, candidatePath);
                    sourceReplaced = !rollbackPerformed;
                    return Failure(
                        rollbackPerformed
                            ? SqliteMaintenanceReason.InstalledValidationFailed
                            : SqliteMaintenanceReason.ReplacementRollbackFailed,
                        exception.Message);
                }

                return Failure(SqliteMaintenanceReason.CandidateIntegrityCheckFailed, exception.Message);
            }
            catch (Exception exception)
            {
                if (File.Exists(backupPath))
                {
                    rollbackPerformed = RollbackReplacement(sourcePath, backupPath, candidatePath);
                    sourceReplaced = !rollbackPerformed;
                    return Failure(
                        rollbackPerformed
                            ? SqliteMaintenanceReason.ReplacementFailed
                            : SqliteMaintenanceReason.ReplacementRollbackFailed,
                        exception.Message);
                }

                return Failure(SqliteMaintenanceReason.ReplacementFailed, exception.Message);
            }
            finally
            {
                if (!keepCandidateCopy)
                {
                    _ = TryDeleteDatabaseFamily(candidatePath);
                }
            }

            StateDatabaseOfflineConversionResult OperationResult(
                SqliteMaintenanceReason reason,
                SqliteMaintenanceDisposition disposition,
                string? detail = null,
                long requiredFreeBytes = 0,
                long availableFreeBytes = 0) =>
                new(
                    options.Mode,
                    disposition,
                    reason,
                    beforeStorage,
                    EmptyStorage,
                    beforeStorage,
                    beforeStorage,
                    beforePages,
                    beforePages,
                    CandidateValidation: null,
                    requiredFreeBytes,
                    availableFreeBytes,
                    BackupBytes: 0,
                    SourceReplaced: false,
                    RollbackPerformed: false,
                    detail);

            StateDatabaseOfflineConversionResult Failure(SqliteMaintenanceReason reason, string detail)
            {
                var finalStorage = File.Exists(sourcePath)
                    ? SqliteStorageSnapshot.Measure(sourcePath)
                    : EmptyStorage;
                var finalPages = File.Exists(sourcePath)
                    ? StateDatabaseMaintenance.Probe(sourcePath)
                    : EmptyPages;
                var retainedBackupBytes = MeasureExistingFamilyBytes(backupPath);
                return new StateDatabaseOfflineConversionResult(
                    options.Mode,
                    SqliteMaintenanceDisposition.Stalled,
                    reason,
                    beforeStorage,
                    candidateStorage,
                    peakStorage,
                    finalStorage,
                    beforePages,
                    finalPages,
                    candidateValidation,
                    requiredFreeBytes,
                    availableFreeBytes,
                    backupBytes,
                    sourceReplaced,
                    rollbackPerformed,
                    detail,
                    retainedBackupBytes);
            }
        }

        StateDatabaseOfflineConversionResult Early(
            SqliteMaintenanceReason reason,
            SqliteMaintenanceDisposition disposition,
            string? detail = null) =>
            new(
                options.Mode,
                disposition,
                reason,
                beforeStorage,
                EmptyStorage,
                beforeStorage,
                beforeStorage,
                beforePages,
                beforePages,
                CandidateValidation: null,
                RequiredFreeBytes: 0,
                AvailableFreeBytes: 0,
                BackupBytes: 0,
                SourceReplaced: false,
                RollbackPerformed: false,
                detail);
    }

    private static string ResolveCopyOutput(string sourcePath, string? outputPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        var fullOutput = Path.GetFullPath(outputPath);
        if (fullOutput.Equals(sourcePath, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Copy output must differ from the source database.", nameof(outputPath));
        Directory.CreateDirectory(Path.GetDirectoryName(fullOutput) ?? Directory.GetCurrentDirectory());
        return fullOutput;
    }

    private static string[] FindRecoverableBackups(string sourcePath)
    {
        var directory = Path.GetDirectoryName(sourcePath) ?? Directory.GetCurrentDirectory();
        var pattern = Path.GetFileName(sourcePath) + "-maintenance-*.backup";
        return Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly)
                .Where(path => path.EndsWith(".backup", StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray()
            : [];
    }

    private static bool IsWithinWindow(DateTimeOffset now, StateDatabaseOfflineConversionOptions options) =>
        now.DayOfWeek == options.OffPeakDay &&
        now.Hour >= options.OffPeakStartHourUtc &&
        now.Hour < options.OffPeakEndHourUtc;

    private static long RequiredFreeBytes(SqliteStorageSnapshot before) => checked(
        Math.Max(16L * 1024 * 1024, before.MainDatabaseBytes * 2 + before.WalBytes + before.ShmBytes));

    private static long ReadAvailableFreeBytes(string path)
    {
        var root = Path.GetPathRoot(path);
        if (string.IsNullOrWhiteSpace(root))
            throw new IOException($"Cannot resolve a volume for '{path}'.");
        return new DriveInfo(root).AvailableFreeSpace;
    }

    private static SqliteConnection OpenDirect(string path, bool readOnly)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite,
            Pooling = false,
            DefaultTimeout = 1
        }.ConnectionString);
        connection.Open();
        using var timeout = connection.CreateCommand();
        timeout.CommandText = "PRAGMA busy_timeout=0";
        timeout.ExecuteNonQuery();
        return connection;
    }

    private static async Task<bool> CanAcquireWriteTransactionAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var begin = connection.CreateCommand();
            begin.CommandText = "BEGIN IMMEDIATE";
            await begin.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await using var rollback = connection.CreateCommand();
            rollback.CommandText = "ROLLBACK";
            await rollback.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
        {
            return false;
        }
    }

    private static async Task ConfigureCandidateAsync(string candidatePath, CancellationToken cancellationToken)
    {
        await using var candidate = OpenDirect(candidatePath, readOnly: false);
        await ExecuteAsync(candidate, "PRAGMA auto_vacuum=INCREMENTAL", cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(candidate, "VACUUM", cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(candidate, "PRAGMA journal_mode=WAL", cancellationToken).ConfigureAwait(false);
        var checkpoint = await ReadCheckpointAsync(candidate, cancellationToken).ConfigureAwait(false);
        if (!checkpoint.Completed)
            throw new SqliteException("Candidate checkpoint did not converge.", 5);
    }

    private static async Task CheckpointInstalledAsync(string path, CancellationToken cancellationToken)
    {
        await using var installed = OpenDirect(path, readOnly: false);
        var checkpoint = await ReadCheckpointAsync(installed, cancellationToken).ConfigureAwait(false);
        if (!checkpoint.Completed)
            throw new SqliteException("Installed database checkpoint did not converge.", 5);
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
        return new SqliteCheckpointResult(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2));
    }

    private static async Task<StateDatabaseCandidateValidation> ValidatePathAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var connection = OpenDirect(path, readOnly: true);
        return await ValidateAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<StateDatabaseCandidateValidation> ValidateAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var integrity = await ScalarTextAsync(connection, "PRAGMA integrity_check", cancellationToken).ConfigureAwait(false);
        return new StateDatabaseCandidateValidation(
            integrity.Equals("ok", StringComparison.OrdinalIgnoreCase),
            integrity,
            await ScalarLongAsync(connection, "PRAGMA schema_version", cancellationToken).ConfigureAwait(false),
            await ScalarLongAsync(connection, "PRAGMA user_version", cancellationToken).ConfigureAwait(false),
            await SchemaFingerprintAsync(connection, cancellationToken).ConfigureAwait(false),
            await ScalarTextAsync(connection, "PRAGMA journal_mode", cancellationToken).ConfigureAwait(false),
            checked((int)await ScalarLongAsync(connection, "PRAGMA auto_vacuum", cancellationToken).ConfigureAwait(false)));
    }

    private static SqliteMaintenanceReason ClassifyValidation(
        StateDatabaseCandidateValidation expected,
        StateDatabaseCandidateValidation actual)
    {
        if (!actual.IntegrityOk)
            return SqliteMaintenanceReason.CandidateIntegrityCheckFailed;
        if (actual.UserVersion != expected.UserVersion ||
            !actual.SchemaFingerprint.Equals(expected.SchemaFingerprint, StringComparison.Ordinal))
        {
            return SqliteMaintenanceReason.CandidateSchemaMismatch;
        }
        if (!actual.JournalMode.Equals("wal", StringComparison.OrdinalIgnoreCase))
            return SqliteMaintenanceReason.CandidateJournalModeMismatch;
        if (actual.AutoVacuumMode != 2)
            return SqliteMaintenanceReason.CandidateAutoVacuumMismatch;
        return SqliteMaintenanceReason.None;
    }

    private static async Task<string> SchemaFingerprintAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT type, name, tbl_name, coalesce(sql, '') FROM sqlite_schema WHERE name NOT LIKE 'sqlite_%' ORDER BY type, name, tbl_name";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var text = new StringBuilder();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            for (var index = 0; index < 4; index++)
                text.Append(reader.GetString(index)).Append('\u001f');
            text.Append('\u001e');
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    private static async Task<long> ScalarLongAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    private static async Task<string> ScalarTextAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static SqliteStorageSnapshot MeasureAggregate(
        SqliteStorageSnapshot source,
        SqliteStorageSnapshot candidateStorage)
    {
        return source with
        {
            OtherTransientBytes = checked(source.OtherTransientBytes + candidateStorage.TotalBytes)
        };
    }

    private static SqliteStorageSnapshot MeasureWithoutOperationFiles(
        string sourcePath,
        params string[] operationPaths)
    {
        var measured = SqliteStorageSnapshot.Measure(sourcePath);
        var operationBytes = operationPaths.Sum(MeasureExistingFamilyBytes);
        return measured with
        {
            OtherTransientBytes = Math.Max(0, measured.OtherTransientBytes - operationBytes)
        };
    }

    private static void MoveDatabaseFamily(string sourcePath, string destinationPath)
    {
        File.Move(sourcePath, destinationPath);
        MoveIfExists(sourcePath + "-wal", destinationPath + "-wal");
        MoveIfExists(sourcePath + "-shm", destinationPath + "-shm");
    }

    private static void MoveIfExists(string sourcePath, string destinationPath)
    {
        if (File.Exists(sourcePath))
            File.Move(sourcePath, destinationPath);
    }

    private static bool RollbackReplacement(string sourcePath, string backupPath, string candidatePath)
    {
        try
        {
            _ = TryDeleteDatabaseFamily(candidatePath);
            if (File.Exists(sourcePath))
                MoveDatabaseFamily(sourcePath, candidatePath);
            MoveDatabaseFamily(backupPath, sourcePath);
            _ = TryDeleteDatabaseFamily(candidatePath);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static long MeasureExistingFamilyBytes(string path)
    {
        long total = 0;
        foreach (var candidate in new[] { path, path + "-wal", path + "-shm" })
        {
            if (File.Exists(candidate))
                total = checked(total + new FileInfo(candidate).Length);
        }
        return total;
    }

    private static bool TryDeleteDatabaseFamily(string path)
    {
        var deleted = true;
        foreach (var candidate in new[] { path, path + "-wal", path + "-shm" })
        {
            try
            {
                if (File.Exists(candidate))
                    File.Delete(candidate);
            }
            catch
            {
                deleted = false;
            }
        }

        return deleted && !File.Exists(path) && !File.Exists(path + "-wal") && !File.Exists(path + "-shm");
    }
}
