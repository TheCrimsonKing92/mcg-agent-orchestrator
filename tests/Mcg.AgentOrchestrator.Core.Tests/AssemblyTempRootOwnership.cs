using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;

internal static class AssemblyTempRootOwnership
{
    internal const int MaxRootsReapedPerProcess = 32;
    private const string RootLeaseSuffix = ".owner.lock";

    internal static string BuildProcessTempRoot(string sharedRoot, int processId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sharedRoot);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(processId);
        return Path.Combine(sharedRoot, $"p{processId:x}");
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

    internal static bool TryParseProcessTempRootName(string? directoryName, out int processId)
    {
        processId = 0;
        return !string.IsNullOrEmpty(directoryName) &&
               directoryName[0] == 'p' &&
               int.TryParse(
                   directoryName.AsSpan(1),
                   NumberStyles.HexNumber,
                   CultureInfo.InvariantCulture,
                   out processId) &&
               processId > 0;
    }

    internal static CoreOwnedTempRoot? TryAcquireOwnedRoot(string rootPath)
    {
        FileStream? lease = null;
        try
        {
            var identity = CurrentIdentity();
            lease = new FileStream(
                RootLeasePath(rootPath),
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.Read,
                bufferSize: 256,
                FileOptions.WriteThrough);
            lease.SetLength(0);
            WriteIdentity(lease, identity);
            return new CoreOwnedTempRoot(rootPath, lease);
        }
        catch
        {
            lease?.Dispose();
            return null;
        }
    }

    internal static CoreTempRootIdentity? TryReadOwnedRootIdentity(string rootPath, int expectedProcessId)
    {
        try
        {
            using var stream = new FileStream(
                RootLeasePath(rootPath),
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            return TryReadIdentity(stream, expectedProcessId);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    internal static CoreProcessIdentityObservation ObserveProcess(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return new CoreProcessIdentityObservation(
                CoreProcessIdentityStatus.Running,
                new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero));
        }
        catch (ArgumentException)
        {
            return new CoreProcessIdentityObservation(CoreProcessIdentityStatus.Exited, null);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            return new CoreProcessIdentityObservation(CoreProcessIdentityStatus.Inaccessible, null);
        }
    }

    internal static bool IsRecordedOwnerGone(
        CoreTempRootIdentity identity,
        CoreProcessIdentityObservation observation) =>
        observation.Status == CoreProcessIdentityStatus.Exited ||
        observation is { Status: CoreProcessIdentityStatus.Running, StartedAtUtc: not null } &&
        !SameInstant(identity.StartedAtUtc, observation.StartedAtUtc.Value);

    internal static IReadOnlyList<CoreReapCandidate> SelectReapableRoots(
        string sharedRoot,
        IEnumerable<string> siblingDirectoryNames,
        int currentProcessId,
        Func<string, int, CoreTempRootIdentity?> readIdentity,
        Func<int, CoreProcessIdentityObservation> observeProcess)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sharedRoot);
        ArgumentNullException.ThrowIfNull(siblingDirectoryNames);
        ArgumentNullException.ThrowIfNull(readIdentity);
        ArgumentNullException.ThrowIfNull(observeProcess);

        var candidates = new List<CoreReapCandidate>();
        foreach (var name in siblingDirectoryNames.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!TryParseProcessTempRootName(name, out var processId) || processId == currentProcessId)
            {
                continue;
            }

            var rootPath = Path.Combine(sharedRoot, name);
            var identity = readIdentity(rootPath, processId);
            if (identity is null || !IsRecordedOwnerGone(identity, observeProcess(processId)))
            {
                continue;
            }

            candidates.Add(new CoreReapCandidate(name, processId, identity));
        }

        return candidates;
    }

    internal static IReadOnlyList<CoreReapCandidate> SelectBoundedReapRoots(
        IEnumerable<CoreReapCandidate> candidates,
        Func<string, DateTime> lastWriteUtc,
        int limit)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(lastWriteUtc);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        return candidates
            .Select(candidate => (Candidate: candidate, LastWriteUtc: TryGetLastWriteUtc(candidate.Name, lastWriteUtc)))
            .OrderBy(candidate => candidate.LastWriteUtc)
            .ThenBy(candidate => candidate.Candidate.Name, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .Select(candidate => candidate.Candidate)
            .ToArray();
    }

    internal static IReadOnlyList<CoreTempRootDeleteOutcome> ReapOrphanedRoots(
        string selectedRoot,
        Action<string>? writeReceipt = null)
    {
        try
        {
            var sharedRoot = Path.GetDirectoryName(Path.GetFullPath(selectedRoot));
            if (string.IsNullOrEmpty(sharedRoot) || !Directory.Exists(sharedRoot))
            {
                return [];
            }

            var siblings = Directory.EnumerateDirectories(sharedRoot)
                .Select(Path.GetFileName)
                .OfType<string>()
                .ToArray();
            var candidates = SelectReapableRoots(
                sharedRoot,
                siblings,
                Environment.ProcessId,
                TryReadOwnedRootIdentity,
                ObserveProcess);
            var bounded = SelectBoundedReapRoots(
                candidates,
                name => Directory.GetLastWriteTimeUtc(Path.Combine(sharedRoot, name)),
                MaxRootsReapedPerProcess);
            var outcomes = ReapBoundedRoots(
                sharedRoot,
                bounded,
                ObserveProcess,
                TryAcquireDeletionLease,
                DeleteTreeWithRetry,
                writeReceipt);
            SweepOrphanedRootLeases(sharedRoot, writeReceipt);
            return outcomes;
        }
        catch
        {
            // This runs in a module initializer. Housekeeping is best-effort and must not
            // prevent test discovery when the filesystem or process boundary is inaccessible.
            return [];
        }
    }

    internal static IReadOnlyList<CoreTempRootDeleteOutcome> ReapBoundedRoots(
        string sharedRoot,
        IEnumerable<CoreReapCandidate> candidates,
        Func<int, CoreProcessIdentityObservation> observeProcess,
        Func<string, FileStream?> acquireDeletionLease,
        Func<string, CoreTempRootDeleteOutcome> deleteTree,
        Action<string>? writeReceipt = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sharedRoot);
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(observeProcess);
        ArgumentNullException.ThrowIfNull(acquireDeletionLease);
        ArgumentNullException.ThrowIfNull(deleteTree);

        var outcomes = new List<CoreTempRootDeleteOutcome>();
        foreach (var candidate in candidates)
        {
            var rootPath = Path.Combine(sharedRoot, candidate.Name);
            CoreTempRootDeleteOutcome outcome;
            FileStream? deletionLease = null;
            try
            {
                if (!IsContainedOwnedRoot(sharedRoot, rootPath, candidate.ProcessId) ||
                    !IsRecordedOwnerGone(candidate.Identity, observeProcess(candidate.ProcessId)))
                {
                    outcome = CoreTempRootDeleteOutcome.Preserved(rootPath, "identity-not-exited");
                }
                else
                {
                    deletionLease = acquireDeletionLease(rootPath);
                    var revalidatedIdentity = deletionLease is null
                        ? null
                        : TryReadIdentity(deletionLease, candidate.ProcessId);
                    outcome = deletionLease is null || revalidatedIdentity is null || revalidatedIdentity != candidate.Identity
                        ? CoreTempRootDeleteOutcome.Preserved(rootPath, "lease-or-identity-unavailable")
                        : deleteTree(rootPath);
                }
            }
            catch (Exception ex)
            {
                outcome = CoreTempRootDeleteOutcome.Failure(rootPath, ex);
            }
            finally
            {
                deletionLease?.Dispose();
            }

            if (outcome.Status is CoreTempRootDeleteStatus.Deleted or CoreTempRootDeleteStatus.AlreadyAbsent)
            {
                TryDeleteRootLease(rootPath);
            }

            TryWriteReceipt(writeReceipt, outcome);
            outcomes.Add(outcome);
        }

        return outcomes;
    }

    internal static bool IsContainedOwnedRoot(string sharedRoot, string candidatePath, int expectedProcessId)
    {
        var fullSharedRoot = Path.GetFullPath(sharedRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullCandidate = Path.GetFullPath(candidatePath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(Path.GetDirectoryName(fullCandidate), fullSharedRoot, StringComparison.OrdinalIgnoreCase) &&
               TryParseProcessTempRootName(Path.GetFileName(fullCandidate), out var processId) &&
               processId == expectedProcessId;
    }

    internal static FileStream? TryAcquireDeletionLease(string rootPath)
    {
        try
        {
            return new FileStream(
                RootLeasePath(rootPath),
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FileNotFoundException)
        {
            return null;
        }
    }

    internal static int SweepOrphanedRootLeases(string sharedRoot, Action<string>? writeReceipt = null)
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
            if (!TryParseProcessTempRootName(rootName, out _) || Directory.Exists(Path.Combine(sharedRoot, rootName)))
            {
                continue;
            }

            try
            {
                using var lease = new FileStream(leasePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                lease.Dispose();
                File.Delete(leasePath);
                deleted++;
                writeReceipt?.Invoke($"assembly-temp-reaper orphanLease=\"{leasePath}\" deleteStatus=Deleted");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                writeReceipt?.Invoke(
                    $"assembly-temp-reaper orphanLease=\"{leasePath}\" deleteStatus=Failed exceptionType={ex.GetType().Name}");
            }
        }

        return deleted;
    }

    internal static CoreTempRootDeleteOutcome DeleteTreeWithRetry(string path)
    {
        const int attempts = 3;
        Exception? lastException = null;
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                if (!Directory.Exists(path))
                {
                    return CoreTempRootDeleteOutcome.AlreadyAbsent(path, attempt);
                }

                ClearReadOnlyAttributes(path);
                Directory.Delete(path, recursive: true);
                return CoreTempRootDeleteOutcome.Deleted(path, attempt);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                lastException = ex;
                if (attempt < attempts)
                {
                    using var delay = new ManualResetEvent(initialState: false);
                    delay.WaitOne(TimeSpan.FromMilliseconds(25 * attempt));
                }
            }
        }

        return CoreTempRootDeleteOutcome.Failure(path, lastException!);
    }

    private static CoreTempRootIdentity CurrentIdentity()
    {
        using var process = Process.GetCurrentProcess();
        return new CoreTempRootIdentity(
            Environment.ProcessId,
            new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero),
            Environment.ProcessPath);
    }

    private static void WriteIdentity(Stream stream, CoreTempRootIdentity identity)
    {
        stream.Position = 0;
        using var writer = new StreamWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(
            $"pid={identity.ProcessId};startedAt={identity.StartedAtUtc:O};path={identity.ExecutablePath}");
        writer.Flush();
        stream.Flush();
        stream.Position = 0;
    }

    private static CoreTempRootIdentity? TryReadIdentity(Stream stream, int expectedProcessId)
    {
        try
        {
            stream.Position = 0;
            using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
            var fields = reader.ReadToEnd()
                .Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Select(field => field.Split('=', 2))
                .Where(field => field.Length == 2)
                .ToDictionary(field => field[0], field => field[1], StringComparer.Ordinal);
            return fields.TryGetValue("pid", out var pidText) &&
                   int.TryParse(pidText, NumberStyles.None, CultureInfo.InvariantCulture, out var processId) &&
                   processId == expectedProcessId &&
                   fields.TryGetValue("startedAt", out var startedAtText) &&
                   DateTimeOffset.TryParseExact(
                       startedAtText,
                       "O",
                       CultureInfo.InvariantCulture,
                       DateTimeStyles.RoundtripKind,
                       out var startedAt)
                ? new CoreTempRootIdentity(
                    processId,
                    startedAt.ToUniversalTime(),
                    fields.GetValueOrDefault("path"))
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or ArgumentException)
        {
            return null;
        }
    }

    private static DateTime TryGetLastWriteUtc(string name, Func<string, DateTime> lastWriteUtc)
    {
        try
        {
            return lastWriteUtc(name);
        }
        catch
        {
            return DateTime.MaxValue;
        }
    }

    private static bool SameInstant(DateTimeOffset left, DateTimeOffset right) =>
        Math.Abs((left.ToUniversalTime() - right.ToUniversalTime()).TotalMilliseconds) < 1;

    private static void ClearReadOnlyAttributes(string rootPath)
    {
        foreach (var path in Directory.EnumerateFileSystemEntries(rootPath, "*", SearchOption.AllDirectories))
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReadOnly) != 0)
            {
                File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
            }
        }
    }

    private static void TryDeleteRootLease(string rootPath)
    {
        try
        {
            File.Delete(RootLeasePath(rootPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void TryWriteReceipt(Action<string>? writeReceipt, CoreTempRootDeleteOutcome outcome)
    {
        if (writeReceipt is null)
        {
            return;
        }

        try
        {
            writeReceipt(
                $"assembly-temp-reaper path=\"{outcome.Path}\" deleteStatus={outcome.Status} " +
                $"reason={outcome.Reason ?? "none"} exceptionType={outcome.ExceptionType ?? "none"}");
        }
        catch
        {
        }
    }
}

internal sealed class CoreOwnedTempRoot(string rootPath, FileStream lease) : IDisposable
{
    private FileStream? lease = lease;
    private int released;

    internal string RootPath { get; } = rootPath;

    internal CoreTempRootDeleteOutcome Release()
    {
        if (Interlocked.Exchange(ref released, 1) != 0)
        {
            return CoreTempRootDeleteOutcome.AlreadyAbsent(RootPath, 0);
        }

        Interlocked.Exchange(ref lease, null)?.Dispose();
        var outcome = AssemblyTempRootOwnership.DeleteTreeWithRetry(RootPath);
        if (outcome.Status is CoreTempRootDeleteStatus.Deleted or CoreTempRootDeleteStatus.AlreadyAbsent)
        {
            try
            {
                File.Delete(AssemblyTempRootOwnership.RootLeasePath(RootPath));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return outcome;
    }

    public void Dispose() => Release();
}

internal sealed record CoreTempRootIdentity(
    int ProcessId,
    DateTimeOffset StartedAtUtc,
    string? ExecutablePath);

internal enum CoreProcessIdentityStatus
{
    Running,
    Exited,
    Inaccessible
}

internal sealed record CoreProcessIdentityObservation(
    CoreProcessIdentityStatus Status,
    DateTimeOffset? StartedAtUtc);

internal sealed record CoreReapCandidate(
    string Name,
    int ProcessId,
    CoreTempRootIdentity Identity);

internal enum CoreTempRootDeleteStatus
{
    Deleted,
    AlreadyAbsent,
    Preserved,
    Failed
}

internal sealed record CoreTempRootDeleteOutcome(
    string Path,
    CoreTempRootDeleteStatus Status,
    int DeleteAttempts,
    string? Reason = null,
    string? ExceptionType = null)
{
    internal static CoreTempRootDeleteOutcome Deleted(string path, int attempts) =>
        new(path, CoreTempRootDeleteStatus.Deleted, attempts);

    internal static CoreTempRootDeleteOutcome AlreadyAbsent(string path, int attempts) =>
        new(path, CoreTempRootDeleteStatus.AlreadyAbsent, attempts);

    internal static CoreTempRootDeleteOutcome Preserved(string path, string reason) =>
        new(path, CoreTempRootDeleteStatus.Preserved, 0, reason);

    internal static CoreTempRootDeleteOutcome Failure(string path, Exception exception) =>
        new(path, CoreTempRootDeleteStatus.Failed, 3, exception.Message, exception.GetType().Name);
}
