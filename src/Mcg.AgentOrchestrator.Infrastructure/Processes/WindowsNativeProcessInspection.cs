using System.ComponentModel;
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

internal static class WindowsNativeProcessInspection
{
    private const uint SnapshotProcesses = 0x00000002;
    private const int ProcessQueryLimitedInformation = 0x1000;
    private const int ProcessVmRead = 0x0010;
    private const int ProcessTerminate = 0x0001;
    private const int Synchronize = 0x00100000;
    private const int ProcessBasicInformation = 0;
    private const int ProcessWow64Information = 26;
    private const int ErrorAccessDenied = 5;
    private const int ErrorInvalidParameter = 87;
    private const int ErrorPartialCopy = 299;
    private const int MaxCommandLineBytes = 32766;
    private const uint StillActive = 259;

    public static IReadOnlyDictionary<int, ProcessInspectionRecord> Read(IEnumerable<int>? requestedProcessIds = null)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new Dictionary<int, ProcessInspectionRecord>();
        }

        if (requestedProcessIds is null)
        {
            return Read(null, EnumerateProcesses, ReadOne);
        }

        var ids = requestedProcessIds.Where(id => id > 0).Distinct().ToArray();
        return Read(ids, () => CreateRequestedSeeds(ids), ReadOne);
    }

    public static IReadOnlyDictionary<int, ProcessInspectionRecord> ReadByNames(IEnumerable<string> processNames)
    {
        var names = processNames
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => Path.GetFileNameWithoutExtension(name)!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return ReadByNames(names, EnumerateProcesses, ReadOne);
    }

    internal static IReadOnlyDictionary<int, ProcessInspectionRecord> ReadByNames(
        IReadOnlySet<string> processNames,
        Func<IReadOnlyList<ProcessInspectionSeed>> enumerate,
        Func<ProcessInspectionSeed, ProcessInspectionRecord> readOne) =>
        Read(null, enumerate, readOne, entry => processNames.Contains(entry.Name));

    internal static IReadOnlyDictionary<int, ProcessInspectionRecord> Read(
        IEnumerable<int>? requestedProcessIds,
        Func<IReadOnlyList<ProcessInspectionSeed>> enumerate,
        Func<ProcessInspectionSeed, ProcessInspectionRecord> readOne,
        Func<ProcessInspectionSeed, bool>? include = null)
    {
        var entries = enumerate();
        IEnumerable<ProcessInspectionSeed> selected = entries;
        if (requestedProcessIds is not null)
        {
            var ids = requestedProcessIds.Where(id => id > 0).ToHashSet();
            selected = entries.Where(entry => ids.Contains(entry.ProcessId));
        }

        if (include is not null)
        {
            selected = selected.Where(include);
        }

        var result = new Dictionary<int, ProcessInspectionRecord>();
        foreach (var entry in selected)
        {
            result[entry.ProcessId] = readOne(entry);
        }

        return result;
    }

    internal static ProcessMemoryLayout GetMemoryLayout(bool targetIsWow64) =>
        targetIsWow64 || IntPtr.Size == 4
            ? new ProcessMemoryLayout(4, 0x10, 0x40)
            : new ProcessMemoryLayout(8, 0x20, 0x70);

    private static IReadOnlyList<ProcessInspectionSeed> CreateRequestedSeeds(IEnumerable<int> processIds)
    {
        return processIds
            .Select(static processId => new ProcessInspectionSeed(processId, 0, string.Empty))
            .ToArray();
    }

    public static int TryGetParentProcessId(int processId) =>
        Read([processId]).TryGetValue(processId, out var record) ? record.ParentProcessId : 0;

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

    private static ProcessInspectionStatus StatusFromLastError() => Marshal.GetLastWin32Error() switch
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

    private static IReadOnlyList<ProcessInspectionSeed> EnumerateProcesses()
    {
        var snapshot = CreateToolhelp32Snapshot(SnapshotProcesses, 0);
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not enumerate Windows processes.");
        }

        try
        {
            var entries = new List<ProcessInspectionSeed>();
            var entry = new PROCESSENTRY32 { Size = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
            if (!Process32First(snapshot, ref entry))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not read the Windows process snapshot.");
            }

            do
            {
                entries.Add(new ProcessInspectionSeed(
                    checked((int)entry.ProcessId),
                    checked((int)entry.ParentProcessId),
                    Path.GetFileNameWithoutExtension(entry.ExecutableFile ?? string.Empty)));
                entry.Size = (uint)Marshal.SizeOf<PROCESSENTRY32>();
            }
            while (Process32Next(snapshot, ref entry));

            return entries;
        }
        finally
        {
            CloseHandle(snapshot);
        }
    }

    private static ProcessInspectionRecord Unavailable(
        ProcessInspectionSeed entry,
        ProcessInspectionStatus status,
        string? path,
        DateTimeOffset? startedAt) =>
        new(entry.ProcessId, entry.ParentProcessId, entry.Name, path, startedAt, null, status);

    private static IntPtr Add(IntPtr address, int offset) => new(address.ToInt64() + offset);
    private static IntPtr Add(long address, int offset) => new(address + offset);

    internal sealed record ProcessInspectionSeed(int ProcessId, int ParentProcessId, string Name);
    internal readonly record struct ProcessOpenResult(IntPtr Handle, int Error);
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
