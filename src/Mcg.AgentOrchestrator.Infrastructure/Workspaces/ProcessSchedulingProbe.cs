using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class ProcessSchedulingProbe
{
    internal static ProcessSchedulingSample FromNative(int pid, string image, uint priorityClass,
        uint controlMask, uint stateMask, int memoryPriority) =>
        new(pid, image, Enum.IsDefined(typeof(ProcessPriorityClass), (int)priorityClass)
            ? ((ProcessPriorityClass)priorityClass).ToString() : $"0x{priorityClass:X}",
            controlMask, stateMask, memoryPriority);

    internal static ProcessSchedulingSample Read(int processId)
    {
        if (!OperatingSystem.IsWindows()) return Unavailable("not-windows");
        try
        {
            using var process = OpenProcess(0x1000, false, processId);
            if (process.IsInvalid) return Unavailable($"open-failed:{Marshal.GetLastWin32Error()}");
            var priority = GetPriorityClass(process);
            if (priority == 0) return Unavailable($"priority-failed:{Marshal.GetLastWin32Error()}");
            var power = new ProcessExecutionThrottlingOptOut.PowerThrottlingState { Version = 1 };
            if (!GetPowerInformation(process, 4, ref power, (uint)Marshal.SizeOf<ProcessExecutionThrottlingOptOut.PowerThrottlingState>()))
                return Unavailable($"throttle-failed:{Marshal.GetLastWin32Error()}");
            if (!GetMemoryInformation(process, 0, out var memory, sizeof(uint)))
                return Unavailable($"memory-failed:{Marshal.GetLastWin32Error()}");
            var image = new StringBuilder(32768);
            var size = (uint)image.Capacity;
            if (!QueryFullProcessImageName(process, 0, image, ref size))
                return Unavailable($"image-failed:{Marshal.GetLastWin32Error()}");
            return FromNative(processId, Path.GetFileName(image.ToString()), priority,
                power.ControlMask, power.StateMask, (int)memory);
        }
        catch (Exception error) { return Unavailable("probe-error:" + error.GetType().Name); }

        ProcessSchedulingSample Unavailable(string reason) => new(processId, "unknown", null, null, null, null, reason);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetPriorityClass(SafeProcessHandle process);

    [DllImport("kernel32.dll", EntryPoint = "GetProcessInformation", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPowerInformation(SafeProcessHandle process, int informationClass,
        ref ProcessExecutionThrottlingOptOut.PowerThrottlingState information, uint size);

    [DllImport("kernel32.dll", EntryPoint = "GetProcessInformation", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMemoryInformation(SafeProcessHandle process, int informationClass, out uint memory, uint size);

    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder image, ref uint size);
}
