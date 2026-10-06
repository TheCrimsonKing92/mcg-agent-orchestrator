using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorJudgePanelTriggerSources
{
    private static string FileIdentity(FileStream stream, FileInfo info)
    {
        if (!OperatingSystem.IsWindows()) return info.CreationTimeUtc.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture);
        // NTFS can preserve creation timestamps on replacement (file tunneling).
        // Bind the cursor to the open handle's volume and file index instead.
        if (!GetFileInformationByHandle(stream.SafeFileHandle, out var identity))
            throw new IOException("Could not read panel producer file identity.", new Win32Exception(Marshal.GetLastWin32Error()));
        return $"{identity.VolumeSerialNumber:X8}:{identity.FileIndexHigh:X8}{identity.FileIndexLow:X8}";
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out ProducerFileInformation information);

    [StructLayout(LayoutKind.Sequential)]
    private struct ProducerFileInformation
    {
        public uint FileAttributes;
        public uint CreationTimeLow, CreationTimeHigh;
        public uint LastAccessTimeLow, LastAccessTimeHigh;
        public uint LastWriteTimeLow, LastWriteTimeHigh;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh, FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh, FileIndexLow;
    }
}
