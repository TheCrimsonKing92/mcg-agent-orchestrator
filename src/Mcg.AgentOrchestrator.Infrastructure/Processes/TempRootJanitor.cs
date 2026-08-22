namespace Mcg.AgentOrchestrator.Infrastructure;

internal enum TempRootJanitorDeleteStatus
{
    Deleted,
    AlreadyAbsent,
    Failed
}

internal sealed record TempRootJanitorDeleteResult(
    string Path,
    TempRootJanitorDeleteStatus Status,
    string? ExceptionType,
    string? FailurePath,
    int ReadOnlyAttributesCleared);

internal sealed record TempRootJanitorOwnedRoot(int ProcessId, string SharedRoot);

/// <summary>
/// Removes the process-owned test roots whose names encode the owning process id.
/// </summary>
internal static class TempRootJanitor
{
    private const string OwnedRootParentName = "mcg-tests";

    internal static string BuildOwnedRootPath(string sharedRoot, int processId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sharedRoot);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(processId);
        return Path.Combine(sharedRoot, $"p{processId:x}");
    }

    internal static TempRootJanitorDeleteResult ReapOwnedRoot(string sharedRoot, int processId) =>
        DeleteTree(BuildOwnedRootPath(sharedRoot, processId));

    internal static IReadOnlyList<TempRootJanitorDeleteResult> ReapOwnedRoots(IEnumerable<int> processIds) =>
        ReapOwnedRoots(SnapshotOwnedRoots(processIds));

    internal static IReadOnlyList<TempRootJanitorDeleteResult> ReapOwnedRoots(
        IEnumerable<int> processIds,
        IEnumerable<string> sharedRoots)
    {
        ArgumentNullException.ThrowIfNull(processIds);
        ArgumentNullException.ThrowIfNull(sharedRoots);

        var roots = sharedRoots.ToArray();
        return ReapOwnedRoots(
            processIds.SelectMany(processId =>
                roots.Select(sharedRoot => new TempRootJanitorOwnedRoot(processId, sharedRoot))));
    }

    internal static IReadOnlyList<TempRootJanitorOwnedRoot> SnapshotOwnedRoots(
        IEnumerable<int> processIds) =>
        SnapshotOwnedRoots(processIds, TryGetProcessImagePath);

    internal static IReadOnlyList<TempRootJanitorOwnedRoot> SnapshotOwnedRoots(
        IEnumerable<int> processIds,
        Func<int, string?> processImagePath)
    {
        ArgumentNullException.ThrowIfNull(processIds);
        ArgumentNullException.ThrowIfNull(processImagePath);

        var ownedRoots = new List<TempRootJanitorOwnedRoot>();
        var standardRoots = EnumerateStandardSharedRoots()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        foreach (var processId in processIds.Where(processId => processId > 0).Distinct())
        {
            ownedRoots.AddRange(standardRoots.Select(sharedRoot =>
                new TempRootJanitorOwnedRoot(processId, sharedRoot)));

            try
            {
                var imagePath = processImagePath(processId);
                var baseDirectory = string.IsNullOrWhiteSpace(imagePath)
                    ? null
                    : Path.GetDirectoryName(imagePath);
                if (!string.IsNullOrWhiteSpace(baseDirectory))
                {
                    ownedRoots.Add(new TempRootJanitorOwnedRoot(
                        processId,
                        Path.Combine(baseDirectory, ".test-tmp")));
                }
            }
            catch
            {
                // A process can exit while its image path is being captured. The standard shared root
                // remains available, and a later test-host startup remains the fallback-root backstop.
            }
        }

        return ownedRoots
            .DistinctBy(
                ownedRoot => $"{ownedRoot.ProcessId}:{ownedRoot.SharedRoot}",
                StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    internal static IReadOnlyList<TempRootJanitorDeleteResult> ReapOwnedRoots(
        IEnumerable<TempRootJanitorOwnedRoot> ownedRoots)
    {
        ArgumentNullException.ThrowIfNull(ownedRoots);

        var results = new List<TempRootJanitorDeleteResult>();
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                return results;
            }

            foreach (var ownedRoot in ownedRoots)
            {
                if (ownedRoot.ProcessId <= 0 || !Directory.Exists(ownedRoot.SharedRoot))
                {
                    continue;
                }

                results.Add(ReapOwnedRoot(ownedRoot.SharedRoot, ownedRoot.ProcessId));
            }
        }
        catch
        {
            // Process cleanup must never disturb worker termination or accounting.
        }

        return results;
    }

    internal static TempRootJanitorDeleteResult DeleteTree(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        try
        {
            Directory.Delete(path, recursive: true);
            return Deleted(path, readOnlyAttributesCleared: 0);
        }
        catch (DirectoryNotFoundException)
        {
            return AlreadyAbsent(path);
        }
        catch (UnauthorizedAccessException)
        {
            // Clearing attributes is deliberately off the successful path. Git object stores and
            // other test fixtures create read-only descendants which make recursive delete fail.
        }
        catch (Exception ex) when (IsFileSystemFailure(ex))
        {
            return Failed(path, ex, FindFirstInaccessiblePath(path), readOnlyAttributesCleared: 0);
        }

        var cleared = 0;
        try
        {
            cleared = ClearReadOnlyAttributes(path);
        }
        catch (Exception ex) when (IsFileSystemFailure(ex))
        {
            // Retry anyway: a partial clear can still make the tree deletable. If not, the delete
            // outcome below remains the authoritative failure rather than this preparation error.
        }

        try
        {
            Directory.Delete(path, recursive: true);
            return Deleted(path, cleared);
        }
        catch (DirectoryNotFoundException)
        {
            return AlreadyAbsent(path, cleared);
        }
        catch (Exception ex) when (IsFileSystemFailure(ex))
        {
            return Failed(path, ex, FindFirstInaccessiblePath(path), cleared);
        }
    }

    private static int ClearReadOnlyAttributes(string root)
    {
        if (!Directory.Exists(root))
        {
            return 0;
        }

        var cleared = 0;
        foreach (var path in Directory.EnumerateFileSystemEntries(
                     root,
                     "*",
                     SearchOption.AllDirectories).Prepend(root))
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReadOnly) == 0)
            {
                continue;
            }

            File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
            cleared++;
        }

        return cleared;
    }

    private static string? FindFirstInaccessiblePath(string root)
    {
        try
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(
                         root,
                         "*",
                         SearchOption.AllDirectories).Prepend(root))
            {
                try
                {
                    _ = File.GetAttributes(path);
                }
                catch (Exception ex) when (IsFileSystemFailure(ex))
                {
                    return path;
                }
            }
        }
        catch (Exception ex) when (IsFileSystemFailure(ex))
        {
            return root;
        }

        return root;
    }

    private static IEnumerable<string> EnumerateStandardSharedRoots()
    {
        foreach (var localAppData in new[]
                 {
                     Environment.GetEnvironmentVariable("LOCALAPPDATA"),
                     Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
                 })
        {
            if (!string.IsNullOrWhiteSpace(localAppData))
            {
                yield return Path.Combine(localAppData, "Temp", "Low", OwnedRootParentName);
            }
        }
    }

    private static string? TryGetProcessImagePath(int processId)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(processId);
            return process.MainModule?.FileName;
        }
        catch
        {
            return null;
        }
    }

    private static TempRootJanitorDeleteResult Deleted(string path, int readOnlyAttributesCleared) =>
        new(
            path,
            TempRootJanitorDeleteStatus.Deleted,
            ExceptionType: null,
            FailurePath: null,
            ReadOnlyAttributesCleared: readOnlyAttributesCleared);

    private static TempRootJanitorDeleteResult AlreadyAbsent(
        string path,
        int readOnlyAttributesCleared = 0) =>
        new(
            path,
            TempRootJanitorDeleteStatus.AlreadyAbsent,
            ExceptionType: null,
            FailurePath: null,
            ReadOnlyAttributesCleared: readOnlyAttributesCleared);

    private static TempRootJanitorDeleteResult Failed(
        string path,
        Exception exception,
        string? failurePath,
        int readOnlyAttributesCleared) =>
        new(
            path,
            TempRootJanitorDeleteStatus.Failed,
            exception.GetType().Name,
            failurePath,
            readOnlyAttributesCleared);

    private static bool IsFileSystemFailure(Exception exception) =>
        exception is UnauthorizedAccessException or IOException;
}
