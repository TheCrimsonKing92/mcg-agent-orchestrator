using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static partial class ConductorLoopHandoff
{
    // NULL queries describe only the innermost job. They cannot identify an ancestor
    // boundary or prove lifetime escape; fixtures must retain those owning handles.
    private static object CaptureWindowsLaunchAuthority(uint flags, string application)
    {
        try
        {
            using var self = Process.GetCurrentProcess();
            var membership = ProbeProcessJobMembership(self.Handle);
            uint? limits = null;
            var queryError = 0;
            if (membership == "true")
            {
                if (QueryLaunchJobInformation(IntPtr.Zero, 9, out var info,
                        (uint)Marshal.SizeOf<LaunchJobExtendedLimits>(), IntPtr.Zero))
                    limits = info.Basic.LimitFlags;
                else
                    queryError = Marshal.GetLastWin32Error();
            }
            return new
            {
                creationFlags = $"0x{flags:X8}", application,
                incumbentPid = self.Id, incumbentStartedAt = self.StartTime.ToUniversalTime(),
                incumbentImage = Environment.ProcessPath,
                assemblyPath = typeof(ConductorLoopHandoff).Assembly.Location,
                assemblyMvid = typeof(ConductorLoopHandoff).Module.ModuleVersionId,
                incumbentJobMembership = membership, innermostJobLimitFlags = limits,
                jobQueryNativeError = queryError, jobQueryScope = "innermost-only",
                ancestorJobIdentity = "unavailable"
            };
        }
        catch (Exception ex)
        {
            return new { creationFlags = $"0x{flags:X8}", application,
                diagnosticFailure = $"{ex.GetType().Name}:{ex.Message}" };
        }
    }

    private static void TryAppendWindowsLaunchAuthority(
        ConductLoopLaunchRequest request, object authority, int nativeError, int? successorPid,
        DateTimeOffset? successorStartedAt = null, string? fallback = null)
    {
        // The caller captures CreateProcessW's error before this best-effort diagnostic I/O.
        try
        {
            TryAppendWindowsSpawnEvent(request,
                fallback is null
                    ? JsonSerializer.Serialize(new { api = "CreateProcessW", nativeError, successorPid, successorStartedAt, authority })
                    : JsonSerializer.Serialize(new { api = "CreateProcessW", nativeError, successorPid, successorStartedAt, authority,
                        fallback, fallbackDetail = $"fallback={fallback}" }),
                "loop-handoff-launch-authority");
        }
        catch (Exception ex)
        {
            // Diagnostic failure must not replace the launch error or lose a live successor.
            try { Console.Error.WriteLine($"LOOP_HANDOFF_SPAWN_DIAGNOSTIC_FAILED error={ex.GetType().Name}:{ex.Message}"); }
            catch { }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LaunchJobBasicLimits
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LaunchJobExtendedLimits
    {
        public LaunchJobBasicLimits Basic;
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", EntryPoint = "QueryInformationJobObject", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryLaunchJobInformation(IntPtr job, int informationClass,
        out LaunchJobExtendedLimits information, uint length, IntPtr returnLength);
}
