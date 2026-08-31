using System.Runtime.CompilerServices;
using Mcg.AgentOrchestrator.Infrastructure;

// Infrastructure.Tests builds a self-contained Microsoft.Testing.Platform executable.
// Select a process-local temp root before tests create scratch files, but commit it only
// after its Low label (where required), child-directory write, and cleanup are verified.
internal static class AssemblyTempRedirect
{
    internal const string LowInheritableLevel = "(OI)(CI)L";
    internal const int MaxRootsReapedPerProcess = 32;

    [ModuleInitializer]
    internal static void Install()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var fileSystem = new PhysicalTempRootFileSystem();
        var labeler = new IcaclsIntegrityLabeler();
        var selection = SelectWritableRoot(
            EnumerateCandidateRoots(),
            candidate => TryPrepareRoot(
                candidate,
                isWindows: true,
                fileSystem,
                labeler,
                $".write-probe-{Guid.NewGuid():N}"));

        Console.Error.WriteLine(FormatDiagnostic(selection));
        if (selection.SelectedRoot is null)
        {
            return;
        }

        Environment.SetEnvironmentVariable("TMP", selection.SelectedRoot, EnvironmentVariableTarget.Process);
        Environment.SetEnvironmentVariable("TEMP", selection.SelectedRoot, EnvironmentVariableTarget.Process);

        // The root is owned by this process: delete it on exit, and reap roots whose owning
        // process is gone. Both are required — exit handlers do not run for killed processes,
        // and this repository cancels dispatches and times out gates routinely.
        ReapOrphanedRoots(selection.SelectedRoot);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => TryDeleteTree(selection.SelectedRoot);
    }

    internal static IReadOnlyList<string> SelectReapableRoots(
        IEnumerable<string> siblingDirectoryNames,
        int currentProcessId,
        Func<int, bool> isProcessAlive)
    {
        ArgumentNullException.ThrowIfNull(siblingDirectoryNames);
        ArgumentNullException.ThrowIfNull(isProcessAlive);

        var reapable = new List<string>();
        foreach (var name in siblingDirectoryNames)
        {
            // Anything not matching the owned-root shape belongs to someone else. Leave it.
            if (!TryParseProcessTempRootName(name, out var processId))
            {
                continue;
            }

            if (processId == currentProcessId || isProcessAlive(processId))
            {
                continue;
            }

            reapable.Add(name);
        }

        return reapable;
    }

    internal static bool TryParseProcessTempRootName(string? directoryName, out int processId)
    {
        processId = 0;
        if (string.IsNullOrEmpty(directoryName) || directoryName[0] != 'p')
        {
            return false;
        }

        return int.TryParse(
                directoryName.AsSpan(1),
                System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture,
                out processId)
            && processId > 0;
    }

    private static void ReapOrphanedRoots(string selectedRoot) =>
        ReapOrphanedRoots(
            selectedRoot,
            WindowsNativeProcessInspection.Read,
            TryDeleteTree);

    internal static void ReapOrphanedRoots(
        string selectedRoot,
        Func<IEnumerable<int>?, WindowsNativeProcessInspection.ProcessInspectionResult> inspect,
        Action<string> deleteTree)
    {
        ArgumentNullException.ThrowIfNull(inspect);
        ArgumentNullException.ThrowIfNull(deleteTree);
        try
        {
            var sharedRoot = Path.GetDirectoryName(selectedRoot);
            if (string.IsNullOrEmpty(sharedRoot) || !Directory.Exists(sharedRoot))
            {
                return;
            }

            var siblings = Directory.EnumerateDirectories(sharedRoot)
                .Select(Path.GetFileName)
                .OfType<string>()
                .ToArray();
            var liveProcessIds = SnapshotLiveProcessIds(siblings, Environment.ProcessId, inspect);
            var reapableByProcess = SelectReapableRoots(
                siblings,
                Environment.ProcessId,
                liveProcessIds.Contains);
            var bounded = reapableByProcess
                          .Distinct(StringComparer.OrdinalIgnoreCase)
                          .OrderBy(name => TryGetLastWriteUtc(
                              name,
                              candidate => Directory.GetLastWriteTimeUtc(Path.Combine(sharedRoot, candidate))))
                          .Take(MaxRootsReapedPerProcess)
                          .ToArray();
            foreach (var candidate in RevalidateExitedRoots(bounded, inspect))
            {
                deleteTree(Path.Combine(sharedRoot, candidate));
            }
        }
        catch (Exception)
        {
            // Reaping is best-effort; never fail a test run over temp housekeeping. This runs from
            // a ModuleInitializer, so anything escaping here kills the apphost before discovery and
            // the lane reports zero tests with no failing test to point at.
        }
    }

    // Use one exact-candidate native snapshot per phase. The second read closes the selection-to-delete
    // PID-reuse gap; inaccessible or otherwise ambiguous observations retain the root.
    private static HashSet<int> SnapshotLiveProcessIds(
        IEnumerable<string> siblingDirectoryNames,
        int currentProcessId,
        Func<IEnumerable<int>?, WindowsNativeProcessInspection.ProcessInspectionResult> inspect)
    {
        var relevantProcessIds = new HashSet<int>();
        foreach (var name in siblingDirectoryNames)
        {
            if (TryParseProcessTempRootName(name, out var processId) && processId != currentProcessId)
            {
                relevantProcessIds.Add(processId);
            }
        }

        var inspection = inspect(relevantProcessIds);
        if (inspection.Failure is not null)
        {
            return relevantProcessIds;
        }

        return relevantProcessIds
            .Where(processId =>
                !inspection.Records.TryGetValue(processId, out var record) ||
                record.Status != ProcessInspectionStatus.Exited)
            .ToHashSet();
    }

    private static IReadOnlyList<string> RevalidateExitedRoots(
        IEnumerable<string> boundedCandidates,
        Func<IEnumerable<int>?, WindowsNativeProcessInspection.ProcessInspectionResult> inspect)
    {
        var candidates = boundedCandidates
            .Select(name => new { Name = name, Parsed = TryParseProcessTempRootName(name, out var pid), Pid = pid })
            .Where(candidate => candidate.Parsed && candidate.Pid != Environment.ProcessId)
            .ToArray();
        if (candidates.Length == 0)
        {
            return [];
        }

        var inspection = inspect(
            candidates.Select(candidate => candidate.Pid).Distinct().ToArray());
        if (inspection.Failure is not null)
        {
            return [];
        }

        return candidates
            .Where(candidate =>
                inspection.Records.TryGetValue(candidate.Pid, out var record) &&
                record.Status == ProcessInspectionStatus.Exited)
            .Select(candidate => candidate.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static void TryDeleteTree(string path)
    {
        try
        {
            _ = TempRootJanitor.DeleteTree(path);
        }
        catch
        {
            // This path is called by the module initializer and process-exit handler.
        }
    }

    internal static TempRootSelectionResult SelectWritableRoot(
        IEnumerable<TempRootCandidate> candidates,
        Func<TempRootCandidate, TempRootPreparationResult> tryPrepareRoot)
    {
        var rejections = new List<TempRootRejection>();
        foreach (var candidate in candidates)
        {
            var preparation = tryPrepareRoot(candidate);
            if (preparation.Succeeded)
            {
                return new TempRootSelectionResult(candidate.Path, rejections);
            }

            rejections.Add(new TempRootRejection(candidate.Path, preparation.Reasons));
        }

        return new TempRootSelectionResult(null, rejections);
    }

    internal static TempRootPreparationResult TryPrepareRoot(
        TempRootCandidate candidate,
        bool isWindows,
        ITempRootFileSystem fileSystem,
        IWorkerIntegrityLabeler labeler,
        string probeDirectoryName)
    {
        try
        {
            fileSystem.CreateDirectory(candidate.Path);
        }
        catch (Exception ex) when (IsFileSystemFailure(ex))
        {
            return TempRootPreparationResult.Rejected(TempRootRejectionReason.Create);
        }

        if (isWindows && candidate.RequiresLowLabel && !EnsureLowLabel(candidate.Path, labeler))
        {
            return TempRootPreparationResult.Rejected(TempRootRejectionReason.Label);
        }

        var reasons = new List<TempRootRejectionReason>();
        var probeDirectory = Path.Combine(candidate.Path, probeDirectoryName);
        var probeFile = Path.Combine(probeDirectory, "probe.tmp");
        try
        {
            fileSystem.CreateDirectory(probeDirectory);
            fileSystem.CreateProbeFile(probeFile);
        }
        catch (Exception ex) when (IsFileSystemFailure(ex))
        {
            reasons.Add(TempRootRejectionReason.Write);
        }
        finally
        {
            var cleanupFailed = false;
            try
            {
                fileSystem.DeleteFile(probeFile);
            }
            catch (Exception ex) when (IsFileSystemFailure(ex))
            {
                cleanupFailed = true;
            }

            try
            {
                fileSystem.DeleteDirectory(probeDirectory);
            }
            catch (DirectoryNotFoundException)
            {
                // No probe directory remains to poison a later lane.
            }
            catch (Exception ex) when (IsFileSystemFailure(ex))
            {
                cleanupFailed = true;
            }

            if (cleanupFailed)
            {
                reasons.Add(TempRootRejectionReason.Cleanup);
            }
        }

        return reasons.Count == 0
            ? TempRootPreparationResult.Success
            : new TempRootPreparationResult(false, reasons);
    }

    internal static string FormatDiagnostic(TempRootSelectionResult selection)
    {
        var selected = selection.SelectedRoot ?? "<none>";
        var rejected = selection.Rejections.Count == 0
            ? "<none>"
            : string.Join(
                "|",
                selection.Rejections.Select(rejection =>
                    $"{rejection.Path}:{string.Join('+', rejection.Reasons.Select(ReasonToken))}"));
        return $"assembly-temp-redirect selected={selected} rejected={rejected}";
    }

    internal static string BuildProcessTempRoot(string sharedRoot, int processId)
    {
        return TempRootJanitor.BuildOwnedRootPath(sharedRoot, processId);
    }

    private static DateTime TryGetLastWriteUtc(string name, Func<string, DateTime> lastWriteUtc)
    {
        try
        {
            return lastWriteUtc(name);
        }
        catch (Exception ex) when (IsFileSystemFailure(ex))
        {
            return DateTime.MaxValue;
        }
    }

    private static bool EnsureLowLabel(string path, IWorkerIntegrityLabeler labeler)
    {
        try
        {
            var state = labeler.Query(path);
            if (state.Exists && state.Low && state.Inheritable)
            {
                return true;
            }

            if (!labeler.SetIntegrity(path, LowInheritableLevel, recursive: false))
            {
                return false;
            }

            state = labeler.Query(path);
            return state.Exists && state.Low && state.Inheritable;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsFileSystemFailure(Exception exception) =>
        exception is UnauthorizedAccessException or IOException;

    private static string ReasonToken(TempRootRejectionReason reason) => reason switch
    {
        TempRootRejectionReason.Create => "create",
        TempRootRejectionReason.Label => "label",
        TempRootRejectionReason.Write => "write",
        TempRootRejectionReason.Cleanup => "cleanup",
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, null)
    };

    private static IEnumerable<TempRootCandidate> EnumerateCandidateRoots()
    {
        foreach (var localAppData in new[]
                 {
                     Environment.GetEnvironmentVariable("LOCALAPPDATA"),
                     Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
                 })
        {
            if (!string.IsNullOrEmpty(localAppData))
            {
                yield return new TempRootCandidate(
                    BuildProcessTempRoot(
                        Path.Combine(localAppData, "Temp", "Low", "mcg-tests"),
                        Environment.ProcessId),
                    RequiresLowLabel: true);
            }
        }

        yield return new TempRootCandidate(
            BuildProcessTempRoot(Path.Combine(AppContext.BaseDirectory, ".test-tmp"), Environment.ProcessId),
            RequiresLowLabel: false);
    }
}

internal sealed record TempRootCandidate(string Path, bool RequiresLowLabel);

internal enum TempRootRejectionReason
{
    Create,
    Label,
    Write,
    Cleanup
}

internal sealed record TempRootPreparationResult(
    bool Succeeded,
    IReadOnlyList<TempRootRejectionReason> Reasons)
{
    internal static TempRootPreparationResult Success { get; } = new(true, []);

    internal static TempRootPreparationResult Rejected(TempRootRejectionReason reason) =>
        new(false, [reason]);
}

internal sealed record TempRootRejection(
    string Path,
    IReadOnlyList<TempRootRejectionReason> Reasons);

internal sealed record TempRootSelectionResult(
    string? SelectedRoot,
    IReadOnlyList<TempRootRejection> Rejections);

internal interface ITempRootFileSystem
{
    void CreateDirectory(string path);

    void CreateProbeFile(string path);

    void DeleteFile(string path);

    void DeleteDirectory(string path);
}

internal sealed class PhysicalTempRootFileSystem : ITempRootFileSystem
{
    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    public void CreateProbeFile(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 1,
            FileOptions.WriteThrough);
        stream.WriteByte(0);
        stream.Flush(flushToDisk: true);
    }

    public void DeleteFile(string path) => File.Delete(path);

    public void DeleteDirectory(string path) => Directory.Delete(path, recursive: false);
}
