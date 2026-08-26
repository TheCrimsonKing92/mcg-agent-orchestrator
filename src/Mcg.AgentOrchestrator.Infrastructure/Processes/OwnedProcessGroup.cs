using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
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

    private OwnedProcessGroup(bool allowBreakaway)
    {
        if (OperatingSystem.IsWindows())
        {
            _jobHandle = WindowsJob.CreateKillOnCloseJob(allowBreakaway);
        }
    }

    public IReadOnlyList<int> ProcessIds => _processIds;

    public static OwnedProcessGroup Create() => new(allowBreakaway: true);

    internal static OwnedProcessGroup CreateContained() => new(allowBreakaway: false);

    public static OwnedProcessGroup Attach(Process process)
    {
        var group = Create();
        try
        {
            group.Add(process);
            return group;
        }
        catch
        {
            group.Dispose();
            throw;
        }
    }

    public static SuspendedProcessStart StartSuspended(ProcessStartInfo startInfo)
        => StartSuspendedCore(startInfo, null, null, contained: false);

    internal static SuspendedProcessStart StartSuspendedContained(ProcessStartInfo startInfo)
        => StartSuspendedCore(startInfo, null, null, contained: true);

    internal static SuspendedProcessStart StartSuspendedWithFileCapture(
        ProcessStartInfo startInfo,
        string stdoutPath,
        string stderrPath)
        => StartSuspendedCore(
            startInfo,
            Path.GetFullPath(stdoutPath),
            Path.GetFullPath(stderrPath),
            contained: false);

    internal static SuspendedProcessStart StartSuspendedContainedWithFileCapture(
        ProcessStartInfo startInfo,
        string stdoutPath,
        string stderrPath)
        => StartSuspendedCore(
            startInfo,
            Path.GetFullPath(stdoutPath),
            Path.GetFullPath(stderrPath),
            contained: true);

    private static SuspendedProcessStart StartSuspendedCore(
        ProcessStartInfo startInfo,
        string? stdoutPath,
        string? stderrPath,
        bool contained)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Suspended owned-process launch is Windows-only.");
        }

        if (startInfo.UseShellExecute ||
            startInfo.RedirectStandardInput ||
            startInfo.RedirectStandardOutput ||
            startInfo.RedirectStandardError)
        {
            throw new InvalidOperationException(
                "Suspended owned-process launch requires UseShellExecute=false and no redirected standard streams.");
        }

        var group = contained ? CreateContained() : Create();
        try
        {
            var processStart = WindowsJob.StartSuspendedInJob(
                group._jobHandle!,
                startInfo,
                stdoutPath,
                stderrPath);
            group._processIds.Add(processStart.Process.Id);
            return new SuspendedProcessStart(
                group,
                processStart.Process,
                processStart.ProcessHandle,
                processStart.InitialThread);
        }
        catch
        {
            group.Kill();
            throw;
        }
    }

    internal static string CaptureAssignmentFailureEvidenceForTests(Process candidate)
    {
        using var group = Create();
        return WindowsJob.CaptureAssignmentFailureEvidence(group._jobHandle!, candidate);
    }

    public void Add(Process process)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _processIds.Add(process.Id);

        if (OperatingSystem.IsWindows())
        {
            if (_jobHandle is not null && !WindowsJob.AssignProcessToJobObject(_jobHandle, process.Handle))
            {
                var nativeErrorCode = Marshal.GetLastWin32Error();
                var evidence = WindowsJob.CaptureAssignmentFailureEvidence(_jobHandle, process);
                throw new OwnedProcessAttachmentException(
                    nativeErrorCode,
                    "Failed to assign process to owned job object.",
                    evidence);
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

    public bool TryGetActiveProcessIds(out IReadOnlyList<int> processIds)
    {
        processIds = [];
        if (_disposed)
        {
            return false;
        }

        if (OperatingSystem.IsWindows())
        {
            return _jobHandle is not null &&
                !_jobHandle.IsClosed &&
                !_jobHandle.IsInvalid &&
                WindowsJob.TryGetActiveProcessIds(_jobHandle, out processIds);
        }

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            if (_processGroupId is { } processGroupId)
            {
                processIds = UnixProcessGroups.ListLiveProcessGroupMembers(processGroupId);
                return true;
            }

            processIds = _processIds
                .Where(IsProcessRunning)
                .ToArray();
            return true;
        }

        return false;
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

    internal static string QuoteCommandArgument(string value)
    {
        var quoted = new StringBuilder();
        quoted.Append('"');
        var backslashes = 0;
        foreach (var character in value)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }

            if (character == '"')
            {
                quoted.Append('\\', (backslashes * 2) + 1);
                quoted.Append('"');
                backslashes = 0;
                continue;
            }

            if (backslashes > 0)
            {
                quoted.Append('\\', backslashes);
                backslashes = 0;
            }

            quoted.Append(character);
        }

        if (backslashes > 0)
        {
            quoted.Append('\\', backslashes * 2);
        }

        quoted.Append('"');
        return quoted.ToString();
    }

    /// <summary>
    /// Releases this process group without terminating its members. On Windows the kill-on-close
    /// limit must be cleared before the final job handle is closed; otherwise a logical detach
    /// still kills the worker tree when the conductor exits.
    /// </summary>
    public bool TryDetachWithoutKill()
    {
        if (_disposed)
        {
            return true;
        }

        if (OperatingSystem.IsWindows() &&
            (_jobHandle is null ||
             _jobHandle.IsClosed ||
             _jobHandle.IsInvalid ||
             !WindowsJob.TryDisableKillOnClose(_jobHandle)))
        {
            return false;
        }

        _disposed = true;
        _jobHandle?.Dispose();
        _jobHandle = null;
        _processGroupId = null;
        return true;
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

    internal sealed class SuspendedProcessStart : IDisposable
    {
        private SafeFileHandle? _initialThread;
        private SafeFileHandle? _processHandle;
        private bool _transferred;

        internal SuspendedProcessStart(
            OwnedProcessGroup group,
            Process process,
            SafeFileHandle processHandle,
            SafeFileHandle initialThread)
        {
            Group = group;
            Process = process;
            _processHandle = processHandle;
            _initialThread = initialThread;
        }

        internal OwnedProcessGroup Group { get; }
        internal Process Process { get; }

        internal void Resume()
        {
            if (_initialThread is null || _initialThread.IsClosed || _initialThread.IsInvalid)
            {
                throw new InvalidOperationException("Owned process initial thread is unavailable for resume.");
            }

            if (WindowsJob.ResumeThread(_initialThread) == uint.MaxValue)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to resume owned process initial thread.");
            }
        }

        internal Process TransferOwnership()
        {
            _processHandle?.Dispose();
            _processHandle = null;
            _transferred = true;
            return Process;
        }

        internal OwnedProcessStartTransfer TransferOwnedProcess()
        {
            if (_processHandle is null)
            {
                throw new InvalidOperationException("Owned native process handle is unavailable for transfer.");
            }

            var transfer = new OwnedProcessStartTransfer(Process, _processHandle);
            _processHandle = null;
            _transferred = true;
            return transfer;
        }

        public void Dispose()
        {
            _initialThread?.Dispose();
            _initialThread = null;
            if (_transferred)
            {
                return;
            }

            Group.Kill();
            _processHandle?.Dispose();
            _processHandle = null;
            Process.Dispose();
        }
    }

    internal sealed record OwnedProcessStartTransfer(
        Process Process,
        SafeFileHandle ProcessHandle);

    private static class WindowsJob
    {
        private const int JobObjectBasicAccountingInformation = 1;
        private const int JobObjectBasicProcessIdList = 3;
        private const int JobObjectExtendedLimitInformation = 9;
        private const int JobObjectBasicUiRestrictions = 4;
        private const uint JobObjectLimitKillOnJobClose = 0x00002000;
        private const uint JobObjectLimitBreakawayOk = 0x00000800;
        private const uint DuplicateSameAccess = 0x00000002;
        private const int ErrorMoreData = 234;
        private const uint CreateSuspended = 0x00000004;
        private const uint CreateUnicodeEnvironment = 0x00000400;
        private const uint ExtendedStartupInfoPresent = 0x00080000;
        private const int ProcThreadAttributeJobList = 0x0002000D;
        private const int ProcThreadAttributeHandleList = 0x00020002;
        private const int StartfUseStdHandles = 0x00000100;
        private const uint GenericWrite = 0x40000000;
        private const uint FileShareRead = 0x00000001;
        private const uint FileShareDelete = 0x00000004;
        private const uint CreateAlways = 2;
        private const uint FileAttributeNormal = 0x00000080;

        public static WindowsSuspendedProcess StartSuspendedInJob(
            SafeFileHandle job,
            ProcessStartInfo startInfo,
            string? stdoutPath,
            string? stderrPath)
        {
            var captureToFiles = stdoutPath is not null && stderrPath is not null;
            using var stdoutHandle = captureToFiles ? CreateInheritedOutputFile(stdoutPath!) : null;
            using var stderrHandle = captureToFiles ? CreateInheritedOutputFile(stderrPath!) : null;
            var startupInfo = new STARTUPINFOEX
            {
                StartupInfo = new STARTUPINFO
                {
                    cb = Marshal.SizeOf<STARTUPINFOEX>(),
                    dwFlags = captureToFiles ? StartfUseStdHandles : 0,
                    hStdOutput = stdoutHandle?.DangerousGetHandle() ?? IntPtr.Zero,
                    hStdError = stderrHandle?.DangerousGetHandle() ?? IntPtr.Zero
                }
            };
            var inheritedHandles = captureToFiles
                ? new[] { stdoutHandle!.DangerousGetHandle(), stderrHandle!.DangerousGetHandle() }
                : [];
            using var attributes = WindowsJobAttributeList.Create(job, inheritedHandles);
            startupInfo.lpAttributeList = attributes.AttributeList;
            var commandLine = new StringBuilder(BuildCommandLine(startInfo));
            var environment = BuildEnvironmentBlock(startInfo.Environment);
            var workingDirectory = string.IsNullOrWhiteSpace(startInfo.WorkingDirectory)
                ? Environment.CurrentDirectory
                : startInfo.WorkingDirectory;

            using var suppression = ProcessTreeGuiSuppression.AcquireSuppressedChildSpawn();
            if (!CreateProcessW(
                    null,
                    commandLine,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    captureToFiles,
                    CreateSuspended | CreateUnicodeEnvironment | ExtendedStartupInfoPresent,
                    environment,
                    workingDirectory,
                    ref startupInfo,
                    out var processInformation))
            {
                var nativeErrorCode = Marshal.GetLastWin32Error();
                throw new OwnedProcessLaunchException(
                    nativeErrorCode,
                    "Failed to start suspended process in owned job object.",
                    CaptureLaunchFailureEvidence(job));
            }

            var nativeProcess = new SafeFileHandle(processInformation.hProcess, ownsHandle: true);
            var initialThread = new SafeFileHandle(processInformation.hThread, ownsHandle: true);
            try
            {
                var process = Process.GetProcessById(unchecked((int)processInformation.dwProcessId));
                return new WindowsSuspendedProcess(process, nativeProcess, initialThread);
            }
            catch
            {
                nativeProcess.Dispose();
                initialThread.Dispose();
                throw;
            }
        }

        private static SafeFileHandle CreateInheritedOutputFile(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
            var securityAttributes = new SECURITY_ATTRIBUTES
            {
                nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(),
                bInheritHandle = true
            };
            var handle = CreateFileW(
                path,
                GenericWrite,
                FileShareRead | FileShareDelete,
                ref securityAttributes,
                CreateAlways,
                FileAttributeNormal,
                IntPtr.Zero);
            if (handle.IsInvalid)
            {
                var nativeErrorCode = Marshal.GetLastWin32Error();
                handle.Dispose();
                throw new Win32Exception(nativeErrorCode, $"Failed to open owned-process capture file: {path}");
            }

            return handle;
        }

        public static string CaptureAssignmentFailureEvidence(SafeFileHandle ownedJob, Process candidate)
        {
            var parts = new List<string>
            {
                CaptureProbe(
                    "candidate_has_exited=unknown; candidate_exit_code",
                    () => CaptureCandidateExit(candidate)),
                CaptureProbe("owner_in_job", () =>
                {
                using var owner = Process.GetCurrentProcess();
                    return CaptureMembership("owner_in_job", owner.Handle, IntPtr.Zero);
                }),
                CaptureProbe(
                    "candidate_in_job",
                    () => CaptureMembership("candidate_in_job", candidate.Handle, IntPtr.Zero)),
                CaptureProbe("candidate_in_owned_job", () =>
                {
                var addedRef = false;
                try
                {
                    ownedJob.DangerousAddRef(ref addedRef);
                        return CaptureMembership(
                        "candidate_in_owned_job",
                        candidate.Handle,
                            ownedJob.DangerousGetHandle());
                }
                finally
                {
                    if (addedRef)
                    {
                        ownedJob.DangerousRelease();
                    }
                }
                }),
                CaptureProbe(
                    "owner_job_limit_flags",
                    () => CaptureJobFlags("owner_job_limit_flags", IntPtr.Zero, JobObjectExtendedLimitInformation)),
                CaptureProbe(
                    "owner_job_ui_restrictions",
                    () => CaptureJobFlags("owner_job_ui_restrictions", IntPtr.Zero, JobObjectBasicUiRestrictions)),
                CaptureProbe("owned_job_limit_flags", () => CaptureOwnedJobFlags(ownedJob)),
                CaptureProbe(
                    "owned_job_ui_restrictions",
                    () => CaptureOwnedJobFlags(
                        ownedJob,
                        "owned_job_ui_restrictions",
                        JobObjectBasicUiRestrictions))
            };

            return string.Join("; ", parts);
        }

        private static string CaptureLaunchFailureEvidence(SafeFileHandle ownedJob)
        {
            var parts = new List<string>
            {
                CaptureProbe("owner_in_job", () =>
                {
                    using var owner = Process.GetCurrentProcess();
                    return CaptureMembership("owner_in_job", owner.Handle, IntPtr.Zero);
                }),
                CaptureProbe(
                    "owner_job_limit_flags",
                    () => CaptureJobFlags("owner_job_limit_flags", IntPtr.Zero, JobObjectExtendedLimitInformation)),
                CaptureProbe(
                    "owner_job_ui_restrictions",
                    () => CaptureJobFlags("owner_job_ui_restrictions", IntPtr.Zero, JobObjectBasicUiRestrictions)),
                CaptureProbe(
                    "owned_job_limit_flags",
                    () => CaptureOwnedJobFlags(
                        ownedJob,
                        "owned_job_limit_flags",
                        JobObjectExtendedLimitInformation)),
                CaptureProbe(
                    "owned_job_ui_restrictions",
                    () => CaptureOwnedJobFlags(
                        ownedJob,
                        "owned_job_ui_restrictions",
                        JobObjectBasicUiRestrictions))
            };

            return string.Join("; ", parts);
        }

        private static string CaptureProbe(string name, Func<string> probe)
        {
            try
            {
                return probe();
            }
            catch (Exception ex)
            {
                return $"{name}=unknown(probe_exception={ex.GetType().Name}:{Sanitize(ex.Message)})";
            }
        }

        private static string CaptureCandidateExit(Process candidate)
        {
            try
            {
                if (!candidate.HasExited)
                {
                    return "candidate_has_exited=false; candidate_exit_code=not-applicable";
                }

                return $"candidate_has_exited=true; candidate_exit_code={candidate.ExitCode}";
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
            {
                return $"candidate_has_exited=unknown; candidate_exit_code=unknown({ex.GetType().Name})";
            }
        }

        private static string CaptureMembership(string name, IntPtr process, IntPtr job)
        {
            if (IsProcessInJob(process, job, out var result))
            {
                return $"{name}={result.ToString().ToLowerInvariant()}";
            }

            var error = Marshal.GetLastWin32Error();
            return $"{name}=unknown(native_error_code={error})";
        }

        private static string CaptureJobFlags(string name, IntPtr job, int infoClass)
        {
            if (infoClass == JobObjectExtendedLimitInformation)
            {
                return TryQuery(job, infoClass, out JOBOBJECT_EXTENDED_LIMIT_INFORMATION extended, out var error)
                    ? $"{name}=0x{extended.BasicLimitInformation.LimitFlags:x8}"
                    : $"{name}=unknown(native_error_code={error})";
            }

            return TryQuery(job, infoClass, out JOBOBJECT_BASIC_UI_RESTRICTIONS ui, out var uiError)
                ? $"{name}=0x{ui.UIRestrictionsClass:x8}"
                : $"{name}=unknown(native_error_code={uiError})";
        }

        private static string CaptureOwnedJobFlags(SafeFileHandle job) =>
            CaptureOwnedJobFlags(job, "owned_job_limit_flags", JobObjectExtendedLimitInformation);

        private static string CaptureOwnedJobFlags(SafeFileHandle job, string name, int infoClass)
        {
            var addedRef = false;
            try
            {
                job.DangerousAddRef(ref addedRef);
                return CaptureJobFlags(name, job.DangerousGetHandle(), infoClass);
            }
            catch (ObjectDisposedException)
            {
                return $"{name}=unknown(ObjectDisposedException)";
            }
            finally
            {
                if (addedRef)
                {
                    job.DangerousRelease();
                }
            }
        }

        private static bool TryQuery<T>(IntPtr job, int infoClass, out T value, out int error)
            where T : struct
        {
            value = default;
            error = 0;
            var length = Marshal.SizeOf<T>();
            var buffer = Marshal.AllocHGlobal(length);
            try
            {
                if (!QueryInformationJobObject(job, infoClass, buffer, (uint)length, IntPtr.Zero))
                {
                    error = Marshal.GetLastWin32Error();
                    return false;
                }

                value = Marshal.PtrToStructure<T>(buffer);
                return true;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        private static string BuildCommandLine(ProcessStartInfo startInfo)
        {
            var commandLine = new StringBuilder(QuoteCommandArgument(startInfo.FileName));
            if (startInfo.ArgumentList.Count > 0)
            {
                foreach (var argument in startInfo.ArgumentList)
                {
                    commandLine.Append(' ');
                    commandLine.Append(QuoteCommandArgument(argument));
                }
            }
            else if (!string.IsNullOrWhiteSpace(startInfo.Arguments))
            {
                commandLine.Append(' ');
                commandLine.Append(startInfo.Arguments);
            }

            return commandLine.ToString();
        }

        private static string BuildEnvironmentBlock(IDictionary<string, string?> environment)
        {
            var builder = new StringBuilder();
            foreach (var pair in environment.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            {
                builder.Append(pair.Key);
                builder.Append('=');
                builder.Append(pair.Value);
                builder.Append('\0');
            }

            builder.Append('\0');
            return builder.ToString();
        }

        private static string Sanitize(string value) =>
            value.Replace('\r', ' ').Replace('\n', ' ').Trim();

        public static SafeFileHandle CreateKillOnCloseJob(bool allowBreakaway = true)
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
                    LimitFlags = JobObjectLimitKillOnJobClose |
                        (allowBreakaway ? JobObjectLimitBreakawayOk : 0)
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
        public static extern uint ResumeThread(SafeFileHandle thread);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool IsProcessInJob(
            IntPtr processHandle,
            IntPtr jobHandle,
            [MarshalAs(UnmanagedType.Bool)] out bool result);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CreateProcessW(
            string? lpApplicationName,
            StringBuilder lpCommandLine,
            IntPtr lpProcessAttributes,
            IntPtr lpThreadAttributes,
            bool bInheritHandles,
            uint dwCreationFlags,
            string lpEnvironment,
            string lpCurrentDirectory,
            ref STARTUPINFOEX lpStartupInfo,
            out PROCESS_INFORMATION lpProcessInformation);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFileW(
            string lpFileName,
            uint dwDesiredAccess,
            uint dwShareMode,
            ref SECURITY_ATTRIBUTES lpSecurityAttributes,
            uint dwCreationDisposition,
            uint dwFlagsAndAttributes,
            IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool InitializeProcThreadAttributeList(
            IntPtr lpAttributeList,
            int dwAttributeCount,
            int dwFlags,
            ref IntPtr lpSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool UpdateProcThreadAttribute(
            IntPtr lpAttributeList,
            uint dwFlags,
            IntPtr attribute,
            IntPtr lpValue,
            IntPtr cbSize,
            IntPtr lpPreviousValue,
            IntPtr lpReturnSize);

        [DllImport("kernel32.dll")]
        private static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

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

        public static bool TryDisableKillOnClose(SafeFileHandle job)
        {
            if (!TryQuery(job, JobObjectExtendedLimitInformation, out JOBOBJECT_EXTENDED_LIMIT_INFORMATION info))
            {
                return false;
            }

            if ((info.BasicLimitInformation.LimitFlags & JobObjectLimitKillOnJobClose) == 0)
            {
                return true;
            }

            info.BasicLimitInformation.LimitFlags &= ~JobObjectLimitKillOnJobClose;
            var length = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
            var buffer = Marshal.AllocHGlobal(length);
            try
            {
                Marshal.StructureToPtr(info, buffer, false);
                return SetInformationJobObject(job, JobObjectExtendedLimitInformation, buffer, (uint)length);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

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

        public static bool TryGetActiveProcessIds(SafeFileHandle job, out IReadOnlyList<int> processIds)
        {
            processIds = [];
            if (job.IsClosed || job.IsInvalid)
            {
                return false;
            }

            var capacity = 16;
            while (capacity <= 4096)
            {
                var result = TryQueryProcessIdList(job, capacity, out processIds, out var error);
                if (result)
                {
                    return true;
                }

                if (error != ErrorMoreData)
                {
                    return false;
                }

                capacity *= 2;
            }

            return false;
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

        private static bool TryQueryProcessIdList(
            SafeFileHandle job,
            int capacity,
            out IReadOnlyList<int> processIds,
            out int error)
        {
            processIds = [];
            error = 0;
            var addedRef = false;
            var length = 8 + (capacity * IntPtr.Size);
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

                if (!QueryInformationJobObject(job.DangerousGetHandle(), JobObjectBasicProcessIdList, buffer, (uint)length, IntPtr.Zero))
                {
                    error = Marshal.GetLastWin32Error();
                    return false;
                }

                var numberOfAssignedProcesses = Marshal.ReadInt32(buffer);
                var numberOfProcessIdsInList = Marshal.ReadInt32(buffer, 4);
                var count = Math.Min(Math.Min(numberOfAssignedProcesses, numberOfProcessIdsInList), capacity);
                var ids = new List<int>(count);
                for (var i = 0; i < count; i++)
                {
                    var rawPid = Marshal.ReadIntPtr(buffer, 8 + (i * IntPtr.Size)).ToInt64();
                    if (rawPid > 0 && rawPid <= int.MaxValue)
                    {
                        ids.Add((int)rawPid);
                    }
                }

                processIds = ids.Distinct().ToArray();
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

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct STARTUPINFO
        {
            public int cb;
            public string? lpReserved;
            public string? lpDesktop;
            public string? lpTitle;
            public int dwX;
            public int dwY;
            public int dwXSize;
            public int dwYSize;
            public int dwXCountChars;
            public int dwYCountChars;
            public int dwFillAttribute;
            public int dwFlags;
            public short wShowWindow;
            public short cbReserved2;
            public IntPtr lpReserved2;
            public IntPtr hStdInput;
            public IntPtr hStdOutput;
            public IntPtr hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct STARTUPINFOEX
        {
            public STARTUPINFO StartupInfo;
            public IntPtr lpAttributeList;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SECURITY_ATTRIBUTES
        {
            public int nLength;
            public IntPtr lpSecurityDescriptor;
            [MarshalAs(UnmanagedType.Bool)]
            public bool bInheritHandle;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_INFORMATION
        {
            public IntPtr hProcess;
            public IntPtr hThread;
            public uint dwProcessId;
            public uint dwThreadId;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_BASIC_UI_RESTRICTIONS
        {
            public uint UIRestrictionsClass;
        }

        public sealed record WindowsSuspendedProcess(
            Process Process,
            SafeFileHandle ProcessHandle,
            SafeFileHandle InitialThread);

        private sealed class WindowsJobAttributeList : IDisposable
        {
            private readonly IntPtr _jobHandleValue;
            private readonly IntPtr _inheritedHandleValues;
            private readonly bool _initialized;

            private WindowsJobAttributeList(
                IntPtr attributeList,
                IntPtr jobHandleValue,
                IntPtr inheritedHandleValues,
                bool initialized)
            {
                AttributeList = attributeList;
                _jobHandleValue = jobHandleValue;
                _inheritedHandleValues = inheritedHandleValues;
                _initialized = initialized;
            }

            public IntPtr AttributeList { get; }

            public static WindowsJobAttributeList Create(SafeFileHandle job, IReadOnlyList<IntPtr> inheritedHandles)
            {
                var attributeCount = inheritedHandles.Count == 0 ? 1 : 2;
                var size = IntPtr.Zero;
                _ = InitializeProcThreadAttributeList(IntPtr.Zero, attributeCount, 0, ref size);
                if (size == IntPtr.Zero)
                {
                    throw new OwnedProcessLaunchException(
                        Marshal.GetLastWin32Error(),
                        "Failed to size owned-process attribute list.",
                        CaptureLaunchFailureEvidence(job));
                }

                var attributeList = Marshal.AllocHGlobal(size);
                var jobHandleValue = Marshal.AllocHGlobal(IntPtr.Size);
                var inheritedHandleValues = inheritedHandles.Count == 0
                    ? IntPtr.Zero
                    : Marshal.AllocHGlobal(IntPtr.Size * inheritedHandles.Count);
                var initialized = false;
                var addedRef = false;
                try
                {
                    if (!InitializeProcThreadAttributeList(attributeList, attributeCount, 0, ref size))
                    {
                        throw new OwnedProcessLaunchException(
                            Marshal.GetLastWin32Error(),
                            "Failed to initialize owned-process attribute list.",
                            CaptureLaunchFailureEvidence(job));
                    }

                    initialized = true;
                    job.DangerousAddRef(ref addedRef);
                    Marshal.WriteIntPtr(jobHandleValue, job.DangerousGetHandle());
                    if (!UpdateProcThreadAttribute(
                            attributeList,
                            0,
                            new IntPtr(ProcThreadAttributeJobList),
                            jobHandleValue,
                            new IntPtr(IntPtr.Size),
                            IntPtr.Zero,
                            IntPtr.Zero))
                    {
                        throw new OwnedProcessLaunchException(
                            Marshal.GetLastWin32Error(),
                            "Failed to assign owned job during process creation.",
                            CaptureLaunchFailureEvidence(job));
                    }

                    if (inheritedHandles.Count > 0)
                    {
                        Marshal.Copy(inheritedHandles.ToArray(), 0, inheritedHandleValues, inheritedHandles.Count);
                        if (!UpdateProcThreadAttribute(
                                attributeList,
                                0,
                                new IntPtr(ProcThreadAttributeHandleList),
                                inheritedHandleValues,
                                new IntPtr(IntPtr.Size * inheritedHandles.Count),
                                IntPtr.Zero,
                                IntPtr.Zero))
                        {
                            throw new OwnedProcessLaunchException(
                                Marshal.GetLastWin32Error(),
                                "Failed to restrict inherited handles during owned-process creation.",
                                CaptureLaunchFailureEvidence(job));
                        }
                    }

                    return new WindowsJobAttributeList(
                        attributeList,
                        jobHandleValue,
                        inheritedHandleValues,
                        initialized);
                }
                catch
                {
                    if (initialized)
                    {
                        DeleteProcThreadAttributeList(attributeList);
                    }

                    Marshal.FreeHGlobal(inheritedHandleValues);
                    Marshal.FreeHGlobal(jobHandleValue);
                    Marshal.FreeHGlobal(attributeList);
                    throw;
                }
                finally
                {
                    if (addedRef)
                    {
                        job.DangerousRelease();
                    }
                }
            }

            public void Dispose()
            {
                if (_initialized)
                {
                    DeleteProcThreadAttributeList(AttributeList);
                }

                Marshal.FreeHGlobal(_inheritedHandleValues);
                Marshal.FreeHGlobal(_jobHandleValue);
                Marshal.FreeHGlobal(AttributeList);
            }
        }

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

    private static bool IsProcessRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch
        {
            return false;
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

        public static IReadOnlyList<int> ListLiveProcessGroupMembers(int processGroupId)
        {
            if (processGroupId <= 0)
            {
                return [];
            }

            var members = new List<int>();
            foreach (var process in Process.GetProcesses())
            {
                using (process)
                {
                    int processId;
                    try
                    {
                        processId = process.Id;
                    }
                    catch
                    {
                        continue;
                    }

                    if (!IsProcessRunning(processId) ||
                        !TryGetProcessGroupId(processId, out var candidateProcessGroupId) ||
                        candidateProcessGroupId != processGroupId)
                    {
                        continue;
                    }

                    members.Add(processId);
                }
            }

            return members
                .Distinct()
                .OrderBy(processId => processId)
                .ToArray();
        }

        private static bool TryGetProcessGroupId(int processId, out int processGroupId)
        {
            processGroupId = 0;
            try
            {
                var result = getpgid(processId);
                if (result <= 0)
                {
                    return false;
                }

                processGroupId = result;
                return true;
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

        [DllImport("libc", SetLastError = true)]
        private static extern int getpgid(int pid);
    }
}

internal sealed class OwnedProcessAttachmentException : Win32Exception
{
    public OwnedProcessAttachmentException(int nativeErrorCode, string operationMessage, string jobEvidence)
        : base(nativeErrorCode, operationMessage)
    {
        OperationMessage = operationMessage;
        JobEvidence = jobEvidence;
    }

    public string OperationMessage { get; }
    public string JobEvidence { get; }
}

internal sealed class OwnedProcessLaunchException : Win32Exception
{
    public OwnedProcessLaunchException(int nativeErrorCode, string operationMessage, string jobEvidence)
        : base(nativeErrorCode, operationMessage)
    {
        OperationMessage = operationMessage;
        JobEvidence = jobEvidence;
    }

    public string OperationMessage { get; }
    public string JobEvidence { get; }
}
