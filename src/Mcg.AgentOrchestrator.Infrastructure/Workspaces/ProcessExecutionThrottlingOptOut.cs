using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class ProcessExecutionThrottlingOptOut
{
    internal const int ProcessPowerThrottlingClass = 4;
    internal const uint StateVersion = 1;
    internal const uint ControlMaskExecutionSpeed = 0x1;
    internal const uint StateMaskHighQos = 0;
    internal const uint ProcessSetInformationAccess = 0x0200;

    [StructLayout(LayoutKind.Sequential)]
    internal struct PowerThrottlingState
    {
        internal uint Version;
        internal uint ControlMask;
        internal uint StateMask;
    }

    internal static bool TryApply(int processId) =>
        TrySet(processId, ControlMaskExecutionSpeed, StateMaskHighQos);

    internal static bool TrySet(int processId, uint controlMask, uint stateMask)
    {
        if (!OperatingSystem.IsWindows() || processId <= 0 ||
            controlMask != ControlMaskExecutionSpeed || stateMask != StateMaskHighQos) return false;
        try
        {
            using var process = OpenProcess(ProcessSetInformationAccess, false, processId);
            if (process.IsInvalid) return false;
            var state = new PowerThrottlingState
            {
                Version = StateVersion, ControlMask = ControlMaskExecutionSpeed, StateMask = StateMaskHighQos
            };
            return SetProcessInformation(process, ProcessPowerThrottlingClass, ref state,
                (uint)Marshal.SizeOf<PowerThrottlingState>());
        }
        catch (Exception) { return false; }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessInformation(SafeProcessHandle process, int informationClass,
        ref PowerThrottlingState information, uint size);
}
