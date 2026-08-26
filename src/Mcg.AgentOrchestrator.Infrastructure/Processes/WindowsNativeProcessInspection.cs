using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Mcg.AgentOrchestrator.Infrastructure;

public enum ProcessInspectionStatus
{
    Available,
    AccessDenied,
    Exited,
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
    private const int ProcessBasicInformation = 0;
    private const int ProcessWow64Information = 26;
    private const int ErrorAccessDenied = 5;
    private const int ErrorInvalidParameter = 87;
    private const int ErrorPartialCopy = 299;
    private const int MaxCommandLineBytes = 32766;

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

    internal static IReadOnlyDictionary<int, ProcessInspectionRecord> Read(
        IEnumerable<int>? requestedProcessIds,
        Func<IReadOnlyList<ProcessInspectionSeed>> enumerate,
        Func<ProcessInspectionSeed, ProcessInspectionRecord> readOne)
    {
        var entries = enumerate();
        IEnumerable<ProcessInspectionSeed> selected = entries;
        if (requestedProcessIds is not null)
        {
            var ids = requestedProcessIds.Where(id => id > 0).ToHashSet();
            selected = entries.Where(entry => ids.Contains(entry.ProcessId));
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
        var seeds = new List<ProcessInspectionSeed>();
        foreach (var processId in processIds)
        {
            var name = string.Empty;
            try
            {
                using var process = Process.GetProcessById(processId);
                name = process.ProcessName;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception or NotSupportedException)
            {
            }

            seeds.Add(new ProcessInspectionSeed(processId, 0, name));
        }

        return seeds;
    }

    public static int TryGetParentProcessId(int processId) =>
        EnumerateProcesses().FirstOrDefault(entry => entry.ProcessId == processId)?.ParentProcessId ?? 0;

    private static ProcessInspectionRecord ReadOne(ProcessInspectionSeed entry)
    {
        string? path = null;
        DateTimeOffset? startedAt = null;
        try
        {
            using var process = Process.GetProcessById(entry.ProcessId);
            if (process.HasExited)
            {
                return Unavailable(entry, ProcessInspectionStatus.Exited, path, startedAt);
            }

            try { path = process.MainModule?.FileName; } catch (Exception ex) when (IsExpectedInspectionFailure(ex)) { }
            try { startedAt = new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero); } catch (Exception ex) when (IsExpectedInspectionFailure(ex)) { }
        }
        catch (ArgumentException)
        {
            return Unavailable(entry, ProcessInspectionStatus.Exited, path, startedAt);
        }
        catch (Exception ex) when (IsExpectedInspectionFailure(ex))
        {
            // Command-line inspection below supplies the more specific availability status.
        }

        var handle = OpenProcess(ProcessQueryLimitedInformation | ProcessVmRead, false, entry.ProcessId);
        if (handle == IntPtr.Zero)
        {
            var error = Marshal.GetLastWin32Error();
            return Unavailable(
                entry,
                error == ErrorAccessDenied
                    ? ProcessInspectionStatus.AccessDenied
                    : error == ErrorInvalidParameter
                        ? ProcessInspectionStatus.Exited
                        : ProcessInspectionStatus.NativeFailure,
                path,
                startedAt);
        }

        try
        {
            var read = ReadCommandLine(handle);
            return new ProcessInspectionRecord(
                entry.ProcessId,
                entry.ParentProcessId,
                entry.Name,
                path,
                startedAt,
                read.CommandLine,
                read.Status);
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static CommandLineReadResult ReadCommandLine(IntPtr handle)
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

        var basicStatus = NtQueryInformationProcess(
            handle,
            ProcessBasicInformation,
            out PROCESS_BASIC_INFORMATION basic,
            Marshal.SizeOf<PROCESS_BASIC_INFORMATION>(),
            out _);
        if (basicStatus != 0 || basic.PebBaseAddress == IntPtr.Zero)
        {
            return new(null, ProcessInspectionStatus.NativeFailure);
        }

        var pointerSize = IntPtr.Size;
        var pebAddress = basic.PebBaseAddress;
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

    private static bool IsExpectedInspectionFailure(Exception ex) =>
        ex is InvalidOperationException or Win32Exception or NotSupportedException;

    private static IntPtr Add(IntPtr address, int offset) => new(address.ToInt64() + offset);
    private static IntPtr Add(long address, int offset) => new(address + offset);

    internal sealed record ProcessInspectionSeed(int ProcessId, int ParentProcessId, string Name);
    internal readonly record struct ProcessMemoryLayout(
        int PointerSize,
        int ProcessParametersOffset,
        int CommandLineOffset);
    private readonly record struct CommandLineReadResult(string? CommandLine, ProcessInspectionStatus Status);
    private readonly record struct PointerReadResult(long Value, bool Succeeded, ProcessInspectionStatus Status);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool Process32First(IntPtr snapshot, ref PROCESSENTRY32 entry);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool Process32Next(IntPtr snapshot, ref PROCESSENTRY32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(int desiredAccess, bool inheritHandle, int processId);

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
