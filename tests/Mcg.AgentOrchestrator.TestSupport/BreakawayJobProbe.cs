using System.Runtime.InteropServices;

public static class BreakawayJobProbe
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitBreakawayOk = 0x00000800;
    private const uint JobObjectLimitSilentBreakawayOk = 0x00001000;

    private static readonly Lazy<BreakawayProbeResult> Cached = new(
        QueryCurrentProcess,
        LazyThreadSafetyMode.ExecutionAndPublication);

    public static BreakawayProbeResult CanCreateBreakawayChild() => Cached.Value;

    public static BreakawayProbeResult DecideFromJobState(bool isInJob, uint limitFlags)
    {
        if (!isInJob)
        {
            return new BreakawayProbeResult(
                BreakawayVerdict.Permitted,
                "The current process is not in a job.",
                0);
        }

        var permitsBreakaway = (limitFlags &
            (JobObjectLimitBreakawayOk | JobObjectLimitSilentBreakawayOk)) != 0;
        return permitsBreakaway
            ? new BreakawayProbeResult(
                BreakawayVerdict.Permitted,
                $"The current job permits breakaway (limitFlags=0x{limitFlags:X8}).",
                0)
            : new BreakawayProbeResult(
                BreakawayVerdict.Forbidden,
                $"The current job forbids breakaway (limitFlags=0x{limitFlags:X8}).",
                0);
    }

    private static BreakawayProbeResult QueryCurrentProcess()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new BreakawayProbeResult(
                BreakawayVerdict.NotApplicable,
                "Windows job objects are not applicable on this platform.",
                0);
        }

        if (!IsProcessInJob(GetCurrentProcess(), IntPtr.Zero, out var isInJob))
        {
            return QueryFailure("IsProcessInJob");
        }

        if (!isInJob)
        {
            return DecideFromJobState(isInJob: false, limitFlags: 0);
        }

        var length = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        var buffer = Marshal.AllocHGlobal(length);
        try
        {
            if (!QueryInformationJobObject(
                    IntPtr.Zero,
                    JobObjectExtendedLimitInformation,
                    buffer,
                    (uint)length,
                    IntPtr.Zero))
            {
                return QueryFailure("QueryInformationJobObject");
            }

            var information = Marshal.PtrToStructure<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>(buffer);
            // The NULL-handle query exposes only the innermost job. A forbidding inner job is
            // authoritative; an undetectable forbidding outer job remains a fail-loud launch.
            return DecideFromJobState(
                isInJob: true,
                information.BasicLimitInformation.LimitFlags);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static BreakawayProbeResult QueryFailure(string operation)
    {
        var errorCode = Marshal.GetLastWin32Error();
        return new BreakawayProbeResult(
            BreakawayVerdict.Permitted,
            $"{operation} failed with Win32 error {errorCode}; running the launcher test to fail loudly.",
            errorCode);
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool IsProcessInJob(
        IntPtr processHandle,
        IntPtr jobHandle,
        [MarshalAs(UnmanagedType.Bool)] out bool result);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool QueryInformationJobObject(
        IntPtr hJob,
        int jobObjectInfoClass,
        IntPtr lpJobObjectInfo,
        uint cbJobObjectInfoLength,
        IntPtr lpReturnLength);

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }
}
