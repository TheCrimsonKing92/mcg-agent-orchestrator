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

        Environment.SetEnvironmentVariable("TMP", selection.SelectedRoot, EnvironmentVariableTarget.Process);
        Environment.SetEnvironmentVariable("TEMP", selection.SelectedRoot, EnvironmentVariableTarget.Process);

        // The root is owned by this process: delete it on exit, and reap roots whose owning
        // process is gone. Both are required — exit handlers do not run for killed processes,
        // and this repository cancels dispatches and times out gates routinely.
        var timingDiagnostic = RunStartupHousekeeping(
            selection.SelectedRoot,
            timings,
            totalClock,
            DeleteTree);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => DeleteTree(selection.SelectedRoot);
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

    private static void ReapOrphanedRoots(string selectedRoot, TempRootStartupTimings timings) =>
        ReapOrphanedRoots(selectedRoot, timings, DeleteTree);

    private static void ReapOrphanedRoots(
        string selectedRoot,
        TempRootStartupTimings timings,
        Func<string, TempRootDeleteOutcome> deleteTree)
    {
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
            var liveProcesses = SnapshotLiveProcesses(siblings, Environment.ProcessId);
            phaseClock.Stop();
            timings.ReapProcessSnapshotElapsedMilliseconds = phaseClock.ElapsedMilliseconds;

            phaseClock.Restart();
            var reapableByProcess = SelectReapableRoots(
                siblings,
                Environment.ProcessId,
                liveProcesses.ContainsKey);
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
            ReapBoundedRoots(sharedRoot, bounded, deleteTree, timings);
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
        IEnumerable<string> boundedCandidates,
        Func<string, TempRootDeleteOutcome> deleteTree,
        TempRootStartupTimings timings)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sharedRoot);
        ArgumentNullException.ThrowIfNull(boundedCandidates);
        ArgumentNullException.ThrowIfNull(deleteTree);
        ArgumentNullException.ThrowIfNull(timings);

        var outcomes = new List<TempRootDeleteOutcome>();
        foreach (var candidate in boundedCandidates)
        {
            var path = Path.Combine(sharedRoot, candidate);
            TempRootDeleteOutcome outcome;
            try
            {
                outcome = deleteTree(path) ?? TempRootDeleteOutcome.Failure(
                    path,
                    new InvalidOperationException("The delete seam returned no outcome."));
            }
            catch (Exception ex)
            {
                outcome = TempRootDeleteOutcome.Failure(path, ex);
            }

            timings.RecordDelete(outcome);
            outcomes.Add(outcome);
        }

        return outcomes;
    }

    // One process-table snapshot for the whole sweep. Probing each root with
    // Process.GetProcessById costs a full snapshot *per call* on Windows, so the old form was
    // O(orphan roots) snapshots on every test host start: measured at 53.5s with 1106 roots
    // against a 201.2s budget for the entire "Dotnet build slots" lane, and 3.1s once the roots
    // were cleared. Membership in the snapshot also preserves the previous semantics for a PID
    // that cannot be opened — it still enumerates, so it still counts as alive and its root is
    // left alone.
    private static Dictionary<int, DateTime?> SnapshotLiveProcesses(
        IEnumerable<string> siblingDirectoryNames,
        int currentProcessId)
    {
        var relevantProcessIds = new HashSet<int>();
        foreach (var name in siblingDirectoryNames)
        {
            if (TryParseProcessTempRootName(name, out var processId) && processId != currentProcessId)
            {
                relevantProcessIds.Add(processId);
            }
        }

        var live = new Dictionary<int, DateTime?>();
        foreach (var process in System.Diagnostics.Process.GetProcesses())
        {
            try
            {
                if (!relevantProcessIds.Contains(process.Id))
                {
                    continue;
                }

                DateTime? startedUtc = null;
                try
                {
                    startedUtc = process.StartTime.ToUniversalTime();
                }
                catch (Exception ex) when (IsProcessSnapshotFailure(ex))
                {
                    // An inaccessible start time still proves the PID is live. Preserve the root.
                }

                live[process.Id] = startedUtc;
            }
            finally
            {
                process.Dispose();
            }
        }

        return live;
    }

    private static bool IsProcessSnapshotFailure(Exception ex) =>
        ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException;

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

    internal static string? RunStartupHousekeeping(
        string selectedRoot,
        TempRootStartupTimings timings,
        Stopwatch totalClock,
        Func<string, TempRootDeleteOutcome> deleteTree)
    {
        try
        {
            ReapOrphanedRoots(selectedRoot, timings, deleteTree);
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

        ReapDeleteAttempted++;
        DeleteReadOnlyAttributesCleared += outcome.ReadOnlyAttributesCleared;
        switch (outcome.Status)
        {
            case TempRootDeleteStatus.Deleted:
                ReapDeleteSucceeded++;
                ReapDeleteDeleted++;
                break;
            case TempRootDeleteStatus.AlreadyAbsent:
                ReapDeleteAlreadyAbsent++;
                break;
            case TempRootDeleteStatus.Failed:
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
