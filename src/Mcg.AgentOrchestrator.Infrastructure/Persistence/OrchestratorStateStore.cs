using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public static class OrchestratorStateStore
{
    private const int AtomicWriteAttempts = 10;
    private const string BackupExtension = ".bak";
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
        var gate = GetFileLock(path);
        await gate.WaitAsync(cancellationToken);
        try
        {
            EnsureParentDirectory(path);
            var json = JsonSerializer.Serialize(kernel.ExportSnapshot(), JsonOptions());
            await AtomicWriteAsync(path, json, cancellationToken);
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
        var gate = GetFileLock(path);
        await gate.WaitAsync(cancellationToken);
        try
        {
            var fullPath = Path.GetFullPath(path);
            var backupPath = fullPath + BackupExtension;
            if (!File.Exists(backupPath))
            {
                throw new InvalidOperationException($"State backup was not found: {backupPath}");
            }

            var restored = await LoadSnapshotFileAsync(backupPath, cancellationToken);
            EnsureParentDirectory(fullPath);
            var backupContent = await File.ReadAllTextAsync(backupPath, cancellationToken);
            var archivedStatePath = ArchiveCurrentState(fullPath);
            await ReplacePrimaryFromBackupAsync(fullPath, backupContent, cancellationToken);

            return new OrchestratorStateRollbackResult(
                fullPath,
                backupPath,
                archivedStatePath,
                restored.Goals.Count,
                restored.HumanInputRequests.Count);
        }
        finally
        {
            gate.Release();
        }
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
}

public sealed class FileOrchestratorStateRepository : IOrchestratorStateRepository
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
}
