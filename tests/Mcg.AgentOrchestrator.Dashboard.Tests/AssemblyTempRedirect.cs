using System.Runtime.CompilerServices;
using Mcg.AgentOrchestrator.Infrastructure;

// Infrastructure.Tests builds a self-contained Microsoft.Testing.Platform executable.
// Select a process-local temp root before tests create scratch files, but commit it only
// after its Low label (where required), child-directory write, and cleanup are verified.
internal static class AssemblyTempRedirect
{
    internal const string LowInheritableLevel = "(OI)(CI)L";
    internal const int MaxRootsReapedPerProcess = 32;
    private const string RootLeaseSuffix = ".owner.lock";
    private static FileStream? processRootLease;

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

        var rootLease = TryAcquireOwnedRootLease(selection.SelectedRoot);
        if (rootLease is null)
        {
            return;
        }

        processRootLease = rootLease;

        Environment.SetEnvironmentVariable("TMP", selection.SelectedRoot, EnvironmentVariableTarget.Process);
        Environment.SetEnvironmentVariable("TEMP", selection.SelectedRoot, EnvironmentVariableTarget.Process);

        // The root is owned by this process: delete it on exit, and reap roots whose owning
        // process is gone. Both are required — exit handlers do not run for killed processes,
        // and this repository cancels dispatches and times out gates routinely.
        ReapOrphanedRoots(selection.SelectedRoot);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => ReleaseOwnedRoot(selection.SelectedRoot);
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
            TryDeleteTree,
            Console.Error.WriteLine);

    internal static void ReapOrphanedRoots(
        string selectedRoot,
        Func<IEnumerable<int>?, WindowsNativeProcessInspection.ProcessInspectionResult> inspect,
        Func<string, TempRootJanitorDeleteResult> deleteTree,
        Action<string>? writeReceipt)
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
            var destroyedOwners = new List<TempRootApparatusDestroyedOwner>();
            foreach (var candidate in RevalidateExitedRoots(bounded, inspect))
            {
                var path = Path.Combine(sharedRoot, candidate.Name);
                var capturedOwner = TryReadOwnedRootIdentity(path, candidate.ProcessId);
                var outcome = deleteTree(path);
                if (outcome.Status == TempRootJanitorDeleteStatus.Deleted && capturedOwner is not null)
                {
                    destroyedOwners.Add(capturedOwner);
                }
                TryWriteReceipt(
                    writeReceipt,
                    () => FormatReapReceipt(
                        Environment.ProcessId,
                        candidate.ProcessId,
                        path,
                        candidate.Observation,
                        outcome));
            }

            TempRootApparatusLossReceiptStore.RecordDeletedOwners(sharedRoot, destroyedOwners);
        }
        catch (Exception)
        {
            // Reaping is best-effort; never fail a test run over temp housekeeping. This runs from
            // a ModuleInitializer, so anything escaping here kills the apphost before discovery and
            // the lane reports zero tests with no failing test to point at.
        }
    }

    internal static string RootLeasePath(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        var fullPath = Path.GetFullPath(rootPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var parent = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException($"Temp root '{rootPath}' has no parent directory.");
        return Path.Combine(parent, $".{Path.GetFileName(fullPath)}{RootLeaseSuffix}");
    }

    internal static FileStream? TryAcquireOwnedRootLease(string rootPath)
    {
        FileStream? lease = null;
        try
        {
            lease = new FileStream(
                RootLeasePath(rootPath),
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.Read,
                bufferSize: 256,
                FileOptions.WriteThrough);
            lease.SetLength(0);
            using (var writer = new StreamWriter(lease, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                using var process = System.Diagnostics.Process.GetCurrentProcess();
                writer.Write($"pid={Environment.ProcessId};startedAt={process.StartTime.ToUniversalTime():O};path={Environment.ProcessPath}");
                writer.Flush();
            }

            lease.Flush(flushToDisk: true);
            lease.Position = 0;
            return lease;
        }
        catch
        {
            lease?.Dispose();
            return null;
        }
    }

    internal static TempRootApparatusDestroyedOwner? TryReadOwnedRootIdentity(
        string rootPath,
        int expectedProcessId)
    {
        try
        {
            using var stream = new FileStream(
                RootLeasePath(rootPath),
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
            var fields = reader.ReadToEnd()
                .Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Select(field => field.Split('=', 2))
                .Where(field => field.Length == 2)
                .ToDictionary(field => field[0], field => field[1], StringComparer.Ordinal);
            return fields.TryGetValue("pid", out var pidText) &&
                int.TryParse(pidText, out var processId) &&
                processId == expectedProcessId &&
                fields.TryGetValue("startedAt", out var startedAtText) &&
                DateTimeOffset.TryParseExact(
                    startedAtText,
                    "O",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind,
                    out var startedAt)
                ? new TempRootApparatusDestroyedOwner(processId, startedAt, rootPath)
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or ArgumentException)
        {
            return null;
        }
    }

    private static void ReleaseOwnedRoot(string rootPath)
    {
        Interlocked.Exchange(ref processRootLease, null)?.Dispose();
        _ = TryDeleteTree(rootPath);
        try { File.Delete(RootLeasePath(rootPath)); } catch { }
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

    private static IReadOnlyList<RevalidatedTempRoot> RevalidateExitedRoots(
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

        var revalidated = new List<RevalidatedTempRoot>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates)
        {
            if (seen.Add(candidate.Name) &&
                inspection.Records.TryGetValue(candidate.Pid, out var record) &&
                record.Status == ProcessInspectionStatus.Exited)
            {
                revalidated.Add(new RevalidatedTempRoot(candidate.Name, candidate.Pid, record));
            }
        }

        return revalidated;
    }

    private static TempRootJanitorDeleteResult TryDeleteTree(string path)
    {
        try
        {
            return TempRootJanitor.DeleteTree(path);
        }
        catch (Exception ex)
        {
            // This path is called by the module initializer and process-exit handler.
            return new TempRootJanitorDeleteResult(
                path,
                TempRootJanitorDeleteStatus.Failed,
                ex.GetType().Name,
                path,
                ReadOnlyAttributesCleared: 0);
        }
    }

    internal static string FormatReapReceipt(
        int actorProcessId,
        int candidateProcessId,
        string path,
        ProcessInspectionRecord observation,
        TempRootJanitorDeleteResult outcome)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(actorProcessId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(candidateProcessId);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(outcome);

        return "assembly-temp-reaper " +
            $"actorPid={actorProcessId} " +
            $"candidatePid={candidateProcessId} " +
            $"path={QuoteDiagnostic(path)} " +
            $"observedPid={observation.ProcessId} " +
            $"observedStatus={observation.Status} " +
            $"observedName={QuoteDiagnostic(string.IsNullOrWhiteSpace(observation.Name) ? "none" : observation.Name)} " +
            $"observedStartedAt={QuoteDiagnostic(observation.StartedAt?.ToString("O") ?? "none")} " +
            $"observedExecutablePath={QuoteDiagnostic(observation.ExecutablePath ?? "none")} " +
            $"deleteStatus={outcome.Status} " +
            $"exceptionType={SanitizeDiagnosticToken(outcome.ExceptionType ?? "none")} " +
            $"failurePath={QuoteDiagnostic(outcome.FailurePath ?? "none")} " +
            $"readOnlyCleared={outcome.ReadOnlyAttributesCleared}";
    }

    private static void TryWriteReceipt(Action<string>? writeReceipt, Func<string> buildReceipt)
    {
        if (writeReceipt is null)
        {
            return;
        }

        try
        {
            writeReceipt(buildReceipt());
        }
        catch
        {
            // Diagnostics must not turn best-effort startup housekeeping into an apphost failure.
        }
    }

    private static string QuoteDiagnostic(string value) =>
        $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";

    private static string SanitizeDiagnosticToken(string value) =>
        new(value.Select(character => char.IsWhiteSpace(character) ? '_' : character).ToArray());

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

internal sealed record RevalidatedTempRoot(
    string Name,
    int ProcessId,
    ProcessInspectionRecord Observation);

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
