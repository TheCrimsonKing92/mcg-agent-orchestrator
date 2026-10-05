using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed record FileHandleHolder(int ProcessId, string? ProcessName)
{
    public override string ToString() =>
        $"{ProcessId}:{(string.IsNullOrWhiteSpace(ProcessName) ? "unknown" : ProcessName)}";
}

internal sealed record FileHandleHolderObservation(
    IReadOnlyList<FileHandleHolder> Holders,
    string? Failure)
{
    public static FileHandleHolderObservation Failed(string failure) => new([], failure);

    public string Format() =>
        Failure is not null ? $"unavailable({Failure})" :
        Holders.Count == 0 ? "none" :
        string.Join(',', Holders.Select(holder => holder.ToString()));
}

/// <summary>
/// Names the processes that currently hold an open handle to a file.
/// <para>
/// This asks the file system directly through NtQueryInformationFile
/// (FileProcessIdsUsingFileInformation) rather than inferring a holder from command lines, so it
/// reports the actual owner of a sharing violation - including a process that inherited the handle
/// and never named the path. It needs no elevation, spawns no process, and the probe handle itself
/// requests only FILE_READ_ATTRIBUTES with full sharing, so it never competes for the file.
/// </para>
/// <para>
/// <see cref="Read"/> never throws: every refusal is returned as failure text so a diagnostic read
/// on an exception path cannot become a new failure mode. Tests substitute it by passing a
/// <c>Func&lt;string, FileHandleHolderObservation&gt;</c> to the caller that needs it; there is no
/// process-global seam here.
/// </para>
/// </summary>
internal static class FileHandleHolders
{
    private const uint FileReadAttributes = 0x00000080;
    private const uint FileShareAll = 0x00000001 | 0x00000002 | 0x00000004;
    private const uint OpenExisting = 3;
    private const uint FileAttributeNormal = 0x00000080;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const int FileProcessIdsUsingFileInformation = 47;
    private const uint StatusInfoLengthMismatch = 0xC0000004;
    private const uint StatusBufferOverflow = 0x80000005;
    private const uint StatusBufferTooSmall = 0xC0000023;
    private const int MaxHolders = 64;

    internal static FileHandleHolderObservation Read(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return FileHandleHolderObservation.Failed("unsupported-platform");
        }

        try
        {
            return ReadWindows(path);
        }
        catch (Exception exception)
        {
            return FileHandleHolderObservation.Failed(exception.GetType().Name);
        }
    }

    private static FileHandleHolderObservation ReadWindows(string path)
    {
        using var handle = CreateFileW(
            path,
            FileReadAttributes,
            FileShareAll,
            IntPtr.Zero,
            OpenExisting,
            FileAttributeNormal | FileFlagBackupSemantics,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            return FileHandleHolderObservation.Failed($"CreateFileW-0x{Marshal.GetLastWin32Error():X8}");
        }

        var length = 512;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var buffer = Marshal.AllocHGlobal(length);
            try
            {
                var status = NtQueryInformationFile(
                    handle,
                    out _,
                    buffer,
                    (uint)length,
                    FileProcessIdsUsingFileInformation);
                if (status == StatusInfoLengthMismatch ||
                    status == StatusBufferOverflow ||
                    status == StatusBufferTooSmall)
                {
                    length *= 8;
                    continue;
                }

                if (status != 0)
                {
                    return FileHandleHolderObservation.Failed($"NtQueryInformationFile-0x{status:X8}");
                }

                return new FileHandleHolderObservation(ReadHolders(buffer, length), null);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        return FileHandleHolderObservation.Failed("NtQueryInformationFile-buffer-exhausted");
    }

    private static IReadOnlyList<FileHandleHolder> ReadHolders(IntPtr buffer, int length)
    {
        // FILE_PROCESS_IDS_USING_FILE_INFORMATION: ULONG count, then a pointer-aligned ULONG_PTR array.
        var count = Marshal.ReadInt32(buffer);
        var stride = IntPtr.Size;
        var available = (length - stride) / stride;
        if (count < 0 || available < 0)
        {
            return [];
        }

        var readable = Math.Min(Math.Min(count, available), MaxHolders);
        var holders = new List<FileHandleHolder>(readable);
        for (var index = 0; index < readable; index++)
        {
            var processId = Marshal.ReadIntPtr(buffer, stride + (index * stride)).ToInt64();
            if (processId is <= 0 or > int.MaxValue)
            {
                continue;
            }

            holders.Add(new FileHandleHolder((int)processId, TryReadProcessName((int)processId)));
        }

        return holders;
    }

    private static string? TryReadProcessName(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.ProcessName;
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_STATUS_BLOCK
    {
        public IntPtr Status;
        public IntPtr Information;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("ntdll.dll")]
    private static extern uint NtQueryInformationFile(
        SafeFileHandle fileHandle,
        out IO_STATUS_BLOCK ioStatusBlock,
        IntPtr fileInformation,
        uint length,
        int fileInformationClass);
}
