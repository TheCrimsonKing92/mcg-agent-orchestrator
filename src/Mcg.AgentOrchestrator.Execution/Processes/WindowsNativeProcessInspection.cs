using System.Runtime.InteropServices;
using System.Text;

namespace Mcg.AgentOrchestrator.Infrastructure;

public enum ProcessInspectionStatus
{
    Available,
    AccessDenied,
    Exited,
    DeadOrRecycled,
    MalformedData,
    PartialRead,
    UnsupportedTarget,
    NativeFailure
}

public sealed record ProcessInspectionRecord(
    int ProcessId,
    int ParentProcessId,
    string Name,
    string? ExecutablePath,
    DateTimeOffset? StartedAt,
    string? CommandLine,
    ProcessInspectionStatus Status);

public sealed record ProcessInspectionFailure(
    ProcessInspectionStatus Status,
    int NativeError,
    string Operation);

internal static partial class WindowsNativeProcessInspection
{
    private const uint SnapshotProcesses = 0x00000002;
    private const int ProcessQueryLimitedInformation = 0x1000;
    private const int ProcessVmRead = 0x0010;
    private const int ProcessTerminate = 0x0001;
    private const int Synchronize = 0x00100000;
    private const int ProcessBasicInformation = 0;
    private const int ProcessWow64Information = 26;
    private const int ErrorAccessDenied = 5;
    private const int ErrorNoMoreFiles = 18;
    private const int ErrorInvalidParameter = 87;
    private const int ErrorPartialCopy = 299;
    private const int MaxCommandLineBytes = 32766;
    private const uint StillActive = 259;

    public static ProcessInspectionResult Read(IEnumerable<int>? requestedProcessIds = null)
    {
        if (!OperatingSystem.IsWindows())
        {
            return ProcessInspectionResult.Success(new Dictionary<int, ProcessInspectionRecord>());
        }

        if (requestedProcessIds is null)
        {
            return BeginOperation().ReadAll();
        }

        var ids = requestedProcessIds.Where(id => id > 0).Distinct().ToArray();
        return BeginOperation().ReadRequested(ids);
    }

    internal static ProcessLifecycleIdentityReadResult ReadLifecycleIdentity(
        int expectedProcessId,
        SafeHandle processHandle)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(expectedProcessId, 1);
        ArgumentNullException.ThrowIfNull(processHandle);
        if (!OperatingSystem.IsWindows())
        {
            return new(null, null, ProcessInspectionStatus.UnsupportedTarget, 0, "platform-check");
        }

        if (processHandle.IsClosed || processHandle.IsInvalid)
        {
            return new(null, null, ProcessInspectionStatus.NativeFailure, 0, "process-handle-validation");
        }

        var addedReference = false;
        try
        {
            processHandle.DangerousAddRef(ref addedReference);
            return ReadLifecycleIdentity(expectedProcessId, processHandle.DangerousGetHandle());
        }
        catch (ObjectDisposedException)
        {
            return new(null, null, ProcessInspectionStatus.NativeFailure, 0, "process-handle-validation");
        }
        finally
        {
            if (addedReference)
            {
                processHandle.DangerousRelease();
            }
        }
    }

    private static ProcessLifecycleIdentityReadResult ReadLifecycleIdentity(
        int expectedProcessId,
        IntPtr processHandle)
    {

        var actualProcessId = GetProcessId(processHandle);
        if (actualProcessId == 0)
        {
            var error = Marshal.GetLastWin32Error();
            return new(null, null, StatusFromError(error), error, "process-id-read");
        }

        if (actualProcessId != (uint)expectedProcessId)
        {
            return new(null, null, ProcessInspectionStatus.DeadOrRecycled, 0, "process-id-match");
        }

        if (!GetExitCodeProcess(processHandle, out var exitCode))
        {
            var error = Marshal.GetLastWin32Error();
            return new(null, null, StatusFromError(error), error, "process-liveness-read-before-identity");
        }

        if (exitCode != StillActive)
        {
            return new(null, null, ProcessInspectionStatus.Exited, 0, "process-liveness-read-before-identity");
        }

        var capacity = 32768u;
        var path = new StringBuilder((int)capacity);
        if (!QueryFullProcessImageName(processHandle, 0, path, ref capacity))
        {
            var error = Marshal.GetLastWin32Error();
            return new(null, null, StatusFromError(error), error, "process-image-read");
        }

        if (!GetProcessTimes(processHandle, out var creationTime, out _, out _, out _))
        {
            var error = Marshal.GetLastWin32Error();
            return new(null, null, StatusFromError(error), error, "process-creation-time-read");
        }

        DateTimeOffset startedAt;
        try
        {
            startedAt = new DateTimeOffset(DateTime.FromFileTimeUtc(creationTime), TimeSpan.Zero);
        }
        catch (ArgumentOutOfRangeException)
        {
            return new(null, null, ProcessInspectionStatus.MalformedData, 0, "process-creation-time-conversion");
        }

        actualProcessId = GetProcessId(processHandle);
        if (actualProcessId == 0)
        {
            var error = Marshal.GetLastWin32Error();
            return new(null, null, StatusFromError(error), error, "process-liveness-read-after-identity");
        }

        if (actualProcessId != (uint)expectedProcessId)
        {
            return new(null, null, ProcessInspectionStatus.DeadOrRecycled, 0, "process-id-match-after-identity");
        }

        if (!GetExitCodeProcess(processHandle, out exitCode))
        {
            var error = Marshal.GetLastWin32Error();
            return new(null, null, StatusFromError(error), error, "process-liveness-read-after-identity");
        }

        return exitCode == StillActive
            ? new(path.ToString(), startedAt, ProcessInspectionStatus.Available, 0, "kernel-lifecycle-identity")
            : new(null, null, ProcessInspectionStatus.Exited, 0, "process-liveness-read-after-identity");
    }

    public static ProcessInspectionResult ReadByNames(IEnumerable<string> processNames)
    {
        var names = processNames
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => Path.GetFileNameWithoutExtension(name)!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return BeginOperation().ReadByNames(names);
    }

    internal static IReadOnlyList<int> ListIdentityBoundDescendantProcessIds(int ancestorProcessId)
    {
        return ReadIdentityBoundDescendants(ancestorProcessId).Records.Keys
            .OrderBy(processId => processId)
            .ToArray();
    }

    internal static IReadOnlyList<int> ListIdentityBoundDescendantProcessIds(
        int ancestorProcessId,
        Func<ProcessEnumerationResult> enumerate,
        Func<ProcessInspectionSeed, ProcessInspectionRecord> readOne)
    {
        return ReadIdentityBoundDescendants(ancestorProcessId, enumerate, readOne).Records.Keys
            .OrderBy(processId => processId)
            .ToArray();
    }

    /// <summary>
    /// The identity-bound descendant set with each accepted record kept, so a caller can re-check the
    /// exact identity later instead of re-resolving a bare pid that may have been reassigned.
    /// </summary>
    internal static ProcessInspectionResult ReadIdentityBoundDescendants(int ancestorProcessId)
    {
        if (ancestorProcessId <= 0 || !OperatingSystem.IsWindows())
        {
            return ProcessInspectionResult.Success(new Dictionary<int, ProcessInspectionRecord>());
        }

        return ReadIdentityBoundDescendants(ancestorProcessId, EnumerateProcesses, ReadOne);
    }

    internal static ProcessInspectionResult ReadIdentityBoundDescendants(
        int ancestorProcessId,
        Func<ProcessEnumerationResult> enumerate,
        Func<ProcessInspectionSeed, ProcessInspectionRecord> readOne)
    {
        var enumeration = enumerate();
        if (ancestorProcessId <= 0)
        {
            return ProcessInspectionResult.Success(new Dictionary<int, ProcessInspectionRecord>());
        }

        if (enumeration.Failure is not null)
        {
            return ProcessInspectionResult.Failed(enumeration.Failure);
        }

        var seedsById = enumeration.Processes
            .Where(seed => seed.ProcessId > 0)
            .GroupBy(seed => seed.ProcessId)
            .ToDictionary(group => group.Key, group => group.First());
        if (!seedsById.TryGetValue(ancestorProcessId, out var rootSeed))
        {
            return ProcessInspectionResult.Success(new Dictionary<int, ProcessInspectionRecord>());
        }

        var records = new Dictionary<int, ProcessInspectionRecord>();
        ProcessInspectionRecord Read(ProcessInspectionSeed seed)
        {
            if (!records.TryGetValue(seed.ProcessId, out var record))
            {
                record = readOne(seed);
                records[seed.ProcessId] = record;
            }

            return record;
        }

        var root = Read(rootSeed);
        if (!CanEstablishLiveIdentity(root))
        {
            return ProcessInspectionResult.Success(new Dictionary<int, ProcessInspectionRecord>());
        }

        var childrenByParent = enumeration.Processes
            .Where(seed => seed.ProcessId > 0 && seed.ParentProcessId > 0 && seed.ProcessId != ancestorProcessId)
            .GroupBy(seed => seed.ParentProcessId)
            .ToDictionary(group => group.Key, group => group.ToArray());
        var accepted = new Dictionary<int, ProcessInspectionRecord>();
        var queue = new Queue<ProcessInspectionSeed>();
        queue.Enqueue(rootSeed);
        while (queue.TryDequeue(out var parentSeed))
        {
            if (!childrenByParent.TryGetValue(parentSeed.ProcessId, out var children))
            {
                continue;
            }

            var parent = Read(parentSeed);
            foreach (var childSeed in children)
            {
                var child = Read(childSeed);
                if (!CanEstablishLiveIdentity(parent) ||
                    !CanEstablishLiveIdentity(child) ||
                    ProcessTreeEdgeEligibility.Evaluate(
                        parentSeed.ProcessId,
                        parent.StartedAt,
                        child).Decision != ProcessTreeEdgeDecision.Eligible)
                {
                    continue;
                }

                accepted.TryAdd(childSeed.ProcessId, child);
                queue.Enqueue(childSeed);
            }
        }

        return ProcessInspectionResult.Success(accepted);
    }

    internal static IReadOnlyList<int> ListConservativeDescendantProcessIdsForRefusal(
        int ancestorProcessId,
        DateTimeOffset recordedAncestorStartedAt)
    {
        if (ancestorProcessId <= 0 || !OperatingSystem.IsWindows())
        {
            return [];
        }

        return ListConservativeDescendantProcessIdsForRefusal(
            ancestorProcessId,
            recordedAncestorStartedAt,
            EnumerateProcesses,
            ReadOne);
    }

    internal static IReadOnlyList<int> ListConservativeDescendantProcessIdsForRefusal(
        int ancestorProcessId,
        DateTimeOffset recordedAncestorStartedAt,
        Func<ProcessEnumerationResult> enumerate,
        Func<ProcessInspectionSeed, ProcessInspectionRecord> readOne)
    {
        var enumeration = enumerate();
        if (ancestorProcessId <= 0 || enumeration.Failure is not null)
        {
            return [];
        }

        var seedsById = enumeration.Processes
            .Where(seed => seed.ProcessId > 0)
            .GroupBy(seed => seed.ProcessId)
            .ToDictionary(group => group.Key, group => group.First());
        var records = new Dictionary<int, ProcessInspectionRecord>();
        ProcessInspectionRecord Read(ProcessInspectionSeed seed)
        {
            if (!records.TryGetValue(seed.ProcessId, out var record))
            {
                record = readOne(seed);
                records[seed.ProcessId] = record;
            }

            return record;
        }

        var rootEarliestPossibleStart = recordedAncestorStartedAt;
        // TaskProcessRecord.StartedAt is observed after Process.Start; it is not the
        // OS creation time and cannot prove PID recycle by exact comparison. For this
        // refusal-only walk, use a readable live root identity as the temporal anchor.
        // A mismatch remains unknown and conservative, never ownership or kill authority.
        if (seedsById.TryGetValue(ancestorProcessId, out var currentRootSeed))
        {
            var currentRoot = Read(currentRootSeed);
            if (currentRoot.StartedAt is { } currentRootStartedAt)
            {
                rootEarliestPossibleStart = currentRootStartedAt;
            }
        }

        var childrenByParent = enumeration.Processes
            .Where(seed => seed.ProcessId > 0 && seed.ParentProcessId > 0 && seed.ProcessId != ancestorProcessId)
            .GroupBy(seed => seed.ParentProcessId)
            .ToDictionary(group => group.Key, group => group.ToArray());
        var candidates = new List<int>();
        var queue = new Queue<(int ProcessId, DateTimeOffset EarliestPossibleStart)>();
        queue.Enqueue((ancestorProcessId, rootEarliestPossibleStart));
        while (queue.TryDequeue(out var parent))
        {
            if (!childrenByParent.TryGetValue(parent.ProcessId, out var children))
            {
                continue;
            }

            foreach (var childSeed in children)
            {
                var child = Read(childSeed);
                if (child.Status == ProcessInspectionStatus.DeadOrRecycled)
                {
                    continue;
                }

                // Known temporal inversion proves that this is a stale parent-PID edge;
                // prune its entire subtree. An unreadable identity remains an unknown
                // refusal candidate, but never flows into identity-bound ownership.
                if (ProcessTreeEdgeEligibility.Evaluate(
                        parent.ProcessId,
                        parent.EarliestPossibleStart,
                        child).Decision == ProcessTreeEdgeDecision.TemporalInversion)
                {
                    continue;
                }

                candidates.Add(childSeed.ProcessId);
                queue.Enqueue((childSeed.ProcessId, child.StartedAt ?? parent.EarliestPossibleStart));
            }
        }

        return candidates.Distinct().OrderBy(processId => processId).ToArray();
    }

    private static bool CanEstablishLiveIdentity(ProcessInspectionRecord record) =>
        record.StartedAt is not null &&
        record.Status is not ProcessInspectionStatus.Exited and not ProcessInspectionStatus.DeadOrRecycled;

    internal static ProcessInspectionResult ReadByNames(
        IReadOnlySet<string> processNames,
        Func<ProcessEnumerationResult> enumerate,
        Func<ProcessInspectionSeed, ProcessInspectionRecord> readOne) =>
        BeginOperation(enumerate, readOne).ReadByNames(processNames);

    internal static ProcessInspectionResult Read(
        IEnumerable<int>? requestedProcessIds,
        Func<ProcessEnumerationResult> enumerate,
        Func<ProcessInspectionSeed, ProcessInspectionRecord> readOne,
        Func<ProcessInspectionSeed, bool>? include = null)
    {
        var operation = BeginOperation(enumerate, readOne);
        if (requestedProcessIds is not null)
        {
            return operation.ReadRequested(requestedProcessIds);
        }

        // This compatibility seam is retained for focused tests. Production name filtering uses
        // ReadByNames so candidate selection still happens before an expensive identity read.
        var all = operation.ReadAll();
        if (include is null || all.Failure is not null)
        {
            return all;
        }

        return ProcessInspectionResult.Success(all.Records
            .Where(pair => include(new ProcessInspectionSeed(
                pair.Value.ProcessId,
                pair.Value.ParentProcessId,
                pair.Value.Name)))
            .ToDictionary(pair => pair.Key, pair => pair.Value));
    }

    internal static ProcessMemoryLayout GetMemoryLayout(bool targetIsWow64) =>
        targetIsWow64 || IntPtr.Size == 4
            ? new ProcessMemoryLayout(4, 0x10, 0x40)
            : new ProcessMemoryLayout(8, 0x20, 0x70);

    internal static ProcessInspectionResult ReadRequested(
        IReadOnlyCollection<int> requestedProcessIds,
        Func<ProcessEnumerationResult> enumerate,
        Func<ProcessInspectionSeed, ProcessInspectionRecord> readOne)
    {
        return BeginOperation(enumerate, readOne).ReadRequested(requestedProcessIds);
    }

    internal static ProcessInspectionOperation BeginOperation() =>
        BeginOperation(EnumerateProcesses, ReadOne);

    internal static ProcessInspectionOperation BeginOperation(
        Func<ProcessEnumerationResult> enumerate,
        Func<ProcessInspectionSeed, ProcessInspectionRecord> readOne) =>
        new(enumerate(), readOne);

    public static ProcessParentIdReadResult ReadParentProcessId(int processId) =>
        ReadParentProcessId(processId, EnumerateProcesses);

    internal static ProcessParentIdReadResult ReadParentProcessId(
        int processId,
        Func<ProcessEnumerationResult> enumerate)
    {
        var enumeration = enumerate();
        if (enumeration.Failure is not null)
        {
            return new(null, enumeration.Failure.Status, enumeration.Failure);
        }

        var seed = enumeration.Processes.FirstOrDefault(entry => entry.ProcessId == processId);
        return seed is null
            ? new(null, ProcessInspectionStatus.Exited, null)
            : new(
                seed.ParentProcessId > 0 ? seed.ParentProcessId : null,
                ProcessInspectionStatus.Available,
                null);
    }

    internal static bool TryTerminateIfMatches(ProcessInspectionRecord expected) =>
        OperatingSystem.IsWindows() && TryTerminateIfMatches(
            expected,
            processId =>
            {
                var handle = OpenProcess(
                    ProcessQueryLimitedInformation | ProcessVmRead | ProcessTerminate | Synchronize,
                    false,
                    processId);
                return new ProcessOpenResult(
                    handle,
                    handle == IntPtr.Zero ? Marshal.GetLastWin32Error() : 0);
            },
            ReadOpenedProcess,
            handle => TerminateProcess(handle, 1) && WaitForSingleObject(handle, 3000) == 0,
            handle => CloseHandle(handle));

    internal static bool TryTerminateIfMatches(
        ProcessInspectionRecord expected,
        Func<int, ProcessOpenResult> open,
        Func<IntPtr, OpenedProcessReadResult> readOpened,
        Func<IntPtr, bool> terminate,
        Action<IntPtr> close)
    {
        var opened = open(expected.ProcessId);
        if (opened.Handle == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            var current = readOpened(opened.Handle);
            return MatchesIdentity(expected, current) && terminate(opened.Handle);
        }
        finally
        {
            close(opened.Handle);
        }
    }

    internal static bool MatchesIdentity(
        ProcessInspectionRecord expected,
        ProcessInspectionRecord current) =>
        MatchesIdentity(
            expected,
            new OpenedProcessReadResult(
                current.ParentProcessId,
                current.ExecutablePath,
                current.StartedAt,
                current.CommandLine,
                current.Status));

    private static bool MatchesIdentity(
        ProcessInspectionRecord expected,
        OpenedProcessReadResult current) =>
        expected.Status == ProcessInspectionStatus.Available &&
        current.Status == ProcessInspectionStatus.Available &&
        expected.ParentProcessId == current.ParentProcessId &&
        expected.StartedAt.HasValue &&
        current.StartedAt.HasValue &&
        expected.StartedAt.Value == current.StartedAt.Value &&
        !string.IsNullOrWhiteSpace(expected.ExecutablePath) &&
        !string.IsNullOrWhiteSpace(current.ExecutablePath) &&
        expected.ExecutablePath.Equals(current.ExecutablePath, StringComparison.OrdinalIgnoreCase) &&
        !string.IsNullOrWhiteSpace(expected.CommandLine) &&
        !string.IsNullOrWhiteSpace(current.CommandLine) &&
        expected.CommandLine.Equals(current.CommandLine, StringComparison.Ordinal);

    private static ProcessInspectionRecord ReadOne(ProcessInspectionSeed entry)
    {
        return ReadOne(entry, OpenForInspection, ReadOpenedProcess, handle => CloseHandle(handle));
    }

    internal static ProcessInspectionRecord ReadOne(
        ProcessInspectionSeed entry,
        Func<int, ProcessOpenResult> open,
        Func<IntPtr, OpenedProcessReadResult> readOpened,
        Action<IntPtr> close)
    {
        var opened = open(entry.ProcessId);
        if (opened.Handle == IntPtr.Zero)
        {
            return Unavailable(
                entry,
                opened.Error == ErrorAccessDenied
                    ? ProcessInspectionStatus.AccessDenied
                    : opened.Error == ErrorInvalidParameter
                        ? ProcessInspectionStatus.Exited
                        : ProcessInspectionStatus.NativeFailure,
                null,
                null);
        }

        try
        {
            var read = readOpened(opened.Handle);
            var openedName = string.IsNullOrWhiteSpace(read.ExecutablePath)
                ? entry.Name
                : Path.GetFileNameWithoutExtension(read.ExecutablePath);
            var status = read.Status;
            if (status == ProcessInspectionStatus.Available &&
                !string.IsNullOrWhiteSpace(entry.Name) &&
                !string.IsNullOrWhiteSpace(openedName) &&
                !entry.Name.Equals(openedName, StringComparison.OrdinalIgnoreCase))
            {
                status = ProcessInspectionStatus.DeadOrRecycled;
            }

            return new ProcessInspectionRecord(
                entry.ProcessId,
                read.ParentProcessId,
                openedName,
                read.ExecutablePath,
                read.StartedAt,
                status == ProcessInspectionStatus.Available ? read.CommandLine : null,
                status);
        }
        finally
        {
            close(opened.Handle);
        }
    }

    private static ProcessOpenResult OpenForInspection(int processId)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation | ProcessVmRead, false, processId);
        return new ProcessOpenResult(handle, handle == IntPtr.Zero ? Marshal.GetLastWin32Error() : 0);
    }

    private static OpenedProcessReadResult ReadOpenedProcess(IntPtr handle)
    {
        if (!TryReadExitCode(handle, out var exitStatus))
        {
            return new(0, null, null, null, exitStatus);
        }

        var basicStatus = NtQueryInformationProcess(
            handle,
            ProcessBasicInformation,
            out PROCESS_BASIC_INFORMATION basic,
            Marshal.SizeOf<PROCESS_BASIC_INFORMATION>(),
            out _);
        if (basicStatus != 0 || basic.PebBaseAddress == IntPtr.Zero)
        {
            return new(0, null, null, null, ProcessInspectionStatus.NativeFailure);
        }

        var parentProcessId = basic.InheritedFromUniqueProcessId.ToInt64() is > 0 and <= int.MaxValue
            ? basic.InheritedFromUniqueProcessId.ToInt32()
            : 0;
        var path = ReadExecutablePath(handle);
        if (path.Status != ProcessInspectionStatus.Available)
        {
            return new(parentProcessId, null, null, null, path.Status);
        }

        var startedAt = ReadStartTime(handle);
        if (startedAt.Status != ProcessInspectionStatus.Available)
        {
            return new(parentProcessId, path.Value, null, null, startedAt.Status);
        }

        var commandLine = ReadCommandLine(handle, basic.PebBaseAddress);
        if (commandLine.Status != ProcessInspectionStatus.Available)
        {
            return new(parentProcessId, path.Value, startedAt.Value, null, commandLine.Status);
        }

        if (!TryReadExitCode(handle, out exitStatus))
        {
            return new(parentProcessId, path.Value, startedAt.Value, null, exitStatus);
        }

        return new(parentProcessId, path.Value, startedAt.Value, commandLine.CommandLine, ProcessInspectionStatus.Available);
    }

    private static bool TryReadExitCode(IntPtr handle, out ProcessInspectionStatus status)
    {
        if (!GetExitCodeProcess(handle, out var exitCode))
        {
            status = StatusFromLastError();
            return false;
        }

        status = exitCode == StillActive ? ProcessInspectionStatus.Available : ProcessInspectionStatus.Exited;
        return exitCode == StillActive;
    }

    private static ValueReadResult<string?> ReadExecutablePath(IntPtr handle)
    {
        var capacity = 32768u;
        var path = new StringBuilder((int)capacity);
        return QueryFullProcessImageName(handle, 0, path, ref capacity)
            ? new(path.ToString(), ProcessInspectionStatus.Available)
            : new(null, StatusFromLastError());
    }

    private static ValueReadResult<DateTimeOffset?> ReadStartTime(IntPtr handle)
    {
        if (!GetProcessTimes(handle, out var creationTime, out _, out _, out _))
        {
            return new(null, StatusFromLastError());
        }

        try
        {
            return new(new DateTimeOffset(DateTime.FromFileTimeUtc(creationTime), TimeSpan.Zero), ProcessInspectionStatus.Available);
        }
        catch (ArgumentOutOfRangeException)
        {
            return new(null, ProcessInspectionStatus.MalformedData);
        }
    }

    private static ProcessInspectionStatus StatusFromLastError() => StatusFromError(Marshal.GetLastWin32Error());

    private static ProcessInspectionStatus StatusFromError(int error) => error switch
    {
        ErrorAccessDenied => ProcessInspectionStatus.AccessDenied,
        ErrorInvalidParameter => ProcessInspectionStatus.Exited,
        ErrorPartialCopy => ProcessInspectionStatus.PartialRead,
        _ => ProcessInspectionStatus.NativeFailure
    };

    private static CommandLineReadResult ReadCommandLine(IntPtr handle, IntPtr nativePebAddress)
    {
        if (IntPtr.Size == 4 && Environment.Is64BitOperatingSystem)
        {
            try
            {
                if (!IsWow64Process2(handle, out var processMachine, out _))
                {
                    return new(null, ProcessInspectionStatus.NativeFailure);
                }

                if (processMachine == 0)
                {
                    return new(null, ProcessInspectionStatus.UnsupportedTarget);
                }
            }
            catch (EntryPointNotFoundException)
            {
                return new(null, ProcessInspectionStatus.UnsupportedTarget);
            }
        }

        var pointerSize = IntPtr.Size;
        var pebAddress = nativePebAddress;
        if (IntPtr.Size == 8)
        {
            var wow64Status = NtQueryInformationProcess(
                handle,
                ProcessWow64Information,
                out IntPtr wow64Peb,
                IntPtr.Size,
                out _);
            if (wow64Status != 0)
            {
                return new(null, ProcessInspectionStatus.UnsupportedTarget);
            }

            if (wow64Peb != IntPtr.Zero)
            {
                pointerSize = 4;
                pebAddress = wow64Peb;
            }
        }

        var layout = GetMemoryLayout(pointerSize == 4 && IntPtr.Size == 8);
        var parameters = ReadPointer(handle, Add(pebAddress, layout.ProcessParametersOffset), layout.PointerSize);
        if (!parameters.Succeeded)
        {
            return new(null, parameters.Status);
        }

        if (parameters.Value == 0)
        {
            return new(null, ProcessInspectionStatus.MalformedData);
        }

        return ReadUnicodeString(handle, Add(parameters.Value, layout.CommandLineOffset), layout.PointerSize);
    }

    private static PointerReadResult ReadPointer(IntPtr handle, IntPtr address, int pointerSize)
    {
        var buffer = new byte[pointerSize];
        var status = ReadExact(handle, address, buffer);
        if (status != ProcessInspectionStatus.Available)
        {
            return new(0, false, status);
        }

        var value = pointerSize == 8
            ? BitConverter.ToInt64(buffer, 0)
            : BitConverter.ToUInt32(buffer, 0);
        return new(value, true, ProcessInspectionStatus.Available);
    }

    private static CommandLineReadResult ReadUnicodeString(IntPtr handle, IntPtr address, int pointerSize)
    {
        var header = new byte[pointerSize == 8 ? 16 : 8];
        var headerStatus = ReadExact(handle, address, header);
        if (headerStatus != ProcessInspectionStatus.Available)
        {
            return new(null, headerStatus);
        }

        var length = BitConverter.ToUInt16(header, 0);
        var maximumLength = BitConverter.ToUInt16(header, 2);
        if (length == 0)
        {
            return new(string.Empty, ProcessInspectionStatus.Available);
        }

        if ((length & 1) != 0 || length > maximumLength || length > MaxCommandLineBytes)
        {
            return new(null, ProcessInspectionStatus.MalformedData);
        }

        var bufferAddress = pointerSize == 8
            ? BitConverter.ToInt64(header, 8)
            : BitConverter.ToUInt32(header, 4);
        if (bufferAddress == 0)
        {
            return new(null, ProcessInspectionStatus.MalformedData);
        }

        var commandBuffer = new byte[length];
        var commandStatus = ReadExact(handle, new IntPtr(bufferAddress), commandBuffer);
        return commandStatus == ProcessInspectionStatus.Available
            ? new(Encoding.Unicode.GetString(commandBuffer).TrimEnd('\0'), ProcessInspectionStatus.Available)
            : new(null, commandStatus);
    }

    private static ProcessInspectionStatus ReadExact(IntPtr handle, IntPtr address, byte[] buffer)
    {
        if (!ReadProcessMemory(handle, address, buffer, buffer.Length, out var bytesRead))
        {
            return Marshal.GetLastWin32Error() switch
            {
                ErrorAccessDenied => ProcessInspectionStatus.AccessDenied,
                ErrorPartialCopy => ProcessInspectionStatus.PartialRead,
                _ => ProcessInspectionStatus.NativeFailure
            };
        }

        return bytesRead.ToInt64() == buffer.Length
            ? ProcessInspectionStatus.Available
            : ProcessInspectionStatus.PartialRead;
    }

    private static ProcessEnumerationResult EnumerateProcesses() =>
        EnumerateProcesses(
            () => CreateToolhelp32Snapshot(SnapshotProcesses, 0),
            ReadProcessSnapshot,
            handle => _ = CloseHandle(handle),
            Marshal.GetLastWin32Error);

    internal static ProcessEnumerationResult EnumerateProcesses(
        Func<IntPtr> createSnapshot,
        Func<IntPtr, ProcessEnumerationResult> readSnapshot,
        Action<IntPtr> closeSnapshot,
        Func<int> getLastError)
    {
        var snapshot = createSnapshot();
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1))
        {
            return ProcessEnumerationResult.Failed(
                new ProcessInspectionFailure(
                    ProcessInspectionStatus.NativeFailure,
                    getLastError(),
                    nameof(CreateToolhelp32Snapshot)));
        }

        try
        {
            return readSnapshot(snapshot);
        }
        finally
        {
            closeSnapshot(snapshot);
        }
    }

    private static ProcessEnumerationResult ReadProcessSnapshot(IntPtr snapshot)
    {
        var entry = new PROCESSENTRY32 { Size = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
        if (!Process32First(snapshot, ref entry))
        {
            return ProcessEnumerationResult.Failed(
                new ProcessInspectionFailure(
                    ProcessInspectionStatus.NativeFailure,
                    Marshal.GetLastWin32Error(),
                    nameof(Process32First)));
        }

        return ReadProcessSnapshot(ToSeed(entry), ReadNext);

        (ProcessInspectionSeed? Process, int NativeError) ReadNext()
        {
            entry.Size = (uint)Marshal.SizeOf<PROCESSENTRY32>();
            return Process32Next(snapshot, ref entry)
                ? (ToSeed(entry), 0)
                : (null, Marshal.GetLastWin32Error());
        }
    }

    internal static ProcessEnumerationResult ReadProcessSnapshot(
        ProcessInspectionSeed first,
        Func<(ProcessInspectionSeed? Process, int NativeError)> readNext)
    {
        var entries = new List<ProcessInspectionSeed> { first };
        while (true)
        {
            var next = readNext();
            if (next.Process is null)
            {
                return next.NativeError == ErrorNoMoreFiles
                    ? ProcessEnumerationResult.Success(entries)
                    : ProcessEnumerationResult.Failed(
                        new ProcessInspectionFailure(
                            ProcessInspectionStatus.NativeFailure,
                            next.NativeError,
                            nameof(Process32Next)));
            }

            entries.Add(next.Process);
        }
    }

    private static ProcessInspectionSeed ToSeed(PROCESSENTRY32 entry) =>
        new(
            checked((int)entry.ProcessId),
            checked((int)entry.ParentProcessId),
            Path.GetFileNameWithoutExtension(entry.ExecutableFile ?? string.Empty));

    private static ProcessInspectionRecord Unavailable(
        ProcessInspectionSeed entry,
        ProcessInspectionStatus status,
        string? path,
        DateTimeOffset? startedAt) =>
        new(entry.ProcessId, entry.ParentProcessId, entry.Name, path, startedAt, null, status);

    private static IntPtr Add(IntPtr address, int offset) => new(address.ToInt64() + offset);
    private static IntPtr Add(long address, int offset) => new(address + offset);

    internal sealed record ProcessInspectionSeed(int ProcessId, int ParentProcessId, string Name);
    internal sealed record ProcessParentIdReadResult(
        int? ParentProcessId,
        ProcessInspectionStatus Status,
        ProcessInspectionFailure? Failure);
    internal sealed record ProcessInspectionResult(
        IReadOnlyDictionary<int, ProcessInspectionRecord> Records,
        ProcessInspectionFailure? Failure)
    {
        internal IReadOnlyList<ProcessTreeEdgeVerdict> EdgeVerdicts { get; init; } = [];
        internal int TruncatedEdgeVerdictCount { get; init; }

        public static ProcessInspectionResult Success(IReadOnlyDictionary<int, ProcessInspectionRecord> records) =>
            new(records, null);

        public static ProcessInspectionResult Failed(ProcessInspectionFailure failure) =>
            new(new Dictionary<int, ProcessInspectionRecord>(), failure);
    }

    internal sealed record ProcessEnumerationResult(
        IReadOnlyList<ProcessInspectionSeed> Processes,
        ProcessInspectionFailure? Failure)
    {
        public static ProcessEnumerationResult Success(IReadOnlyList<ProcessInspectionSeed> processes) =>
            new(processes, null);

        public static ProcessEnumerationResult Failed(ProcessInspectionFailure failure) =>
            new(Array.Empty<ProcessInspectionSeed>(), failure);
    }
    internal readonly record struct ProcessOpenResult(IntPtr Handle, int Error);
    internal sealed record ProcessLifecycleIdentityReadResult(
        string? ExecutablePath,
        DateTimeOffset? StartedAt,
        ProcessInspectionStatus Status,
        int NativeError,
        string Operation);
    internal sealed record OpenedProcessReadResult(
        int ParentProcessId,
        string? ExecutablePath,
        DateTimeOffset? StartedAt,
        string? CommandLine,
        ProcessInspectionStatus Status);
    internal readonly record struct ProcessMemoryLayout(
        int PointerSize,
        int ProcessParametersOffset,
        int CommandLineOffset);
    private readonly record struct CommandLineReadResult(string? CommandLine, ProcessInspectionStatus Status);
    private readonly record struct PointerReadResult(long Value, bool Succeeded, ProcessInspectionStatus Status);
    private readonly record struct ValueReadResult<T>(T Value, ProcessInspectionStatus Status);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool Process32First(IntPtr snapshot, ref PROCESSENTRY32 entry);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool Process32Next(IntPtr snapshot, ref PROCESSENTRY32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(int desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(IntPtr processHandle, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetProcessId(IntPtr processHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(IntPtr processHandle, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(
        IntPtr processHandle,
        int flags,
        StringBuilder executablePath,
        ref uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessTimes(
        IntPtr processHandle,
        out long creationTime,
        out long exitTime,
        out long kernelTime,
        out long userTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool IsWow64Process2(
        IntPtr processHandle,
        out ushort processMachine,
        out ushort nativeMachine);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        IntPtr processHandle,
        int processInformationClass,
        out PROCESS_BASIC_INFORMATION processInformation,
        int processInformationLength,
        out int returnLength);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        IntPtr processHandle,
        int processInformationClass,
        out IntPtr processInformation,
        int processInformationLength,
        out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(
        IntPtr processHandle,
        IntPtr baseAddress,
        byte[] buffer,
        int size,
        out IntPtr bytesRead);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_BASIC_INFORMATION
    {
        public IntPtr Reserved1;
        public IntPtr PebBaseAddress;
        public IntPtr Reserved2_0;
        public IntPtr Reserved2_1;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct PROCESSENTRY32
    {
        public uint Size;
        public uint UsageCount;
        public uint ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId;
        public uint ThreadCount;
        public uint ParentProcessId;
        public int BasePriority;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string ExecutableFile;
    }
}
