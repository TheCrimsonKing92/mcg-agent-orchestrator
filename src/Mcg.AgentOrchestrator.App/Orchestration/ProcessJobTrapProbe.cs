using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal readonly record struct ProcessJobReading(bool? InJob, uint? LimitFlags)
{
    internal static ProcessJobReading Unknown => new(null, null);
}

internal interface IProcessJobProbe
{
    ProcessJobReading Read();
}

// A reading describes only the current process's innermost job, never its ancestors.
internal sealed class ProcessJobTrapProbe : IProcessJobProbe
{
    private const uint KillOnJobClose = 0x2000;
    private const uint BreakawayOk = 0x800;
    private const uint SilentBreakawayOk = 0x1000;
    private static readonly AsyncLocal<ProcessJobReading?> SupervisorReading = new();
    private readonly Func<ProcessJobReading> _read;

    private ProcessJobTrapProbe(Func<ProcessJobReading> read) => _read = read;

    internal static IProcessJobProbe Native { get; } = new ProcessJobTrapProbe(ReadCurrentProcess);
    internal static IProcessJobProbe UnknownProbe { get; } = new ProcessJobTrapProbe(() => ProcessJobReading.Unknown);

    public ProcessJobReading Read() => _read();

    internal static bool Traps(ProcessJobReading reading) =>
        reading.InJob == true && reading.LimitFlags is { } flags &&
        (flags & KillOnJobClose) != 0 && (flags & (BreakawayOk | SilentBreakawayOk)) == 0;

    internal static string FormatFlags(uint flags) => "0x" + flags.ToString("X8", CultureInfo.InvariantCulture);

    internal static string FormatMembership(ProcessJobReading reading) => reading.InJob switch
    {
        true => "true",
        false => "false",
        null => "unknown"
    };

    internal static ProcessJobReading ReadForSupervisor() => SupervisorReading.Value ?? ReadCurrentProcess();

    internal static IDisposable UseSupervisorReading(ProcessJobReading reading)
    {
        var previous = SupervisorReading.Value;
        SupervisorReading.Value = reading;
        return new RestoreReading(previous);
    }

    private sealed class RestoreReading(ProcessJobReading? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            SupervisorReading.Value = previous;
            _disposed = true;
        }
    }

    internal static ProcessJobReading ReadCurrentProcess()
    {
        if (!OperatingSystem.IsWindows()) return ProcessJobReading.Unknown;
        try
        {
            using var self = Process.GetCurrentProcess();
            if (!IsProcessInJob(self.Handle, IntPtr.Zero, out var inJob)) return ProcessJobReading.Unknown;
            if (!inJob) return new(false, null);
            // A NULL query reads the innermost job, as in the launch-authority diagnostic.
            return QueryInformationJobObject(IntPtr.Zero, 9, out var info,
                (uint)Marshal.SizeOf<JobExtendedLimits>(), IntPtr.Zero)
                ? new(true, info.Basic.LimitFlags) : ProcessJobReading.Unknown;
        }
        catch (Exception)
        {
            return ProcessJobReading.Unknown;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobBasicLimits
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobExtendedLimits
    {
        public JobBasicLimits Basic;
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsProcessInJob(IntPtr process, IntPtr job,
        [MarshalAs(UnmanagedType.Bool)] out bool result);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(IntPtr job, int informationClass,
        out JobExtendedLimits information, uint length, IntPtr returnLength);
}
