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

internal sealed record TempRootJanitorOwnedRoot(
    int ProcessId,
    string SharedRoot,
    ProcessInspectionRecord? CapturedOwner,
    string CaptureSource,
    string? CandidatePath = null);

internal enum TempRootJanitorReapDisposition
{
    Deleted,
    AlreadyAbsent,
    RetainedLiveOwner,
    RetainedRecycledPid,
    RetainedUnknownOwner,
    RejectedPath,
    DeleteFailed
}

internal sealed record TempRootJanitorReapResult(
    int ProcessId,
    string SharedRoot,
    string Path,
    string CaptureSource,
    ProcessInspectionRecord? CapturedOwner,
    ProcessInspectionRecord? ObservedOwner,
    TempRootJanitorReapDisposition Disposition,
    TempRootJanitorDeleteResult? DeleteResult);

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

    internal static IReadOnlyList<TempRootJanitorReapResult> ReapOwnedRoots(IEnumerable<int> processIds) =>
        ReapOwnedRoots(SnapshotOwnedRoots(processIds));

    internal static IReadOnlyList<TempRootJanitorReapResult> ReapOwnedRoots(
        IEnumerable<int> processIds,
        IEnumerable<string> sharedRoots)
    {
        ArgumentNullException.ThrowIfNull(processIds);
        ArgumentNullException.ThrowIfNull(sharedRoots);

        var ids = processIds.Where(processId => processId > 0).Distinct().ToArray();
        var captured = CaptureOwners(ids, WindowsNativeProcessInspection.Read);
        var roots = sharedRoots.ToArray();
        return ReapOwnedRoots(ids.SelectMany(processId => roots.Select(sharedRoot =>
            new TempRootJanitorOwnedRoot(
                processId,
                sharedRoot,
                captured.GetValueOrDefault(processId),
                "explicit-shared-root",
                string.IsNullOrWhiteSpace(sharedRoot)
                    ? null
                    : BuildOwnedRootPath(sharedRoot, processId)))));
    }

    internal static IReadOnlyList<TempRootJanitorOwnedRoot> SnapshotOwnedRoots(
        IEnumerable<int> processIds) =>
        SnapshotOwnedRoots(processIds, WindowsNativeProcessInspection.Read);

    internal static IReadOnlyList<TempRootJanitorOwnedRoot> SnapshotOwnedRoots(
        IEnumerable<int> processIds,
        Func<IEnumerable<int>?, WindowsNativeProcessInspection.ProcessInspectionResult> inspect)
    {
        ArgumentNullException.ThrowIfNull(processIds);
        ArgumentNullException.ThrowIfNull(inspect);

        var ids = processIds.Where(processId => processId > 0).Distinct().ToArray();
        var captured = CaptureOwners(ids, inspect);
        var ownedRoots = new List<TempRootJanitorOwnedRoot>();
        var standardRoots = EnumerateStandardSharedRoots()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        foreach (var processId in ids)
        {
            captured.TryGetValue(processId, out var capturedOwner);
            ownedRoots.AddRange(standardRoots.Select(sharedRoot =>
                new TempRootJanitorOwnedRoot(
                    processId,
                    sharedRoot,
                    capturedOwner,
                    "standard-shared-root",
                    BuildOwnedRootPath(sharedRoot, processId))));

            try
            {
                var imagePath = capturedOwner?.ExecutablePath;
                var baseDirectory = string.IsNullOrWhiteSpace(imagePath)
                    ? null
                    : Path.GetDirectoryName(imagePath);
                if (!string.IsNullOrWhiteSpace(baseDirectory))
                {
                    var fallbackRoot = Path.Combine(baseDirectory, ".test-tmp");
                    ownedRoots.Add(new TempRootJanitorOwnedRoot(
                        processId,
                        fallbackRoot,
                        capturedOwner,
                        "image-fallback-root",
                        BuildOwnedRootPath(fallbackRoot, processId)));
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

    internal static IReadOnlyList<TempRootJanitorReapResult> ReapOwnedRoots(
        IEnumerable<TempRootJanitorOwnedRoot> ownedRoots) =>
        ReapOwnedRoots(ownedRoots, WindowsNativeProcessInspection.Read, DeleteTree);

    internal static IReadOnlyList<TempRootJanitorReapResult> ReapOwnedRoots(
        IEnumerable<TempRootJanitorOwnedRoot> ownedRoots,
        Func<IEnumerable<int>?, WindowsNativeProcessInspection.ProcessInspectionResult> inspect,
        Func<string, TempRootJanitorDeleteResult> deleteTree)
    {
        ArgumentNullException.ThrowIfNull(ownedRoots);
        ArgumentNullException.ThrowIfNull(inspect);
        ArgumentNullException.ThrowIfNull(deleteTree);

        var results = new List<TempRootJanitorReapResult>();
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                return results;
            }

            var candidates = ownedRoots
                .Where(ownedRoot => ownedRoot.ProcessId > 0)
                .DistinctBy(
                    ownedRoot => $"{ownedRoot.ProcessId}:{ownedRoot.SharedRoot}:{ownedRoot.CandidatePath}",
                    StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var observed = CaptureOwners(candidates.Select(candidate => candidate.ProcessId), inspect);
            foreach (var ownedRoot in candidates)
            {
                var path = ownedRoot.CandidatePath ??
                    (string.IsNullOrWhiteSpace(ownedRoot.SharedRoot)
                        ? ownedRoot.SharedRoot ?? string.Empty
                        : BuildOwnedRootPath(ownedRoot.SharedRoot, ownedRoot.ProcessId));
                observed.TryGetValue(ownedRoot.ProcessId, out var observedOwner);
                TempRootJanitorReapResult result;
                if (string.IsNullOrWhiteSpace(ownedRoot.SharedRoot) ||
                    !IsDirectChild(ownedRoot.SharedRoot, path))
                {
                    result = Retained(
                        ownedRoot,
                        path,
                        observedOwner,
                        TempRootJanitorReapDisposition.RejectedPath);
                }
                else if (!HasCompleteIdentity(ownedRoot.CapturedOwner) || observedOwner is null)
                {
                    result = Retained(
                        ownedRoot,
                        path,
                        observedOwner,
                        TempRootJanitorReapDisposition.RetainedUnknownOwner);
                }
                else if (observedOwner.Status == ProcessInspectionStatus.Available)
                {
                    result = Retained(
                        ownedRoot,
                        path,
                        observedOwner,
                        WindowsNativeProcessInspection.MatchesIdentity(ownedRoot.CapturedOwner!, observedOwner)
                            ? TempRootJanitorReapDisposition.RetainedLiveOwner
                            : TempRootJanitorReapDisposition.RetainedRecycledPid);
                }
                else if (observedOwner.Status != ProcessInspectionStatus.Exited)
                {
                    result = Retained(
                        ownedRoot,
                        path,
                        observedOwner,
                        TempRootJanitorReapDisposition.RetainedUnknownOwner);
                }
                else
                {
                    TempRootJanitorDeleteResult deletion;
                    try
                    {
                        deletion = deleteTree(path) ?? Failed(
                            path,
                            new InvalidOperationException("The delete seam returned no result."),
                            path,
                            readOnlyAttributesCleared: 0);
                    }
                    catch (Exception ex)
                    {
                        deletion = Failed(path, ex, path, readOnlyAttributesCleared: 0);
                    }
                    result = new TempRootJanitorReapResult(
                        ownedRoot.ProcessId,
                        ownedRoot.SharedRoot,
                        path,
                        ownedRoot.CaptureSource,
                        ownedRoot.CapturedOwner,
                        observedOwner,
                        deletion.Status switch
                        {
                            TempRootJanitorDeleteStatus.Deleted => TempRootJanitorReapDisposition.Deleted,
                            TempRootJanitorDeleteStatus.AlreadyAbsent => TempRootJanitorReapDisposition.AlreadyAbsent,
                            _ => TempRootJanitorReapDisposition.DeleteFailed
                        },
                        deletion);
                }

                results.Add(result);
            }
        }
        catch
        {
            // Process cleanup must never disturb worker termination or accounting.
        }

        return results;
    }

    internal static string FormatDiagnostic(TempRootJanitorReapResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return $"temp-root-janitor source={Bound(result.CaptureSource, 48)} pid={result.ProcessId} " +
               $"path={Bound(result.Path, 260)} captured={FormatIdentity(result.CapturedOwner)} " +
               $"observed={FormatIdentity(result.ObservedOwner)} disposition={result.Disposition}";
    }

    private static IReadOnlyDictionary<int, ProcessInspectionRecord> CaptureOwners(
        IEnumerable<int> processIds,
        Func<IEnumerable<int>?, WindowsNativeProcessInspection.ProcessInspectionResult> inspect)
    {
        var ids = processIds.Where(processId => processId > 0).Distinct().ToArray();
        if (ids.Length == 0)
        {
            return new Dictionary<int, ProcessInspectionRecord>();
        }

        try
        {
            var result = inspect(ids);
            return result.Failure is null
                ? result.Records
                : new Dictionary<int, ProcessInspectionRecord>();
        }
        catch
        {
            return new Dictionary<int, ProcessInspectionRecord>();
        }
    }

    private static bool HasCompleteIdentity(ProcessInspectionRecord? owner) =>
        owner is
        {
            Status: ProcessInspectionStatus.Available,
            StartedAt: not null,
            ExecutablePath: not null,
            CommandLine: not null
        } &&
        !string.IsNullOrWhiteSpace(owner.ExecutablePath) &&
        !string.IsNullOrWhiteSpace(owner.CommandLine);

    private static bool IsDirectChild(string sharedRoot, string candidatePath)
    {
        try
        {
            var parent = Path.GetFullPath(sharedRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var candidate = Path.GetFullPath(candidatePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.Equals(Path.GetDirectoryName(candidate), parent, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static TempRootJanitorReapResult Retained(
        TempRootJanitorOwnedRoot ownedRoot,
        string path,
        ProcessInspectionRecord? observedOwner,
        TempRootJanitorReapDisposition disposition) =>
        new(
            ownedRoot.ProcessId,
            ownedRoot.SharedRoot,
            path,
            ownedRoot.CaptureSource,
            ownedRoot.CapturedOwner,
            observedOwner,
            disposition,
            DeleteResult: null);

    private static string FormatIdentity(ProcessInspectionRecord? identity) =>
        identity is null
            ? "missing"
            : $"{identity.Status}/{identity.StartedAt?.ToUnixTimeMilliseconds().ToString() ?? "none"}/" +
              $"{Bound(Path.GetFileName(identity.ExecutablePath), 64)}";

    private static string Bound(string? value, int maximumLength) =>
        string.IsNullOrEmpty(value)
            ? "none"
            : value.Length <= maximumLength ? value : value[..maximumLength];

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
