using System.Runtime.CompilerServices;
using System.Diagnostics;
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
    internal static string? StartupTimingDiagnostic { get; private set; }

    [ModuleInitializer]
    internal static void Install()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var fileSystem = new PhysicalTempRootFileSystem();
        var labeler = new IcaclsIntegrityLabeler();
        var timings = new TempRootStartupTimings();
        var totalClock = Stopwatch.StartNew();
        var selection = SelectWritableRoot(
            EnumerateCandidateRoots(),
            candidate => TryPrepareRoot(
                candidate,
                isWindows: true,
                fileSystem,
                labeler,
                $".write-probe-{Guid.NewGuid():N}",
                timings));

        Console.Error.WriteLine(FormatDiagnostic(selection));
        if (selection.SelectedRoot is null)
        {
            totalClock.Stop();
            timings.TotalElapsedMilliseconds = totalClock.ElapsedMilliseconds;
            TryPublishTimingDiagnostic(TryFormatTimingDiagnostic(timings));
            return;
        }

        var rootLease = TryAcquireOwnedRootLease(selection.SelectedRoot);
        if (rootLease is null)
        {
            TryWriteReceipt(
                Console.Error.WriteLine,
                () => $"assembly-temp-redirect lease=unavailable path=\"{RootLeasePath(selection.SelectedRoot)}\"");
            totalClock.Stop();
            timings.TotalElapsedMilliseconds = totalClock.ElapsedMilliseconds;
            TryPublishTimingDiagnostic(TryFormatTimingDiagnostic(timings));
            return;
        }

        processRootLease = rootLease;

        Environment.SetEnvironmentVariable("TMP", selection.SelectedRoot, EnvironmentVariableTarget.Process);
        Environment.SetEnvironmentVariable("TEMP", selection.SelectedRoot, EnvironmentVariableTarget.Process);

        // The root is owned by this process: delete it on exit, and reap roots whose owning
        // process is gone. Both are required — exit handlers do not run for killed processes,
        // and this repository cancels dispatches and times out gates routinely.
        var timingDiagnostic = RunStartupHousekeeping(
            selection.SelectedRoot,
            timings,
            totalClock,
            DeleteTree,
            Console.Error.WriteLine);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => ReleaseOwnedRoot(selection.SelectedRoot);
        TryPublishTimingDiagnostic(timingDiagnostic);
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

    internal static IReadOnlyList<string> SelectBoundedReapRoots(
        IEnumerable<string> reapableByProcess,
        Func<string, DateTime> lastWriteUtc,
        int limit)
    {
        ArgumentNullException.ThrowIfNull(reapableByProcess);
        ArgumentNullException.ThrowIfNull(lastWriteUtc);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        return reapableByProcess
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(name => (Name: name, LastWriteUtc: TryGetLastWriteUtc(name, lastWriteUtc)))
            .OrderBy(candidate => candidate.LastWriteUtc)
            .ThenBy(candidate => candidate.Name, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .Select(candidate => candidate.Name)
            .ToArray();
    }

    internal static void ReapOrphanedRoots(
        string selectedRoot,
        TempRootStartupTimings timings,
        Func<IEnumerable<int>?, WindowsNativeProcessInspection.ProcessInspectionResult> inspect,
        Func<string, TempRootDeleteOutcome> deleteTree,
        Action<string>? writeReceipt)
    {
        ArgumentNullException.ThrowIfNull(inspect);
        timings.ReapRan = true;
        try
        {
            var sharedRoot = Path.GetDirectoryName(selectedRoot);
            if (string.IsNullOrEmpty(sharedRoot) || !Directory.Exists(sharedRoot))
            {
                return;
            }

            var phaseClock = Stopwatch.StartNew();
            var siblings = Directory.EnumerateDirectories(sharedRoot)
                .Select(Path.GetFileName)
                .OfType<string>()
                .ToArray();
            phaseClock.Stop();
            timings.ReapEnumerateElapsedMilliseconds = phaseClock.ElapsedMilliseconds;
            timings.ReapSiblingCount = siblings.Length;

            phaseClock.Restart();
            var liveProcessIds = SnapshotLiveProcessIds(siblings, Environment.ProcessId, inspect);
            phaseClock.Stop();
            timings.ReapProcessSnapshotElapsedMilliseconds = phaseClock.ElapsedMilliseconds;

            phaseClock.Restart();
            var reapableByProcess = SelectReapableRoots(
                siblings,
                Environment.ProcessId,
                liveProcessIds.Contains);
            phaseClock.Stop();
            timings.ReapProcessSelectionElapsedMilliseconds = phaseClock.ElapsedMilliseconds;

            phaseClock.Restart();
            var bounded = SelectBoundedReapRoots(
                reapableByProcess,
                name => Directory.GetLastWriteTimeUtc(Path.Combine(sharedRoot, name)),
                MaxRootsReapedPerProcess);
            phaseClock.Stop();
            timings.ReapBoundSelectionElapsedMilliseconds = phaseClock.ElapsedMilliseconds;

            phaseClock.Restart();
            var revalidated = RevalidateExitedRoots(bounded, inspect);
            ReapBoundedRoots(
                sharedRoot,
                revalidated,
                deleteTree,
                TryAcquireDeletionLease,
                timings,
                writeReceipt);
            SweepOrphanedRootLeases(sharedRoot, writeReceipt);
            phaseClock.Stop();
            timings.ReapDeleteElapsedMilliseconds = phaseClock.ElapsedMilliseconds;
        }
        catch (Exception)
        {
            // Reaping is best-effort; never fail a test run over temp housekeeping. This runs from
            // a ModuleInitializer, so anything escaping here kills the apphost before discovery and
            // the lane reports zero tests with no failing test to point at.
        }
    }

    internal static IReadOnlyList<TempRootDeleteOutcome> ReapBoundedRoots(
        string sharedRoot,
        IEnumerable<RevalidatedTempRoot> boundedCandidates,
        Func<string, TempRootDeleteOutcome> deleteTree,
        Func<string, IDisposable?> acquireDeletionLease,
        TempRootStartupTimings timings,
        Action<string>? writeReceipt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sharedRoot);
        ArgumentNullException.ThrowIfNull(boundedCandidates);
        ArgumentNullException.ThrowIfNull(deleteTree);
        ArgumentNullException.ThrowIfNull(acquireDeletionLease);
        ArgumentNullException.ThrowIfNull(timings);

        var outcomes = new List<TempRootDeleteOutcome>();
        foreach (var candidate in boundedCandidates)
        {
            var path = Path.Combine(sharedRoot, candidate.Name);
            TempRootDeleteOutcome outcome;
            IDisposable? deletionLease = null;
            try
            {
                deletionLease = acquireDeletionLease(path);
                outcome = deletionLease is null
                    ? TempRootDeleteOutcome.RetainedLiveOwner(path)
                    : deleteTree(path) ?? TempRootDeleteOutcome.Failure(
                        path,
                        new InvalidOperationException("The delete seam returned no outcome."));
            }
            catch (Exception ex)
            {
                outcome = TempRootDeleteOutcome.Failure(path, ex);
            }
            finally
            {
                deletionLease?.Dispose();
            }

            if (outcome.Status is TempRootDeleteStatus.Deleted or TempRootDeleteStatus.AlreadyAbsent)
            {
                TryDeleteRootLease(path);
            }

            timings.RecordDelete(outcome);
            TryWriteReceipt(
                writeReceipt,
                () => FormatReapReceipt(
                    Environment.ProcessId,
                    candidate.ProcessId,
                    path,
                    candidate.Observation,
                    outcome.Status.ToString(),
                    outcome.ExceptionType,
                    outcome.FailurePath,
                    outcome.ReadOnlyAttributesCleared));

            outcomes.Add(outcome);
        }

        return outcomes;
    }

    internal static string FormatReapReceipt(
        int actorProcessId,
        int candidateProcessId,
        string path,
        ProcessInspectionRecord observation,
        string deleteStatus,
        string? exceptionType,
        string? failurePath,
        int readOnlyAttributesCleared)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(actorProcessId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(candidateProcessId);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentException.ThrowIfNullOrWhiteSpace(deleteStatus);

        return "assembly-temp-reaper " +
            $"actorPid={actorProcessId} " +
            $"candidatePid={candidateProcessId} " +
            $"path={QuoteDiagnostic(path)} " +
            $"observedPid={observation.ProcessId} " +
            $"observedStatus={observation.Status} " +
            $"observedName={QuoteDiagnostic(string.IsNullOrWhiteSpace(observation.Name) ? "none" : observation.Name)} " +
            $"observedStartedAt={QuoteDiagnostic(observation.StartedAt?.ToString("O") ?? "none")} " +
            $"observedExecutablePath={QuoteDiagnostic(observation.ExecutablePath ?? "none")} " +
            $"deleteStatus={SanitizeDiagnosticToken(deleteStatus)} " +
            $"exceptionType={SanitizeDiagnosticToken(exceptionType ?? "none")} " +
            $"failurePath={QuoteDiagnostic(failurePath ?? "none")} " +
            $"readOnlyCleared={readOnlyAttributesCleared}";
    }

    private static string QuoteDiagnostic(string value) =>
        $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";

    private static string SanitizeDiagnosticToken(string value) =>
        new(value.Select(character => char.IsWhiteSpace(character) ? '_' : character).ToArray());

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

    internal static IReadOnlyList<RevalidatedTempRoot> RevalidateExitedRoots(
        IEnumerable<string> boundedCandidates,
        Func<IEnumerable<int>?, WindowsNativeProcessInspection.ProcessInspectionResult> inspect)
    {
        ArgumentNullException.ThrowIfNull(boundedCandidates);
        ArgumentNullException.ThrowIfNull(inspect);

        var candidates = boundedCandidates
            .Select(name => new { Name = name, Parsed = TryParseProcessTempRootName(name, out var pid), Pid = pid })
            .Where(candidate => candidate.Parsed && candidate.Pid != Environment.ProcessId)
            .ToArray();
        if (candidates.Length == 0)
        {
            return [];
        }

        var inspection = inspect(candidates.Select(candidate => candidate.Pid).Distinct().ToArray());
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

    internal static TempRootDeleteOutcome DeleteTree(string path)
    {
        var result = TempRootJanitor.DeleteTree(path);
        return result.Status switch
        {
            TempRootJanitorDeleteStatus.Deleted => TempRootDeleteOutcome.Deleted(
                path,
                result.ReadOnlyAttributesCleared),
            TempRootJanitorDeleteStatus.AlreadyAbsent => TempRootDeleteOutcome.AlreadyAbsent(
                path,
                result.ReadOnlyAttributesCleared),
            _ => TempRootDeleteOutcome.Failure(
                path,
                result.ExceptionType ?? "unknown",
                result.FailurePath,
                result.ReadOnlyAttributesCleared)
        };
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

    internal static FileStream? TryAcquireDeletionLease(string rootPath)
    {
        try
        {
            var leasePath = RootLeasePath(rootPath);
            return new FileStream(
                leasePath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal static FileStream? TryAcquireOwnedRootLease(string rootPath)
    {
        FileStream? lease = null;
        try
        {
            var leasePath = RootLeasePath(rootPath);
            lease = new FileStream(
                leasePath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.Read,
                bufferSize: 256,
                FileOptions.WriteThrough);
            lease.SetLength(0);
            using (var writer = new StreamWriter(lease, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                using var process = Process.GetCurrentProcess();
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

    internal static int SweepOrphanedRootLeases(string sharedRoot, Action<string>? writeReceipt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sharedRoot);
        var deleted = 0;
        foreach (var leasePath in Directory.EnumerateFiles(sharedRoot, $".*{RootLeaseSuffix}"))
        {
            var fileName = Path.GetFileName(leasePath);
            if (fileName.Length <= RootLeaseSuffix.Length + 1 || fileName[0] != '.')
            {
                continue;
            }

            var rootName = fileName[1..^RootLeaseSuffix.Length];
            if (!TryParseProcessTempRootName(rootName, out _) ||
                Directory.Exists(Path.Combine(sharedRoot, rootName)))
            {
                continue;
            }

            try
            {
                File.Delete(leasePath);
                deleted++;
                TryWriteReceipt(
                    writeReceipt,
                    () => $"assembly-temp-reaper orphanLease={QuoteDiagnostic(leasePath)} deleteStatus=Deleted");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                TryWriteReceipt(
                    writeReceipt,
                    () => $"assembly-temp-reaper orphanLease={QuoteDiagnostic(leasePath)} " +
                          $"deleteStatus=Failed exceptionType={ex.GetType().Name}");
            }
        }

        return deleted;
    }

    private static void ReleaseOwnedRoot(string rootPath)
    {
        Interlocked.Exchange(ref processRootLease, null)?.Dispose();
        _ = DeleteTree(rootPath);
        TryDeleteRootLease(rootPath);
    }

    private static void TryDeleteRootLease(string rootPath)
    {
        try
        {
            File.Delete(RootLeasePath(rootPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Retaining stale lease metadata is safer than broadening cleanup after ownership changed.
        }
    }

    internal static string? RunStartupHousekeeping(
        string selectedRoot,
        TempRootStartupTimings timings,
        Stopwatch totalClock,
        Func<string, TempRootDeleteOutcome> deleteTree,
        Action<string>? writeReceipt)
    {
        try
        {
            ReapOrphanedRoots(
                selectedRoot,
                timings,
                WindowsNativeProcessInspection.Read,
                deleteTree,
                writeReceipt);
            totalClock.Stop();
            timings.TotalElapsedMilliseconds = totalClock.ElapsedMilliseconds;
            return TryFormatTimingDiagnostic(timings);
        }
        catch
        {
            // This path runs from a ModuleInitializer. Housekeeping and its diagnostics must not
            // prevent test discovery even when an injected or physical delete fails unexpectedly.
            return null;
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
        string probeDirectoryName,
        TempRootStartupTimings? timings = null)
    {
        var phaseClock = Stopwatch.StartNew();
        try
        {
            fileSystem.CreateDirectory(candidate.Path);
        }
        catch (Exception ex) when (IsFileSystemFailure(ex))
        {
            return TempRootPreparationResult.Rejected(TempRootRejectionReason.Create);
        }
        finally
        {
            phaseClock.Stop();
            timings?.AddCreateElapsed(phaseClock.ElapsedMilliseconds);
        }

        if (isWindows && candidate.RequiresLowLabel && !EnsureLowLabel(candidate.Path, labeler, timings))
        {
            return TempRootPreparationResult.Rejected(TempRootRejectionReason.Label);
        }

        var reasons = new List<TempRootRejectionReason>();
        var probeDirectory = Path.Combine(candidate.Path, probeDirectoryName);
        var probeFile = Path.Combine(probeDirectory, "probe.tmp");
        phaseClock.Restart();
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
            phaseClock.Stop();
            timings?.AddProbeWriteElapsed(phaseClock.ElapsedMilliseconds);

            phaseClock.Restart();
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

            phaseClock.Stop();
            timings?.AddProbeCleanupElapsed(phaseClock.ElapsedMilliseconds);
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

    internal static string FormatTimingDiagnostic(TempRootStartupTimings timings)
    {
        ArgumentNullException.ThrowIfNull(timings);
        var labelSet = timings.LabelSetOutcome;
        return "assembly-temp-redirect-timing " +
            $"totalMs={timings.TotalElapsedMilliseconds} " +
            $"createMs={timings.CreateElapsedMilliseconds} " +
            $"labelQueryMs={timings.LabelQueryElapsedMilliseconds} " +
            $"labelQueryCount={timings.LabelQueryCount} " +
            $"labelSetMs={FormatOptional(timings.LabelSetElapsedMilliseconds)} " +
            $"labelSetCompleted={FormatOptional(labelSet?.Completed)} " +
            $"labelSetTimedOut={FormatOptional(labelSet?.TimedOut)} " +
            $"labelSetExitCode={FormatOptional(labelSet?.ExitCode)} " +
            $"labelSetSucceeded={FormatOptional(labelSet?.Succeeded)} " +
            $"labelSetFailure={labelSet?.FailureKind ?? "not-run"} " +
            $"probeWriteMs={timings.ProbeWriteElapsedMilliseconds} " +
            $"probeCleanupMs={timings.ProbeCleanupElapsedMilliseconds} " +
            $"reap={(timings.ReapRan ? "run" : "not-run")} " +
            $"reapEnumerateMs={FormatReap(timings, timings.ReapEnumerateElapsedMilliseconds)} " +
            $"reapSiblingCount={FormatReap(timings, timings.ReapSiblingCount)} " +
            $"reapPidSnapshotMs={FormatReap(timings, timings.ReapProcessSnapshotElapsedMilliseconds)} " +
            $"reapOrphanSelectMs={FormatReap(timings, timings.ReapProcessSelectionElapsedMilliseconds)} " +
            $"reapBoundSelectMs={FormatReap(timings, timings.ReapBoundSelectionElapsedMilliseconds)} " +
            $"deleteAttempted={FormatReap(timings, timings.ReapDeleteAttempted)} " +
            $"deleteSucceeded={FormatReap(timings, timings.ReapDeleteSucceeded)} " +
            $"deleteMs={FormatReap(timings, timings.ReapDeleteElapsedMilliseconds)} " +
            $"deleteDeleted={FormatReap(timings, timings.ReapDeleteDeleted)} " +
            $"deleteAlreadyAbsent={FormatReap(timings, timings.ReapDeleteAlreadyAbsent)} " +
            $"deleteRetainedLiveOwner={FormatReap(timings, timings.ReapDeleteRetainedLiveOwner)} " +
            $"deleteFailed={FormatReap(timings, timings.ReapDeleteFailed)} " +
            $"deleteFailureKinds={FormatReap(timings, timings.DeleteFailureKinds)} " +
            $"deleteFirstFailure={FormatReap(timings, timings.FirstDeleteFailure)} " +
            $"deleteReadOnlyCleared={FormatReap(timings, timings.DeleteReadOnlyAttributesCleared)}";
    }

    internal static string BuildProcessTempRoot(string sharedRoot, int processId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sharedRoot);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(processId);
        return TempRootJanitor.BuildOwnedRootPath(sharedRoot, processId);
    }

    private static bool EnsureLowLabel(
        string path,
        IWorkerIntegrityLabeler labeler,
        TempRootStartupTimings? timings)
    {
        try
        {
            var state = QueryLabel(path, labeler, timings);
            if (state.Exists && state.Low && state.Inheritable)
            {
                return true;
            }

            var setClock = Stopwatch.StartNew();
            var completed = false;
            var succeeded = false;
            try
            {
                succeeded = labeler.SetIntegrity(path, LowInheritableLevel, recursive: false);
                completed = true;
            }
            finally
            {
                setClock.Stop();
                var outcome = (labeler as IWorkerIntegrityLabelerDiagnostics)?.LastSetOutcome ??
                    new IntegrityLabelSetOutcome(
                        Completed: completed,
                        TimedOut: false,
                        ExitCode: null,
                        Succeeded: succeeded,
                        FailureKind: completed ? succeeded ? "none" : "returned-false" : "exception",
                        ElapsedMilliseconds: setClock.ElapsedMilliseconds);
                timings?.RecordLabelSet(outcome.ElapsedMilliseconds, outcome);
            }

            if (!succeeded)
            {
                return false;
            }

            state = QueryLabel(path, labeler, timings);
            return state.Exists && state.Low && state.Inheritable;
        }
        catch
        {
            return false;
        }
    }

    private static IntegrityLabelState QueryLabel(
        string path,
        IWorkerIntegrityLabeler labeler,
        TempRootStartupTimings? timings)
    {
        var queryClock = Stopwatch.StartNew();
        try
        {
            return labeler.Query(path);
        }
        finally
        {
            queryClock.Stop();
            timings?.AddLabelQueryElapsed(queryClock.ElapsedMilliseconds);
        }
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

    private static string? TryFormatTimingDiagnostic(TempRootStartupTimings timings)
    {
        try
        {
            return FormatTimingDiagnostic(timings);
        }
        catch
        {
            // Timing must never make module initialization fail.
            return null;
        }
    }

    private static void TryPublishTimingDiagnostic(string? diagnostic)
    {
        try
        {
            if (diagnostic is null)
            {
                return;
            }

            StartupTimingDiagnostic = diagnostic;
            Console.Error.WriteLine(diagnostic);
        }
        catch
        {
            // Publishing timing must never make module initialization fail.
        }
    }

    private static string FormatOptional(long? value) => value?.ToString() ?? "not-run";

    private static string FormatOptional(int? value) => value?.ToString() ?? "not-run";

    private static string FormatOptional(bool? value) => value?.ToString().ToLowerInvariant() ?? "not-run";

    private static string FormatReap(TempRootStartupTimings timings, long value) =>
        timings.ReapRan ? value.ToString() : "not-run";

    private static string FormatReap(TempRootStartupTimings timings, int value) =>
        timings.ReapRan ? value.ToString() : "not-run";

    private static string FormatReap(TempRootStartupTimings timings, string value) =>
        timings.ReapRan ? value : "not-run";

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

internal enum TempRootDeleteStatus
{
    Deleted,
    AlreadyAbsent,
    RetainedLiveOwner,
    Failed
}

internal sealed record TempRootDeleteOutcome(
    string Path,
    TempRootDeleteStatus Status,
    string? ExceptionType,
    string? FailurePath = null,
    int ReadOnlyAttributesCleared = 0)
{
    internal static TempRootDeleteOutcome Deleted(string path, int readOnlyAttributesCleared = 0) =>
        new(
            path,
            TempRootDeleteStatus.Deleted,
            ExceptionType: null,
            FailurePath: null,
            ReadOnlyAttributesCleared: readOnlyAttributesCleared);

    internal static TempRootDeleteOutcome AlreadyAbsent(string path, int readOnlyAttributesCleared = 0) =>
        new(
            path,
            TempRootDeleteStatus.AlreadyAbsent,
            ExceptionType: null,
            FailurePath: null,
            ReadOnlyAttributesCleared: readOnlyAttributesCleared);

    internal static TempRootDeleteOutcome RetainedLiveOwner(string path) =>
        new(
            path,
            TempRootDeleteStatus.RetainedLiveOwner,
            ExceptionType: null,
            FailurePath: null,
            ReadOnlyAttributesCleared: 0);

    internal static TempRootDeleteOutcome Failure(string path, Exception exception) =>
        Failure(path, exception.GetType().Name, failurePath: null, readOnlyAttributesCleared: 0);

    internal static TempRootDeleteOutcome Failure(
        string path,
        string exceptionType,
        string? failurePath,
        int readOnlyAttributesCleared) =>
        new(path, TempRootDeleteStatus.Failed, exceptionType, failurePath, readOnlyAttributesCleared);
}

internal interface IWorkerIntegrityLabelerDiagnostics
{
    IntegrityLabelSetOutcome? LastSetOutcome { get; }
}

internal sealed record IntegrityLabelSetOutcome(
    bool Completed,
    bool TimedOut,
    int? ExitCode,
    bool Succeeded,
    string FailureKind,
    long ElapsedMilliseconds);

internal sealed class TempRootStartupTimings
{
    private readonly Dictionary<string, int> deleteFailureKindCounts = new(StringComparer.Ordinal);

    internal long TotalElapsedMilliseconds { get; set; }
    internal long CreateElapsedMilliseconds { get; private set; }
    internal long LabelQueryElapsedMilliseconds { get; private set; }
    internal int LabelQueryCount { get; private set; }
    internal long? LabelSetElapsedMilliseconds { get; private set; }
    internal IntegrityLabelSetOutcome? LabelSetOutcome { get; private set; }
    internal long ProbeWriteElapsedMilliseconds { get; private set; }
    internal long ProbeCleanupElapsedMilliseconds { get; private set; }
    internal bool ReapRan { get; set; }
    internal long ReapEnumerateElapsedMilliseconds { get; set; }
    internal int ReapSiblingCount { get; set; }
    internal long ReapProcessSnapshotElapsedMilliseconds { get; set; }
    internal long ReapProcessSelectionElapsedMilliseconds { get; set; }
    internal long ReapBoundSelectionElapsedMilliseconds { get; set; }
    internal int ReapDeleteAttempted { get; private set; }
    internal int ReapDeleteSucceeded { get; private set; }
    internal int ReapDeleteDeleted { get; private set; }
    internal int ReapDeleteAlreadyAbsent { get; private set; }
    internal int ReapDeleteRetainedLiveOwner { get; private set; }
    internal int ReapDeleteFailed { get; private set; }
    internal int DeleteReadOnlyAttributesCleared { get; private set; }
    internal long ReapDeleteElapsedMilliseconds { get; set; }
    internal string DeleteFailureKinds => deleteFailureKindCounts.Count == 0
        ? "none"
        : string.Join(
            "|",
            deleteFailureKindCounts
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => $"{pair.Key}:{pair.Value}"));
    internal string FirstDeleteFailure { get; private set; } = "none";

    internal void AddCreateElapsed(long elapsedMilliseconds) =>
        CreateElapsedMilliseconds += elapsedMilliseconds;

    internal void AddLabelQueryElapsed(long elapsedMilliseconds)
    {
        LabelQueryElapsedMilliseconds += elapsedMilliseconds;
        LabelQueryCount++;
    }

    internal void RecordLabelSet(long elapsedMilliseconds, IntegrityLabelSetOutcome outcome)
    {
        LabelSetElapsedMilliseconds = (LabelSetElapsedMilliseconds ?? 0) + elapsedMilliseconds;
        LabelSetOutcome = outcome;
    }

    internal void AddProbeWriteElapsed(long elapsedMilliseconds) =>
        ProbeWriteElapsedMilliseconds += elapsedMilliseconds;

    internal void AddProbeCleanupElapsed(long elapsedMilliseconds) =>
        ProbeCleanupElapsedMilliseconds += elapsedMilliseconds;

    internal void RecordDelete(TempRootDeleteOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);

        DeleteReadOnlyAttributesCleared += outcome.ReadOnlyAttributesCleared;
        switch (outcome.Status)
        {
            case TempRootDeleteStatus.Deleted:
                ReapDeleteAttempted++;
                ReapDeleteSucceeded++;
                ReapDeleteDeleted++;
                break;
            case TempRootDeleteStatus.AlreadyAbsent:
                ReapDeleteAttempted++;
                ReapDeleteAlreadyAbsent++;
                break;
            case TempRootDeleteStatus.RetainedLiveOwner:
                ReapDeleteRetainedLiveOwner++;
                break;
            case TempRootDeleteStatus.Failed:
                ReapDeleteAttempted++;
                ReapDeleteFailed++;
                var exceptionType = SanitizeDiagnosticValue(outcome.ExceptionType ?? "unknown");
                deleteFailureKindCounts[exceptionType] =
                    deleteFailureKindCounts.GetValueOrDefault(exceptionType) + 1;
                if (FirstDeleteFailure == "none")
                {
                    var leaf = SanitizeDiagnosticValue(Path.GetFileName(outcome.FailurePath ?? outcome.Path));
                    FirstDeleteFailure = $"{leaf}:{exceptionType}";
                }

                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(outcome), outcome.Status, null);
        }
    }

    private static string SanitizeDiagnosticValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "unknown";
        }

        return new string(value.Select(character => char.IsWhiteSpace(character) ? '_' : character).ToArray());
    }
}

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
