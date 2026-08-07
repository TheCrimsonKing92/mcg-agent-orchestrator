using System.Globalization;
using System.Runtime.InteropServices;

namespace Mcg.AgentOrchestrator.App.Processes;

internal static class ProcessParentIdResolver
{
    public static int TryGetParentProcessId(int processId)
    {
        if (OperatingSystem.IsWindows())
        {
            return TryGetWindowsParentProcessId(processId);
        }

        if (OperatingSystem.IsLinux())
        {
            return TryGetLinuxParentProcessId(processId);
        }

        return 0;
    }

    private static int TryGetLinuxParentProcessId(int processId)
    {
        try
        {
            var stat = File.ReadAllText($"/proc/{processId}/stat");
            var lastParen = stat.LastIndexOf(')');
            if (lastParen < 0)
            {
                return 0;
            }

            var fields = stat[(lastParen + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return fields.Length >= 2 && int.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parentId)
                ? parentId
                : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static int TryGetWindowsParentProcessId(int processId)
    {
        var snapshot = CreateToolhelp32Snapshot(0x00000002, 0);
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1))
        {
            return 0;
        }

        try
        {
            var entry = new ProcessEntry32 { Size = (uint)Marshal.SizeOf<ProcessEntry32>() };
            if (!Process32First(snapshot, ref entry))
            {
                return 0;
            }

            do
            {
                if (entry.ProcessId == (uint)processId)
                {
                    return (int)entry.ParentProcessId;
                }
            }
            while (Process32Next(snapshot, ref entry));
        }
        finally
        {
            CloseHandle(snapshot);
        }

        return 0;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessEntry32
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
