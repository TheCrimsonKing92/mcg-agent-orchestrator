using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public static class OrchestratorStateStore
{
    private const int AtomicWriteAttempts = 10;
    private const string BackupExtension = ".bak";
    public const string TransactionJournalExtension = ".transactions.jsonl";
    private static readonly TimeSpan InterprocessLockTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan InterprocessLockRetryDelay = TimeSpan.FromMilliseconds(50);
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> FileLocks = new(StringComparer.OrdinalIgnoreCase);

    public static AgentOrchestratorKernel Load(string path)
    {
        return LoadAsync(path).GetAwaiter().GetResult();
    }

    public static async Task<AgentOrchestratorKernel> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        var gate = GetFileLock(path);
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(path))
            {
                return new AgentOrchestratorKernel();
            }

            return await LoadFromFileAsync(path, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    public static void Save(string path, AgentOrchestratorKernel kernel)
    {
        SaveAsync(path, kernel).GetAwaiter().GetResult();
    }

    public static async Task SaveAsync(string path, AgentOrchestratorKernel kernel, CancellationToken cancellationToken = default)
    {
        await WithExclusiveStateFileAccessAsync(
            path,
            async (fullPath, token) =>
            {
                await SaveWithoutExclusiveAccessAsync(fullPath, kernel, token);
                return true;
            },
            cancellationToken);
    }

    public static Task<T> TransactAsync<T>(
        string path,
        Func<AgentOrchestratorKernel, CancellationToken, Task<(bool ShouldSave, T Result)>> transaction,
        CancellationToken cancellationToken = default)
    {
        return TransactAsync(
            path,
            async (kernel, _, token) => await transaction(kernel, token),
            cancellationToken);
    }

    public static async Task<T> TransactAsync<T>(
        string path,
        Func<AgentOrchestratorKernel, Func<Task>, CancellationToken, Task<(bool ShouldSave, T Result)>> transaction,
        CancellationToken cancellationToken = default)
    {
        return await WithExclusiveStateFileAccessAsync(
            path,
            async (fullPath, token) =>
            {
                var transactionId = Guid.NewGuid().ToString("n");
                await AppendTransactionJournalAsync(fullPath, transactionId, "begin", token).ConfigureAwait(false);
                var kernel = File.Exists(fullPath)
                    ? await LoadFromFileAsync(fullPath, token)
                    : new AgentOrchestratorKernel();
                var checkpointSaved = false;

                async Task SaveCheckpointAsync()
                {
                    await SaveWithoutExclusiveAccessAsync(fullPath, kernel, token);
                    await AppendTransactionJournalAsync(fullPath, transactionId, "checkpoint", token).ConfigureAwait(false);
                    checkpointSaved = true;
                }

                try
                {
                    var (shouldSave, result) = await transaction(kernel, SaveCheckpointAsync, token);
                    if (shouldSave)
                    {
                        try
                        {
                            await SaveWithoutExclusiveAccessAsync(fullPath, kernel, token);
                            await AppendTransactionJournalAsync(fullPath, transactionId, "commit", token).ConfigureAwait(false);
                        }
                        catch when (checkpointSaved)
                        {
                            await AppendTransactionJournalAsync(fullPath, transactionId, "commit-after-checkpoint", token).ConfigureAwait(false);
                            return result;
                        }

                        return result;
                    }

                    await AppendTransactionJournalAsync(fullPath, transactionId, "no-change", token).ConfigureAwait(false);
                    return result;
                }
                catch (Exception ex)
                {
                    await AppendTransactionJournalAsync(fullPath, transactionId, "failed", token, ex.GetType().Name).ConfigureAwait(false);
                    throw;
                }
            },
            cancellationToken);
    }

    private static async Task AppendTransactionJournalAsync(
        string statePath,
        string transactionId,
        string status,
        CancellationToken cancellationToken,
        string? detail = null)
    {
        EnsureParentDirectory(statePath);
        var entry = JsonSerializer.Serialize(
            new
            {
                TransactionId = transactionId,
                Status = status,
                At = DateTimeOffset.UtcNow,
                Detail = detail
            },
            TransactionJournalJsonOptions());
        await File.AppendAllTextAsync(statePath + TransactionJournalExtension, entry + Environment.NewLine, cancellationToken).ConfigureAwait(false);
    }

    private static async Task SaveWithoutExclusiveAccessAsync(string path, AgentOrchestratorKernel kernel, CancellationToken cancellationToken)
    {
        EnsureParentDirectory(path);
        var json = JsonSerializer.Serialize(kernel.ExportSnapshot(), JsonOptions());
        await AtomicWriteAsync(path, json, cancellationToken);
    }

    private static async Task<T> WithExclusiveStateFileAccessAsync<T>(
        string path,
        Func<string, CancellationToken, Task<T>> action,
        CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        var gate = GetFileLock(path);
        await gate.WaitAsync(cancellationToken);
        try
        {
            EnsureParentDirectory(fullPath);
            await using var _ = await InterprocessFileLock.AcquireAsync(
                fullPath + ".lock",
                InterprocessLockTimeout,
                InterprocessLockRetryDelay,
                cancellationToken);
            return await action(fullPath, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    public static OrchestratorStateRollbackResult RestoreBackup(string path)
    {
        return RestoreBackupAsync(path).GetAwaiter().GetResult();
    }

    public static async Task<OrchestratorStateRollbackResult> RestoreBackupAsync(string path, CancellationToken cancellationToken = default)
    {
        return await WithExclusiveStateFileAccessAsync(
            path,
            async (fullPath, token) =>
            {
                var backupPath = fullPath + BackupExtension;
                if (!File.Exists(backupPath))
                {
                    throw new InvalidOperationException($"State backup was not found: {backupPath}");
                }

                var restored = await LoadSnapshotFileAsync(backupPath, token);
                var backupContent = await File.ReadAllTextAsync(backupPath, token);
                var archivedStatePath = ArchiveCurrentState(fullPath);
                await ReplacePrimaryFromBackupAsync(fullPath, backupContent, token);

                return new OrchestratorStateRollbackResult(
                    fullPath,
                    backupPath,
                    archivedStatePath,
                    restored.Goals.Count,
                    restored.HumanInputRequests.Count);
            },
            cancellationToken);
    }

    private static SemaphoreSlim GetFileLock(string path)
    {
        return FileLocks.GetOrAdd(Path.GetFullPath(path), _ => new SemaphoreSlim(1, 1));
    }

    private static void EnsureParentDirectory(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    private static async Task AtomicWriteAsync(string path, string content, CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath) ?? AppContext.BaseDirectory;
        var tempPath = Path.Combine(
            directory,
            $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():n}.tmp");

        try
        {
            await File.WriteAllTextAsync(tempPath, content, cancellationToken);
            for (var attempt = 1; attempt <= AtomicWriteAttempts; attempt++)
            {
                try
                {
                    var hadExistingState = File.Exists(fullPath);
                    if (hadExistingState)
                    {
                        File.Copy(fullPath, fullPath + BackupExtension, overwrite: true);
                    }

                    File.Move(tempPath, fullPath, overwrite: true);
                    if (!hadExistingState)
                    {
                        TryCreateInitialBackup(fullPath);
                    }

                    return;
                }
                catch (Exception ex) when (IsTransientAtomicWriteException(ex) && attempt < AtomicWriteAttempts)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(50 * attempt), cancellationToken);
                }
            }
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private static string? ArchiveCurrentState(string fullPath)
    {
        if (!File.Exists(fullPath))
        {
            return null;
        }

        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
        var archivePath = Path.Combine(
            Path.GetDirectoryName(fullPath) ?? AppContext.BaseDirectory,
            $"{Path.GetFileName(fullPath)}.pre-rollback-{stamp}-{Guid.NewGuid():n}.json");
        File.Copy(fullPath, archivePath, overwrite: false);
        return archivePath;
    }

    private static async Task ReplacePrimaryFromBackupAsync(string fullPath, string backupContent, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(fullPath) ?? AppContext.BaseDirectory;
        var tempPath = Path.Combine(
            directory,
            $".{Path.GetFileName(fullPath)}.rollback.{Guid.NewGuid():n}.tmp");

        try
        {
            await File.WriteAllTextAsync(tempPath, backupContent, cancellationToken);
            for (var attempt = 1; attempt <= AtomicWriteAttempts; attempt++)
            {
                try
                {
                    File.Move(tempPath, fullPath, overwrite: true);
                    return;
                }
                catch (Exception ex) when (IsTransientAtomicWriteException(ex) && attempt < AtomicWriteAttempts)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(50 * attempt), cancellationToken);
                }
            }
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private static bool IsTransientAtomicWriteException(Exception ex)
    {
        return ex is IOException or UnauthorizedAccessException;
    }

    private static void TryCreateInitialBackup(string fullPath)
    {
        try
        {
            File.Copy(fullPath, fullPath + BackupExtension, overwrite: true);
        }
        catch (Exception ex) when (IsTransientAtomicWriteException(ex))
        {
        }
    }

    private static async Task<AgentOrchestratorKernel> LoadFromFileAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            return await LoadSnapshotFileAsync(path, cancellationToken);
        }
        catch (JsonException) when (File.Exists(path + BackupExtension))
        {
            return await LoadSnapshotFileAsync(path + BackupExtension, cancellationToken);
        }
    }

    private static async Task<AgentOrchestratorKernel> LoadSnapshotFileAsync(string path, CancellationToken cancellationToken)
    {
        var json = await File.ReadAllTextAsync(path, cancellationToken);
        var snapshot = JsonSerializer.Deserialize<OrchestratorSnapshot>(json, JsonOptions())
            ?? new OrchestratorSnapshot([], []);
        return AgentOrchestratorKernel.FromSnapshot(snapshot);
    }

    private static JsonSerializerOptions JsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private static JsonSerializerOptions TransactionJournalJsonOptions()
    {
        var options = new JsonSerializerOptions();
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}

public sealed record OrchestratorStateRollbackResult(
    string StatePath,
    string BackupPath,
    string? ArchivedStatePath,
    int GoalCount,
    int HumanInputRequestCount);

public interface IOrchestratorStateRepository
{
    Task<AgentOrchestratorKernel> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(AgentOrchestratorKernel kernel, CancellationToken cancellationToken = default);

    // Cheap, hydration-free goal listing for read-only surfaces (the `goals` command, prefix
    // resolution). The SQLite backend reads the indexed metadata columns directly; the file
    // backend projects from a full load (no perf win, kept correct so the interface stays honest).
    Task<IReadOnlyList<GoalSummary>> ListGoalMetadataAsync(CancellationToken cancellationToken = default);
}

public interface ITransactionalOrchestratorStateRepository : IOrchestratorStateRepository
{
    Task<T> TransactAsync<T>(
        Func<AgentOrchestratorKernel, CancellationToken, Task<(bool ShouldSave, T Result)>> transaction,
        CancellationToken cancellationToken = default);

    Task<T> TransactAsync<T>(
        Func<AgentOrchestratorKernel, Func<Task>, CancellationToken, Task<(bool ShouldSave, T Result)>> transaction,
        CancellationToken cancellationToken = default);
}

public sealed class FileOrchestratorStateRepository : ITransactionalOrchestratorStateRepository
{
    private readonly string _path;

    public FileOrchestratorStateRepository(string path)
    {
        _path = path;
    }

    public Task<AgentOrchestratorKernel> LoadAsync(CancellationToken cancellationToken = default)
    {
        return OrchestratorStateStore.LoadAsync(_path, cancellationToken);
    }

    public Task SaveAsync(AgentOrchestratorKernel kernel, CancellationToken cancellationToken = default)
    {
        return OrchestratorStateStore.SaveAsync(_path, kernel, cancellationToken);
    }

    public async Task<IReadOnlyList<GoalSummary>> ListGoalMetadataAsync(CancellationToken cancellationToken = default)
    {
        var kernel = await OrchestratorStateStore.LoadAsync(_path, cancellationToken);
        return kernel.Goals
            .Select(goal => (goal, at: goal.Timeline.LastOrDefault()?.OccurredAt ?? DateTimeOffset.MinValue))
            .OrderByDescending(item => item.at)
            .Select(item => new GoalSummary(
                item.goal.Id.Value,
                item.goal.Status.ToString(),
                item.goal.Objective,
                item.at.ToString("O")))
            .ToList();
    }

    public Task<T> TransactAsync<T>(
        Func<AgentOrchestratorKernel, CancellationToken, Task<(bool ShouldSave, T Result)>> transaction,
        CancellationToken cancellationToken = default)
    {
        return OrchestratorStateStore.TransactAsync(_path, transaction, cancellationToken);
    }

    public Task<T> TransactAsync<T>(
        Func<AgentOrchestratorKernel, Func<Task>, CancellationToken, Task<(bool ShouldSave, T Result)>> transaction,
        CancellationToken cancellationToken = default)
    {
        return OrchestratorStateStore.TransactAsync(_path, transaction, cancellationToken);
    }
}

public sealed class InterprocessFileLock : IDisposable, IAsyncDisposable
{
    private readonly FileStream _stream;

    private InterprocessFileLock(FileStream stream)
    {
        _stream = stream;
    }

    public static async Task<InterprocessFileLock> AcquireAsync(
        string lockPath,
        TimeSpan timeout,
        TimeSpan retryDelay,
        CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(lockPath);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var stream = new FileStream(fullPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                var owner = Encoding.UTF8.GetBytes(
                    $"pid={Environment.ProcessId}; machine={Environment.MachineName}; acquired={DateTimeOffset.UtcNow:O}{Environment.NewLine}");
                stream.SetLength(0);
                await stream.WriteAsync(owner, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Position = 0;
                return new InterprocessFileLock(stream);
            }
            catch (Exception ex) when (IsLockContention(ex) && elapsed.Elapsed < timeout)
            {
                var delay = retryDelay < timeout - elapsed.Elapsed
                    ? retryDelay
                    : timeout - elapsed.Elapsed;
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, cancellationToken);
                }
            }
            catch (Exception ex) when (IsLockContention(ex))
            {
                throw new TimeoutException($"Timed out waiting for exclusive file lock: {fullPath}", ex);
            }
        }
    }

    public void Dispose()
    {
        _stream.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        await _stream.DisposeAsync();
    }

    private static bool IsLockContention(Exception ex)
    {
        return ex is IOException or UnauthorizedAccessException;
    }
}
