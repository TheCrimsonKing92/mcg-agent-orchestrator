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
        private const int JobObjectExtendedLimitInformation = 9;
        private const uint JobObjectLimitKillOnJobClose = 0x00002000;

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
        public static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);

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
