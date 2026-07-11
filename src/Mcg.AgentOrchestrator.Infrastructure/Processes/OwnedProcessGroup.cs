using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Mcg.AgentOrchestrator.Infrastructure;

/// <summary>
/// Owns a spawned process tree without relying on process names. Windows uses a Job Object with
/// KILL_ON_JOB_CLOSE; Unix best-effort isolates the root in its own process group and kills by pgid.
/// </summary>
internal sealed class OwnedProcessGroup : IDisposable
{
    private readonly List<int> _processIds = [];
    private SafeFileHandle? _jobHandle;
    private int? _processGroupId;
    private bool _disposed;

    private OwnedProcessGroup()
    {
        if (OperatingSystem.IsWindows())
        {
            _jobHandle = WindowsJob.CreateKillOnCloseJob();
        }
    }

    public IReadOnlyList<int> ProcessIds => _processIds;

    public static OwnedProcessGroup Create() => new();

    public static OwnedProcessGroup Attach(Process process)
    {
        var group = Create();
        group.Add(process);
        return group;
    }

    public void Add(Process process)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _processIds.Add(process.Id);

        if (OperatingSystem.IsWindows())
        {
            if (_jobHandle is not null && !WindowsJob.AssignProcessToJobObject(_jobHandle, process.Handle))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to assign process to owned job object.");
            }

            return;
        }

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            _processGroupId ??= process.Id;
            _ = UnixProcessGroups.TrySetProcessGroup(process.Id, _processGroupId.Value);
        }
    }

    public void Kill()
    {
        if (_disposed)
        {
            return;
        }

        if (OperatingSystem.IsWindows())
        {
            if (_jobHandle is not null && !_jobHandle.IsClosed && !_jobHandle.IsInvalid)
            {
                WindowsJob.TryTerminate(_jobHandle);
            }

            Dispose();
            return;
        }

        if ((OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()) && _processGroupId is { } pgid)
        {
            UnixProcessGroups.TryKillProcessGroup(pgid, UnixProcessGroups.SigTerm);
            Thread.Sleep(250);
            UnixProcessGroups.TryKillProcessGroup(pgid, UnixProcessGroups.SigKill);
        }
    }

    public bool TryReadAccounting(out WorkerProcessJobAccounting accounting)
    {
        accounting = WorkerProcessJobAccounting.Empty;
        if (_disposed ||
            !OperatingSystem.IsWindows() ||
            _jobHandle is null ||
            _jobHandle.IsClosed ||
            _jobHandle.IsInvalid)
        {
            return false;
        }

        return WindowsJob.TryReadAccounting(_jobHandle, out accounting);
    }

    public bool TryDuplicateAccountingHandle(out SafeFileHandle duplicate)
    {
        duplicate = new SafeFileHandle(IntPtr.Zero, ownsHandle: true);
        if (_disposed ||
            !OperatingSystem.IsWindows() ||
            _jobHandle is null ||
            _jobHandle.IsClosed ||
            _jobHandle.IsInvalid)
        {
            return false;
        }

        return WindowsJob.TryDuplicateCurrentProcessHandle(_jobHandle, out duplicate);
    }

    public static bool TryReadAccounting(SafeFileHandle jobHandle, out WorkerProcessJobAccounting accounting)
    {
        accounting = WorkerProcessJobAccounting.Empty;
        return OperatingSystem.IsWindows() && WindowsJob.TryReadAccounting(jobHandle, out accounting);
    }

    // Bounded wait for the WHOLE job tree (children and grandchildren) to exit after a kill,
    // polled via a duplicated job handle because Kill() closes the group's own handle. Slot
    // release/handoff must never proceed over a live gate-owned process.
    public static bool WaitForJobExit(SafeFileHandle jobHandle, TimeSpan timeout)
    {
        if (!OperatingSystem.IsWindows())
        {
            return true;
        }

        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (!WindowsJob.TryGetActiveProcessCount(jobHandle, out var activeProcesses))
            {
                return false;
            }

            if (activeProcesses == 0)
            {
                return true;
            }

            Thread.Sleep(50);
        }

        return WindowsJob.TryGetActiveProcessCount(jobHandle, out var finalActiveProcesses) &&
            finalActiveProcesses == 0;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            Kill();
        }

        _disposed = true;
        _jobHandle?.Dispose();
        _jobHandle = null;
    }

    private static class WindowsJob
    {
        private const int JobObjectBasicAccountingInformation = 1;
        private const int JobObjectExtendedLimitInformation = 9;
        private const uint JobObjectLimitKillOnJobClose = 0x00002000;
        private const uint DuplicateSameAccess = 0x00000002;

        public static SafeFileHandle CreateKillOnCloseJob()
        {
            var handle = CreateJobObjectW(IntPtr.Zero, null);
            if (handle.IsInvalid)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to create owned job object.");
            }

            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
            {
                BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
                {
                    LimitFlags = JobObjectLimitKillOnJobClose
                }
            };

            var length = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
            var buffer = Marshal.AllocHGlobal(length);
            try
            {
                Marshal.StructureToPtr(info, buffer, false);
                if (!SetInformationJobObject(handle, JobObjectExtendedLimitInformation, buffer, (uint)length))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to configure owned job object.");
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }

            return handle;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern SafeFileHandle CreateJobObjectW(IntPtr lpJobAttributes, string? lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(
            SafeFileHandle hJob,
            int jobObjectInfoClass,
            IntPtr lpJobObjectInfo,
            uint cbJobObjectInfoLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool QueryInformationJobObject(
            IntPtr hJob,
            int jobObjectInfoClass,
            IntPtr lpJobObjectInfo,
            uint cbJobObjectInfoLength,
            IntPtr lpReturnLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DuplicateHandle(
            IntPtr hSourceProcessHandle,
            IntPtr hSourceHandle,
            IntPtr hTargetProcessHandle,
            out SafeFileHandle lpTargetHandle,
            uint dwDesiredAccess,
            bool bInheritHandle,
            uint dwOptions);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool TerminateJobObject(SafeFileHandle hJob, uint uExitCode);

        public static bool TryDuplicateCurrentProcessHandle(SafeFileHandle job, out SafeFileHandle duplicate)
        {
            duplicate = new SafeFileHandle(IntPtr.Zero, ownsHandle: true);
            if (job.IsClosed || job.IsInvalid)
            {
                return false;
            }

            var addedRef = false;
            try
            {
                job.DangerousAddRef(ref addedRef);
                var currentProcess = GetCurrentProcess();
                return DuplicateHandle(
                    currentProcess,
                    job.DangerousGetHandle(),
                    currentProcess,
                    out duplicate,
                    0,
                    false,
                    DuplicateSameAccess) &&
                    !duplicate.IsInvalid;
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
            finally
            {
                if (addedRef)
                {
                    job.DangerousRelease();
                }
            }
        }

        public static bool TryTerminate(SafeFileHandle job) =>
            !job.IsClosed && !job.IsInvalid && TerminateJobObject(job, 1);

        public static bool TryGetActiveProcessCount(SafeFileHandle job, out uint activeProcessCount)
        {
            activeProcessCount = 0;
            if (job.IsClosed || job.IsInvalid)
            {
                return false;
            }

            if (!TryQuery(job, JobObjectBasicAccountingInformation, out JOBOBJECT_BASIC_ACCOUNTING_INFORMATION basic))
            {
                return false;
            }

            activeProcessCount = basic.ActiveProcesses;
            return true;
        }

        public static bool TryReadAccounting(SafeFileHandle job, out WorkerProcessJobAccounting accounting)
        {
            accounting = WorkerProcessJobAccounting.Empty;
            if (job.IsClosed || job.IsInvalid)
            {
                return false;
            }

            if (!TryQuery(job, JobObjectBasicAccountingInformation, out JOBOBJECT_BASIC_ACCOUNTING_INFORMATION basic) ||
                !TryQuery(job, JobObjectExtendedLimitInformation, out JOBOBJECT_EXTENDED_LIMIT_INFORMATION extended))
            {
                return false;
            }

            var cpuTicks = SaturatingAddNonNegative(basic.TotalUserTime, basic.TotalKernelTime);
            var ioBytes = SaturatingAdd(
                SaturatingAdd(extended.IoInfo.ReadTransferCount, extended.IoInfo.WriteTransferCount),
                extended.IoInfo.OtherTransferCount);
            accounting = new WorkerProcessJobAccounting(
                cpuTicks / TimeSpan.TicksPerMillisecond,
                ToInt64(extended.PeakJobMemoryUsed),
                ToInt64(ioBytes));
            return true;
        }

        private static bool TryQuery<T>(SafeFileHandle job, int infoClass, out T value)
            where T : struct
        {
            value = default;
            var addedRef = false;
            var length = Marshal.SizeOf<T>();
            var buffer = Marshal.AllocHGlobal(length);
            try
            {
                if (job.IsClosed || job.IsInvalid)
                {
                    return false;
                }

                try
                {
                    job.DangerousAddRef(ref addedRef);
                }
                catch (ObjectDisposedException)
                {
                    return false;
                }

                if (!QueryInformationJobObject(job.DangerousGetHandle(), infoClass, buffer, (uint)length, IntPtr.Zero))
                {
                    return false;
                }

                value = Marshal.PtrToStructure<T>(buffer);
                return true;
            }
            finally
            {
                if (addedRef)
                {
                    job.DangerousRelease();
                }

                Marshal.FreeHGlobal(buffer);
            }
        }

        private static ulong SaturatingAdd(ulong left, ulong right)
        {
            var result = left + right;
            return result < left ? ulong.MaxValue : result;
        }

        private static long SaturatingAddNonNegative(long left, long right)
        {
            if (left < 0 || right < 0)
            {
                return 0;
            }

            return long.MaxValue - left < right ? long.MaxValue : left + right;
        }

        private static long ToInt64(UIntPtr value) => ToInt64(value.ToUInt64());

        private static long ToInt64(ulong value) => value > long.MaxValue ? long.MaxValue : (long)value;

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_BASIC_ACCOUNTING_INFORMATION
        {
            public long TotalUserTime;
            public long TotalKernelTime;
            public long ThisPeriodTotalUserTime;
            public long ThisPeriodTotalKernelTime;
            public uint TotalPageFaultCount;
            public uint TotalProcesses;
            public uint ActiveProcesses;
            public uint TotalTerminatedProcesses;
        }

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

    private static class UnixProcessGroups
    {
        public const int SigTerm = 15;
        public const int SigKill = 9;

        public static bool TrySetProcessGroup(int processId, int processGroupId)
        {
            try
            {
                return setpgid(processId, processGroupId) == 0;
            }
            catch
            {
                return false;
            }
        }

        public static bool TryKillProcessGroup(int processGroupId, int signal)
        {
            try
            {
                return kill(-processGroupId, signal) == 0;
            }
            catch
            {
                return false;
            }
        }

        [DllImport("libc", SetLastError = true)]
        private static extern int setpgid(int pid, int pgid);

        [DllImport("libc", SetLastError = true)]
        private static extern int kill(int pid, int sig);
    }
}
