using System.Runtime.InteropServices;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal enum ProcessCurrentDirectoryStatus
{
    Available,
    AccessDenied,
    ProcessExited,
    Unsupported,
    ReadFailed
}

internal sealed record ProcessCurrentDirectoryReadResult(
    string? Path, ProcessCurrentDirectoryStatus Status, int NativeError = 0);

internal static partial class WindowsNativeProcessInspection
{
    internal static ProcessCurrentDirectoryReadResult ReadCurrentDirectory(int processId)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new(null, ProcessCurrentDirectoryStatus.Unsupported);
        }

        var handle = IntPtr.Zero;
        try
        {
            var opened = OpenForInspection(processId);
            handle = opened.Handle;
            if (handle == IntPtr.Zero)
            {
                return CurrentDirectoryFailure(StatusFromError(opened.Error), opened.Error);
            }

            if (!TryReadExitCode(handle, out var status))
            {
                return CurrentDirectoryFailure(status);
            }

            var basicStatus = NtQueryInformationProcess(handle, ProcessBasicInformation,
                out PROCESS_BASIC_INFORMATION basic, Marshal.SizeOf<PROCESS_BASIC_INFORMATION>(), out _);
            if (basicStatus != 0 || basic.PebBaseAddress == IntPtr.Zero)
            {
                return new(null, ProcessCurrentDirectoryStatus.ReadFailed, basicStatus);
            }

            var value = ReadCurrentDirectoryFromParameters(handle, basic.PebBaseAddress);
            if (!TryReadExitCode(handle, out status))
            {
                return CurrentDirectoryFailure(status);
            }

            return value.Status == ProcessInspectionStatus.Available && !string.IsNullOrWhiteSpace(value.CommandLine)
                ? new(System.IO.Path.TrimEndingDirectorySeparator(value.CommandLine), ProcessCurrentDirectoryStatus.Available)
                : CurrentDirectoryFailure(value.Status == ProcessInspectionStatus.Available
                    ? ProcessInspectionStatus.MalformedData : value.Status);
        }
        catch (EntryPointNotFoundException)
        {
            return new(null, ProcessCurrentDirectoryStatus.Unsupported);
        }
        catch
        {
            return new(null, ProcessCurrentDirectoryStatus.ReadFailed);
        }
        finally
        {
            if (handle != IntPtr.Zero)
            {
                CloseHandle(handle);
            }
        }
    }

    private static CommandLineReadResult ReadCurrentDirectoryFromParameters(IntPtr handle, IntPtr nativePebAddress)
    {
        // Select the target's PEB and pointer width exactly as the command-line reader does.
        if (IntPtr.Size == 4 && Environment.Is64BitOperatingSystem)
        {
            if (!IsWow64Process2(handle, out var processMachine, out _))
            {
                return new(null, StatusFromLastError());
            }

            if (processMachine == 0)
            {
                return new(null, ProcessInspectionStatus.UnsupportedTarget);
            }
        }

        var pointerSize = IntPtr.Size;
        var pebAddress = nativePebAddress;
        if (IntPtr.Size == 8)
        {
            var wow64Status = NtQueryInformationProcess(handle, ProcessWow64Information,
                out IntPtr wow64Peb, IntPtr.Size, out _);
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
        if (!parameters.Succeeded || parameters.Value == 0)
        {
            return new(null, parameters.Succeeded ? ProcessInspectionStatus.MalformedData : parameters.Status);
        }

        // CURDIR begins with its DosPath UNICODE_STRING, beside CommandLine in the same block.
        return ReadUnicodeString(handle, Add(parameters.Value, pointerSize == 8 ? 0x38 : 0x24), pointerSize);
    }

    private static ProcessCurrentDirectoryReadResult CurrentDirectoryFailure(ProcessInspectionStatus status, int error = 0) =>
        new(null, status switch
        {
            ProcessInspectionStatus.AccessDenied => ProcessCurrentDirectoryStatus.AccessDenied,
            ProcessInspectionStatus.Exited or ProcessInspectionStatus.DeadOrRecycled => ProcessCurrentDirectoryStatus.ProcessExited,
            ProcessInspectionStatus.UnsupportedTarget => ProcessCurrentDirectoryStatus.Unsupported,
            _ => ProcessCurrentDirectoryStatus.ReadFailed
        }, error);
}
