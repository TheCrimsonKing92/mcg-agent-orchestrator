using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Collections.Concurrent;
using Mcg.AgentOrchestrator.Core;
using Microsoft.Win32.SafeHandles;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed record WorkerProcessJobAccounting(
    long CpuMilliseconds,
    long PeakMemoryBytes,
    long IoBytes,
    string AccountingSource = "live")
{
    public static WorkerProcessJobAccounting Empty { get; } = new(0, 0, 0);
}

internal sealed record OwnedChildStartMetadata(
    string FileName,
    string Arguments,
    IReadOnlyList<string> ArgumentList,
    string WorkingDirectory);

internal sealed record WorkerProcessJobReleaseEvidence(
    bool RegistrationFound,
    bool TerminationRequested,
    bool JobExitConfirmed);

internal sealed class RegisteredJob
{
    internal RegisteredJob(
        OwnedProcessGroup Group,
        SafeFileHandle? DuplicateAccountingHandle,
        WorkerProcessJobAccounting? RegistrationSnapshot,
        SpawnProcessIdentity? Identity = null,
        int IdentityReadAttempts = 0,
        bool RequiresDurableDetach = false,
        object? LifecycleAuthority = null)
    {
        this.Group = Group;
        this.DuplicateAccountingHandle = DuplicateAccountingHandle;
        this.RegistrationSnapshot = RegistrationSnapshot;
        this.Identity = Identity;
        this.IdentityReadAttempts = IdentityReadAttempts;
        this.RequiresDurableDetach = RequiresDurableDetach;
        this.LifecycleAuthority = LifecycleAuthority;
    }

    internal OwnedProcessGroup Group { get; }
    internal SafeFileHandle? DuplicateAccountingHandle { get; }
    internal WorkerProcessJobAccounting? RegistrationSnapshot { get; }
    internal SpawnProcessIdentity? Identity { get; }
    internal int IdentityReadAttempts { get; }
    internal bool RequiresDurableDetach { get; }
    internal object? LifecycleAuthority { get; }
    internal bool RequiresOwnedRelease => LifecycleAuthority is not null;

    internal bool IsAuthorizedBy(object authority) =>
        ReferenceEquals(LifecycleAuthority, authority);
}

internal sealed class RegisteredOwnedProcess : IDisposable
{
    private const uint TerminatedExitCode = 1;
    private readonly int _processId;
    private readonly RegisteredJob _registration;
    private readonly object _lifecycleAuthority;
    private Process? _process;
    private SafeFileHandle? _processHandle;
    private StreamWriter? _standardInput;
    private StreamReader? _standardOutput;
    private StreamReader? _standardError;
    private int _registrationReleased;

    internal RegisteredOwnedProcess(
        Process process,
        SafeFileHandle? processHandle,
        ProcessStartInfo startInfo,
        RegisteredJob registration,
        object lifecycleAuthority)
    {
        if (!registration.IsAuthorizedBy(lifecycleAuthority))
        {
            throw new InvalidOperationException(
                "worker-process-owned-lifecycle-authority-mismatch: stage=owned-child-transfer");
        }

        _process = process;
        _processId = process.Id;
        _processHandle = processHandle;
        _registration = registration;
        _lifecycleAuthority = lifecycleAuthority;
        StartMetadata = new OwnedChildStartMetadata(
            startInfo.FileName,
            startInfo.Arguments,
            startInfo.ArgumentList.ToArray(),
            startInfo.WorkingDirectory);
    }

    internal RegisteredOwnedProcess(
        OwnedProcessGroup.RedirectedOwnedProcessStart transfer,
        ProcessStartInfo startInfo,
        RegisteredJob registration,
        object lifecycleAuthority)
        : this(
            transfer.Process,
            transfer.ProcessHandle,
            startInfo,
            registration,
            lifecycleAuthority)
    {
        _standardInput = transfer.StandardInput;
        _standardOutput = transfer.StandardOutput;
        _standardError = transfer.StandardError;
    }

    internal int Id => Process.Id;
    internal StreamReader StandardOutput => _standardOutput ?? Process.StandardOutput;
    internal StreamReader StandardError => _standardError ?? Process.StandardError;
    internal OwnedChildStartMetadata StartMetadata { get; }
    internal SpawnProcessIdentity? Identity => _registration.Identity;
    internal int IdentityReadAttempts => _registration.IdentityReadAttempts;
    internal SpawnProcessIdentity? TryReadDirectChildIdentity() =>
        WorkerProcessJobs.TryReadDirectChildIdentity(_processId, _registration.Group);
    internal bool HasOpenNativeHandle =>
        _processHandle is { IsClosed: false, IsInvalid: false };
    internal bool IsDisposed => _process is null;
    internal bool OwnsRegisteredJob =>
        Volatile.Read(ref _registrationReleased) == 0 &&
        WorkerProcessJobs.HasRegisteredJob(_processId, _registration);

    internal int ExitCode
    {
        get
        {
            if (_processHandle is null)
            {
                return Process.ExitCode;
            }

            if (!GetExitCodeProcess(_processHandle, out var exitCode))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to read owned child exit code.");
            }

            return unchecked((int)exitCode);
        }
    }

    internal void CompleteInput()
    {
        if (_standardInput is not null)
        {
            _standardInput.Dispose();
            _standardInput = null;
            return;
        }

        if (Process.StartInfo.RedirectStandardInput)
        {
            Process.StandardInput.Dispose();
        }
    }

    internal async Task WaitForExitAsync(CancellationToken cancellationToken)
    {
        if (_processHandle is null)
        {
            await Process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        using var waitHandle = new NativeProcessWaitHandle(_processHandle);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registeredWait = ThreadPool.RegisterWaitForSingleObject(
            waitHandle,
            static (state, _) => ((TaskCompletionSource)state!).TrySetResult(),
            completion,
            Timeout.InfiniteTimeSpan,
            executeOnlyOnce: true);
        using var cancellationRegistration = cancellationToken.Register(
            static state =>
            {
                var (source, token) = ((TaskCompletionSource Source, CancellationToken Token))state!;
                source.TrySetCanceled(token);
            },
            (completion, cancellationToken));
        try
        {
            await completion.Task.ConfigureAwait(false);
        }
        finally
        {
            registeredWait.Unregister(null);
        }
    }

    internal void Kill(bool entireProcessTree)
    {
        if (Volatile.Read(ref _registrationReleased) == 0)
        {
            _registration.Group.Kill();
            return;
        }

        if (_processHandle is not null)
        {
            if (!TerminateProcess(_processHandle, TerminatedExitCode))
            {
                var nativeError = Marshal.GetLastWin32Error();
                if (nativeError != 5)
                {
                    throw new Win32Exception(nativeError, "Failed to terminate owned child process.");
                }
            }

            return;
        }

        Process.Kill(entireProcessTree);
    }

    internal bool Release(out WorkerProcessJobAccounting? accounting)
    {
        accounting = null;
        if (Interlocked.Exchange(ref _registrationReleased, 1) != 0)
        {
            return false;
        }

        WorkerProcessJobs.ReleaseOwned(
            _processId,
            _registration,
            _lifecycleAuthority,
            out accounting);
        return true;
    }

    public void Dispose()
    {
        try
        {
            _ = Release(out _);
        }
        finally
        {
            _standardInput?.Dispose();
            _standardInput = null;
            _standardOutput?.Dispose();
            _standardOutput = null;
            _standardError?.Dispose();
            _standardError = null;
            _processHandle?.Dispose();
            _processHandle = null;
            _process?.Dispose();
            _process = null;
        }
    }

    private Process Process => _process ?? throw new ObjectDisposedException(nameof(RegisteredOwnedProcess));

    private sealed class NativeProcessWaitHandle : WaitHandle
    {
        internal NativeProcessWaitHandle(SafeFileHandle processHandle)
        {
            SafeWaitHandle = new SafeWaitHandle(processHandle.DangerousGetHandle(), ownsHandle: false);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(SafeFileHandle processHandle, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(SafeFileHandle processHandle, uint exitCode);
}

public static class WorkerProcessJobs
{
    private const string ProtectedPidVariable = "MCG_ORCHESTRATOR_PROTECTED_PID";
    private const int IdentityReadAttempts = 10;
    private const int IdentityReadDelayMilliseconds = 25;
    private static readonly TimeSpan StartupReapClaimLease = TimeSpan.FromMinutes(1);
    private static readonly ConcurrentDictionary<int, RegisteredJob> Jobs = new();
    private static SpawnRegistry? Registry;
    private static string? RegistryDbPath;
    private static readonly Func<Process, SpawnProcessIdentityReadResult> ProductionRegistrationIdentityReader =
        BuildRegistrationIdentityReader(ReadIdentityOnce, identityReadDelay: null);

    internal static Func<int, bool> TryKillPidTree { get; set; } = DefaultTryKillPidTree;

    public static void ConfigureRegistry(string dbPath)
    {
        var registry = new SpawnRegistry(dbPath);
        RegistryDbPath = dbPath;
        Registry = registry;
    }

    public static int SweepStartupOrphans()
    {
        var registry = Registry;
        if (registry is null)
        {
            return 0;
        }

        var reaped = 0;
        var sweepStartedAt = DateTimeOffset.UtcNow;
        var sweeper = BuildSweeperEvidence(sweepStartedAt);
        foreach (var entry in registry.ListActive())
        {
            if (entry.Lifecycle != SpawnRegistryLifecycle.Owned)
            {
                var detachedVictimStatus = SpawnProcessIdentityReader.EvaluateTrackedProcess(
                    entry,
                    out var detachedProcess,
                    out var detachedVictimEvidence);
                detachedProcess?.Dispose();
                if (detachedVictimStatus == SpawnTrackedProcessStatus.DeadOrRecycled)
                {
                    _ = registry.TryMarkReleasedEntry(
                        entry.Id,
                        entry.LastDiagnostic,
                        BuildSweepDiagnostic(
                            "detached-worker-exited",
                            entry,
                            SpawnOwnerLiveness.Unknown,
                            "detached-lifecycle",
                            sweeper,
                            detachedVictimEvidence));
                }
                else
                {
                    RecordRetentionDiagnosticIfChanged(
                        registry,
                        entry,
                        "retain-gracefully-detached",
                        SpawnOwnerLiveness.Unknown,
                        "detached-lifecycle",
                        sweeper,
                        detachedVictimEvidence);
                }

                continue;
            }

            var ownerLiveness = SpawnProcessIdentityReader.EvaluateOwner(entry, out var ownerEvidence);
            if (ownerLiveness != SpawnOwnerLiveness.DeadOrRecycled)
            {
                RecordRetentionDiagnosticIfChanged(registry, entry, ownerLiveness, ownerEvidence, sweeper);
                continue;
            }

            var victimStatus = SpawnProcessIdentityReader.EvaluateTrackedProcess(entry, out var process, out var victimEvidence);
            if (victimStatus == SpawnTrackedProcessStatus.Unknown)
            {
                RecordRetentionDiagnosticIfChanged(
                    registry,
                    entry,
                    "retain-unknown-victim",
                    ownerLiveness,
                    ownerEvidence,
                    sweeper,
                    victimEvidence);
                continue;
            }

            if (victimStatus == SpawnTrackedProcessStatus.DeadOrRecycled)
            {
                _ = registry.TryMarkReleasedEntry(
                    entry.Id,
                    entry.LastDiagnostic,
                    BuildSweepDiagnostic("already-dead-or-recycled", entry, ownerLiveness, ownerEvidence, sweeper, victimEvidence));
                continue;
            }

            using (process)
            {
                if (IsProtectedProcessOrAncestor(entry.ProcessId) || IsProtectedDescendant(entry.ProcessId))
                {
                    registry.RecordDiagnostic(
                        entry.Id,
                        BuildSweepDiagnostic("refused-protected", entry, ownerLiveness, ownerEvidence, sweeper));
                    continue;
                }

                // Revalidate at the destructive boundary. If the evidence changes or becomes unreadable,
                // retain the worker; only positive dead/recycled-owner evidence authorizes a kill.
                ownerLiveness = SpawnProcessIdentityReader.EvaluateOwner(entry, out ownerEvidence);
                if (ownerLiveness != SpawnOwnerLiveness.DeadOrRecycled)
                {
                    RecordRetentionDiagnosticIfChanged(registry, entry, ownerLiveness, ownerEvidence, sweeper);
                    continue;
                }

                // Claim the exact registry entry and durably record authorization before the destructive
                // action. The compare-and-set prevents concurrent CLI startup sweeps from acting on the
                // same stale snapshot; a later sweep can retry a claim abandoned by a crashed sweeper.
                if (HasActiveStartupReapClaim(entry.LastDiagnostic, sweepStartedAt))
                {
                    continue;
                }
                if (!registry.TryRecordDiagnostic(
                        entry.Id,
                        entry.LastDiagnostic,
                        BuildSweepDiagnostic("startup-reap-authorized", entry, ownerLiveness, ownerEvidence, sweeper, victimEvidence)))
                {
                    continue;
                }

                if (TryKillMatchedProcess(process))
                {
                    registry.MarkReleasedEntry(
                        entry.Id,
                        BuildSweepDiagnostic("startup-reaped", entry, ownerLiveness, ownerEvidence, sweeper, victimEvidence));
                    reaped++;
                }
                else
                {
                    registry.RecordDiagnostic(
                        entry.Id,
                        BuildSweepDiagnostic("startup-reap-failed", entry, ownerLiveness, ownerEvidence, sweeper, victimEvidence));
                }
            }
        }

        return reaped;
    }

    private static void RecordRetentionDiagnosticIfChanged(
        SpawnRegistry registry,
        SpawnRegistryEntry entry,
        SpawnOwnerLiveness ownerLiveness,
        string ownerEvidence,
        string sweeper) =>
        RecordRetentionDiagnosticIfChanged(
            registry,
            entry,
            ownerLiveness == SpawnOwnerLiveness.Live ? "retain-live-owner" : "retain-unknown-owner",
            ownerLiveness,
            ownerEvidence,
            sweeper,
            victimEvidence: null);

    private static void RecordRetentionDiagnosticIfChanged(
        SpawnRegistry registry,
        SpawnRegistryEntry entry,
        string action,
        SpawnOwnerLiveness ownerLiveness,
        string ownerEvidence,
        string sweeper,
        string? victimEvidence)
    {
        var stableFingerprint = BuildSweepDiagnostic(
            action,
            entry,
            ownerLiveness,
            ownerEvidence,
            sweeper: string.Empty,
            victimEvidence: victimEvidence).TrimEnd();
        if (entry.LastDiagnostic is null ||
            !entry.LastDiagnostic.StartsWith(stableFingerprint, StringComparison.Ordinal))
        {
            registry.RecordDiagnostic(
                entry.Id,
                BuildSweepDiagnostic(
                    action,
                    entry,
                    ownerLiveness,
                    ownerEvidence,
                    sweeper,
                    victimEvidence));
        }
    }

    private static string BuildSweepDiagnostic(
        string action,
        SpawnRegistryEntry entry,
        SpawnOwnerLiveness ownerLiveness,
        string ownerEvidence,
        string sweeper,
        string? victimEvidence = null) =>
        $"spawn_registry: {action} victim_pid={entry.ProcessId} victim_started_at={entry.ProcessStartedAt:O} " +
        $"victim_image={entry.ImagePath} lifecycle={entry.Lifecycle} owner={entry.OwnerId} owner_pid={entry.OwnerProcessId?.ToString() ?? "unknown"} " +
        $"owner_started_at={entry.OwnerProcessStartedAt?.ToString("O") ?? "unknown"} " +
        $"owner_liveness={ownerLiveness} owner_evidence={SanitizeDiagnostic(ownerEvidence)} " +
        $"victim_evidence={SanitizeDiagnostic(victimEvidence ?? "not-evaluated")} {sweeper}";

    private static bool HasActiveStartupReapClaim(string? diagnostic, DateTimeOffset observedAt)
    {
        const string prefix = "spawn_registry: startup-reap-authorized ";
        const string timestampMarker = "sweeper_claimed_at=";
        if (diagnostic is null || !diagnostic.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var markerIndex = diagnostic.IndexOf(timestampMarker, StringComparison.Ordinal);
        if (markerIndex < 0)
        {
            return false;
        }

        var valueStart = markerIndex + timestampMarker.Length;
        var valueEnd = diagnostic.IndexOf(' ', valueStart);
        var value = valueEnd < 0
            ? diagnostic[valueStart..]
            : diagnostic[valueStart..valueEnd];
        return DateTimeOffset.TryParseExact(
                value,
                "O",
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var claimedAt) &&
            observedAt < claimedAt + StartupReapClaimLease;
    }

    private static string BuildSweeperEvidence(DateTimeOffset claimedAt)
    {
        var argv = SanitizeDiagnostic(string.Join(' ', Environment.GetCommandLineArgs()));
        if (argv.Length > 1024)
        {
            argv = argv[..1024] + "...";
        }

        return $"sweeper_claimed_at={claimedAt:O} sweeper_pid={Environment.ProcessId} sweeper_argv={argv}";
    }

    private static string SanitizeDiagnostic(string value) =>
        value.Replace('\r', ' ').Replace('\n', ' ').Trim();

    private static bool TryKillMatchedProcess(Process? process)
    {
        if (process is null)
        {
            return false;
        }

        try
        {
            if (process.HasExited)
            {
                return false;
            }

            process.Kill(entireProcessTree: true);
            return process.WaitForExit(5000) && process.HasExited;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            return false;
        }
    }

    public static bool TryRegister(Process process, string? ownerId = null)
    {
        return TryRegister(process, ownerId, out _);
    }

    public static bool TryRegister(Process process, string? ownerId, out string registrationFailure)
    {
        return TryRegisterCore(
            process,
            ownerId,
            ProductionRegistrationIdentityReader,
            ProductionRegistrationIdentityReader,
            out registrationFailure);
    }

    public static void RegisterOrThrow(Process process, string? ownerId = null)
    {
        if (!TryRegister(process, ownerId, out var registrationFailure))
        {
            throw new InvalidOperationException(registrationFailure);
        }
    }

    public static Process StartRegisteredOrThrow(ProcessStartInfo startInfo, string? ownerId = null)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        if (!OperatingSystem.IsWindows())
        {
            return StartAndRegisterNonWindows(
                startInfo,
                ownerId,
                ProcessTreeGuiSuppression.Start,
                RegisterOrThrow);
        }

        return StartRegisteredWindows(
            () => OwnedProcessGroup.StartSuspended(startInfo),
            ownerId);
    }

    internal static RegisteredOwnedProcess StartRegisteredOwnedOrThrow(
        ProcessStartInfo startInfo,
        string? ownerId = null,
        Func<Process, SpawnProcessIdentityReadResult>? registrationIdentityReader = null,
        bool containDescendants = false)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        var lifecycleAuthority = new object();
        if (!OperatingSystem.IsWindows())
        {
            var identityReader = registrationIdentityReader ?? ProductionRegistrationIdentityReader;
            var process = StartAndRegisterNonWindows(
                startInfo,
                ownerId,
                ProcessTreeGuiSuppression.Start,
                (candidate, candidateOwnerId) =>
                    RegisterOwnedOrThrow(candidate, candidateOwnerId, lifecycleAuthority, identityReader));
            return new RegisteredOwnedProcess(
                process,
                processHandle: null,
                startInfo,
                GetRegisteredJobOrThrow(process.Id, lifecycleAuthority),
                lifecycleAuthority);
        }

        return StartRegisteredOwnedWindows(
            () => containDescendants
                ? OwnedProcessGroup.StartSuspendedContained(startInfo)
                : OwnedProcessGroup.StartSuspended(startInfo),
            startInfo,
            lifecycleAuthority,
            registrationIdentityReader,
            ownerId);
    }

    internal static RegisteredOwnedProcess StartRegisteredOwnedRedirectedOrThrow(
        ProcessStartInfo startInfo,
        string? ownerId = null)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        if (!OperatingSystem.IsWindows())
        {
            return StartRegisteredOwnedOrThrow(startInfo, ownerId);
        }

        OwnedProcessGroup.SuspendedRedirectedProcessStart launch;
        try
        {
            launch = OwnedProcessGroup.StartSuspendedContainedRedirected(startInfo);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
        {
            throw new InvalidOperationException(
                $"worker-process-start-failed: stage=owned-process-group-launch; cleanup=owned-job-termination-requested; {BuildExceptionEvidence(ex)}",
                ex);
        }

        using (launch)
        {
            var lifecycleAuthority = new object();
            var victimIdentityReader = BuildSuspendedRegistrationIdentityReader(launch.ReadLifecycleIdentity);
            if (!TryRegisterCore(
                    launch.Process,
                    ownerId,
                    victimIdentityReader,
                    ProductionRegistrationIdentityReader,
                    out var registrationFailure,
                    launch.Group,
                    launch.Resume,
                    lifecycleAuthority: lifecycleAuthority))
            {
                throw new InvalidOperationException(registrationFailure);
            }

            var registration = GetRegisteredJobOrThrow(launch.Process.Id, lifecycleAuthority);
            return new RegisteredOwnedProcess(
                launch.TransferOwnership(),
                startInfo,
                registration,
                lifecycleAuthority);
        }
    }

    internal static RegisteredOwnedProcess AdoptRegisteredOwnedOrThrow(
        Process process,
        ProcessStartInfo startInfo,
        string? ownerId = null)
    {
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(startInfo);
        var lifecycleAuthority = new object();
        RegisterOwnedOrThrow(
            process,
            ownerId,
            lifecycleAuthority,
            ProductionRegistrationIdentityReader);
        return new RegisteredOwnedProcess(
            process,
            processHandle: null,
            startInfo,
            GetRegisteredJobOrThrow(process.Id, lifecycleAuthority),
            lifecycleAuthority);
    }

    /// <param name="inheritableWindowObserver">
    /// Invoked while this launch's inheritable capture duplicates exist, immediately before
    /// CreateProcessW. Per-launch; production callers leave it null.
    /// </param>
    internal static Process StartRegisteredWithFileCaptureOrThrow(
        ProcessStartInfo startInfo,
        string stdoutPath,
        string stderrPath,
        string? ownerId = null,
        Action? inheritableWindowObserver = null)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Owned file-capture launch is Windows-only.");
        }

        return StartRegisteredWindows(
            () => OwnedProcessGroup.StartSuspendedWithFileCapture(
                startInfo,
                stdoutPath,
                stderrPath,
                inheritableWindowObserver),
            ownerId);
    }

    private static Process StartRegisteredWindows(
        Func<OwnedProcessGroup.SuspendedProcessStart> start,
        string? ownerId)
    {
        OwnedProcessGroup.SuspendedProcessStart launch;
        try
        {
            launch = start();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
        {
            throw new InvalidOperationException(
                $"worker-process-start-failed: stage=owned-process-group-launch; cleanup=owned-job-termination-requested; {BuildExceptionEvidence(ex)}",
                ex);
        }

        using (launch)
        {
            var victimIdentityReader = BuildSuspendedRegistrationIdentityReader(launch.ReadLifecycleIdentity);
            if (!TryRegisterCore(
                    launch.Process,
                    ownerId,
                    victimIdentityReader,
                    ProductionRegistrationIdentityReader,
                    out var registrationFailure,
                    launch.Group,
                    launch.Resume))
            {
                throw new InvalidOperationException(registrationFailure);
            }

            return launch.TransferOwnership();
        }
    }

    private static RegisteredOwnedProcess StartRegisteredOwnedWindows(
        Func<OwnedProcessGroup.SuspendedProcessStart> start,
        ProcessStartInfo startInfo,
        object lifecycleAuthority,
        Func<Process, SpawnProcessIdentityReadResult>? registrationIdentityReader,
        string? ownerId)
    {
        OwnedProcessGroup.SuspendedProcessStart launch;
        try
        {
            launch = start();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
        {
            throw new InvalidOperationException(
                $"worker-process-start-failed: stage=owned-process-group-launch; cleanup=owned-job-termination-requested; {BuildExceptionEvidence(ex)}",
                ex);
        }

        using (launch)
        {
            var victimIdentityReader = registrationIdentityReader ??
                BuildSuspendedRegistrationIdentityReader(launch.ReadLifecycleIdentity);
            var ownerIdentityReader = registrationIdentityReader ?? ProductionRegistrationIdentityReader;
            if (!TryRegisterCore(
                    launch.Process,
                    ownerId,
                    victimIdentityReader,
                    ownerIdentityReader,
                    out var registrationFailure,
                    launch.Group,
                    launch.Resume,
                    lifecycleAuthority: lifecycleAuthority))
            {
                throw new InvalidOperationException(registrationFailure);
            }

            var registration = GetRegisteredJobOrThrow(launch.Process.Id, lifecycleAuthority);
            var transfer = launch.TransferOwnedProcess();
            return new RegisteredOwnedProcess(
                transfer.Process,
                transfer.ProcessHandle,
                startInfo,
                registration,
                lifecycleAuthority);
        }
    }

    private static void RegisterOwnedOrThrow(
        Process process,
        string? ownerId,
        object lifecycleAuthority,
        Func<Process, SpawnProcessIdentityReadResult> registrationIdentityReader)
    {
        if (!TryRegisterCore(
                process,
                ownerId,
                registrationIdentityReader,
                registrationIdentityReader,
                out var registrationFailure,
                lifecycleAuthority: lifecycleAuthority))
        {
            throw new InvalidOperationException(registrationFailure);
        }
    }

    internal static Process StartAndRegisterNonWindows(
        ProcessStartInfo startInfo,
        string? ownerId,
        Func<ProcessStartInfo, Process> startProcess,
        Action<Process, string?> registerProcess)
    {
        var process = startProcess(startInfo);
        try
        {
            registerProcess(process, ownerId);
            return process;
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }

    internal static bool TryRegister(
        Process process,
        string? ownerId,
        Func<Process, SpawnProcessIdentity?> readIdentity)
    {
        var registrationReader = BuildRegistrationIdentityReader(readIdentity, identityReadDelay: null);
        return TryRegisterCore(process, ownerId, registrationReader, registrationReader, out _);
    }

    internal static bool TryRegister(
        Process process,
        string? ownerId,
        Func<Process, SpawnProcessIdentity?> readVictimIdentity,
        Func<Process, SpawnProcessIdentity?> readOwnerIdentity)
    {
        return TryRegisterCore(
            process,
            ownerId,
            BuildRegistrationIdentityReader(readVictimIdentity, identityReadDelay: null),
            BuildRegistrationIdentityReader(readOwnerIdentity, identityReadDelay: null),
            out _);
    }

    internal static bool TryRegister(
        Process process,
        string? ownerId,
        Func<Process, SpawnProcessIdentity?> readVictimIdentity,
        Func<Process, SpawnProcessIdentity?> readOwnerIdentity,
        Action<int> identityReadDelay,
        out string registrationDiagnostic)
    {
        ArgumentNullException.ThrowIfNull(identityReadDelay);
        return TryRegisterCore(
            process,
            ownerId,
            BuildRegistrationIdentityReader(readVictimIdentity, identityReadDelay),
            BuildRegistrationIdentityReader(readOwnerIdentity, identityReadDelay),
            out registrationDiagnostic);
    }

    internal static bool TryRegisterWithAttachmentForTests(
        Process process,
        string? ownerId,
        Func<Process, OwnedProcessGroup> attachProcess,
        out string registrationFailure)
    {
        ArgumentNullException.ThrowIfNull(attachProcess);
        return TryRegisterCore(
            process,
            ownerId,
                ProductionRegistrationIdentityReader,
                ProductionRegistrationIdentityReader,
            out registrationFailure,
            attachProcess: attachProcess);
    }

    internal static bool TryRegisterSuspendedForTests(
        Process process,
        string? ownerId,
        OwnedProcessGroup group,
        Func<Process, SpawnProcessIdentity?> readVictimIdentity,
        Func<Process, SpawnProcessIdentity?> readOwnerIdentity,
        Action resumeProcess,
        Action<SpawnRegistry, int, string> markReleased,
        out string registrationFailure)
    {
        return TryRegisterCore(
            process,
            ownerId,
            BuildRegistrationIdentityReader(readVictimIdentity, identityReadDelay: null),
            BuildRegistrationIdentityReader(readOwnerIdentity, identityReadDelay: null),
            out registrationFailure,
            group,
            resumeProcess,
            markResumeFailureReleased: markReleased);
    }

    private static bool TryRegisterCore(
        Process process,
        string? ownerId,
        Func<Process, SpawnProcessIdentityReadResult> readVictimIdentity,
        Func<Process, SpawnProcessIdentityReadResult> readOwnerIdentity,
        out string registrationFailure,
        OwnedProcessGroup? preAttachedGroup = null,
        Action? resumeProcess = null,
        Func<Process, OwnedProcessGroup>? attachProcess = null,
        Action<SpawnRegistry, int, string>? markResumeFailureReleased = null,
        object? lifecycleAuthority = null)
    {
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(readVictimIdentity);
        ArgumentNullException.ThrowIfNull(readOwnerIdentity);
        registrationFailure = string.Empty;

        if (IsProtectedProcessOrAncestor(process.Id))
        {
            registrationFailure = BuildRegistrationFailure(
                process.Id,
                "protected-process-boundary",
                "refused-protected-process");
            return false;
        }

        // Registration is single-shot. A PID collision may be a duplicate call, a concurrent
        // publication that has not reached the durable registry yet, or a recycled PID behind a
        // stale in-memory entry. None is safe to accept as success without a registration state
        // machine, so fail closed and terminate the candidate rather than bypassing ownership.
        if (Jobs.TryGetValue(process.Id, out var conflictingRegistration))
        {
            var observedIdentity = ObserveRegistrationCandidateIdentity(process);
            registrationFailure = BuildRegistrationFailure(
                process.Id,
                "duplicate-or-recycled-pid",
                "process-tree-termination-requested",
                conflictingRegistration.Identity,
                observedIdentity);
            TryTerminateUnregisteredProcess(process);
            return false;
        }

        var registry = Registry;
        OwnedProcessGroup? group = preAttachedGroup;
        Microsoft.Win32.SafeHandles.SafeFileHandle? duplicate = null;
        var failureStage = "owned-process-group-attachment";
        try
        {
            group ??= (attachProcess ?? OwnedProcessGroup.Attach)(process);
            SpawnProcessIdentity? victimIdentity = null;
            var victimIdentityReadAttempts = 0;
            SpawnProcessIdentity? ownerIdentity = null;
            var durableRegistrationAvailable = registry is not null;
            if (registry is not null)
            {
                failureStage = "victim-identity-read";
                var victimRead = readVictimIdentity(process);
                victimIdentity = victimRead.Identity;
                victimIdentityReadAttempts = victimRead.Attempts;
                if (victimIdentity is null)
                {
                    registrationFailure = BuildRegistrationDegradation(
                        process.Id,
                        failureStage,
                        victimRead.Evidence);
                    durableRegistrationAvailable = false;
                }

                if (durableRegistrationAvailable)
                {
                    failureStage = "owner-identity-read";
                    using var ownerProcess = Process.GetCurrentProcess();
                    var ownerRead = readOwnerIdentity(ownerProcess);
                    ownerIdentity = ownerRead.Identity;
                    if (ownerIdentity is null)
                    {
                        registrationFailure = BuildRegistrationDegradation(
                            process.Id,
                            failureStage,
                            ownerRead.Evidence);
                        durableRegistrationAvailable = false;
                    }
                }

            }

            failureStage = "job-publication";
            duplicate = group.TryDuplicateAccountingHandle(out var duplicateHandle) ? duplicateHandle : null;
            var snapshot = group.TryReadAccounting(out var registrationAccounting)
                ? registrationAccounting with { AccountingSource = "snapshot" }
                : null;
            var registeredJob = new RegisteredJob(
                group,
                duplicate,
                snapshot,
                victimIdentity,
                victimIdentityReadAttempts,
                RequiresDurableDetach: registry is not null && durableRegistrationAvailable,
                LifecycleAuthority: lifecycleAuthority);
            if (Jobs.TryAdd(process.Id, registeredJob))
            {
                group = null;
                duplicate = null;
                var durableRegistrationFailure = string.Empty;
                var durableRegistrationSucceeded = registry is null ||
                    !durableRegistrationAvailable ||
                    RegisterDurable(
                        registry,
                        victimIdentity!,
                        ownerIdentity!,
                        ownerId,
                        out durableRegistrationFailure);
                if (durableRegistrationSucceeded)
                {
                    if (!durableRegistrationAvailable)
                    {
                        RecordRegistrationDegradation(registrationFailure);
                    }

                    if (resumeProcess is not null)
                    {
                        failureStage = "process-resume";
                        try
                        {
                            resumeProcess();
                        }
                        catch (Exception resumeException)
                        {
                            MarkResumeFailureReleased(
                                registry,
                                process.Id,
                                resumeException,
                                markResumeFailureReleased);
                            if (Jobs.TryRemove(process.Id, out var failedResume))
                            {
                                ReadAccountingAndDispose(
                                    failedResume,
                                    kill: true,
                                    captureAccounting: false,
                                    preferDuplicate: false,
                                    out _);
                            }

                            throw;
                        }
                    }

                    return true;
                }

                registrationFailure = BuildRegistrationFailure(
                    process.Id,
                    "durable-registry-write",
                    "registered-process-tree-termination-requested") +
                    (string.IsNullOrWhiteSpace(durableRegistrationFailure)
                        ? string.Empty
                        : $"; evidence={durableRegistrationFailure}");
                if (Jobs.TryRemove(process.Id, out var failedRegistration))
                {
                    ReadAccountingAndDispose(
                        failedRegistration,
                        kill: true,
                        captureAccounting: false,
                        preferDuplicate: false,
                        out _);
                }

                return false;
            }

            _ = Jobs.TryGetValue(process.Id, out conflictingRegistration);
            var observedIdentity = victimIdentity is null
                ? ObserveRegistrationCandidateIdentity(process)
                : new RegistrationIdentityObservation(victimIdentity, "ok");
            registrationFailure = BuildRegistrationFailure(
                process.Id,
                "duplicate-or-recycled-pid",
                "attached-process-tree-termination-requested",
                conflictingRegistration?.Identity,
                observedIdentity);
            ReadAccountingAndDispose(group, kill: true, captureAccounting: false, out _);
            group = null;
            duplicate?.Dispose();
            duplicate = null;
            return false;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
        {
            registrationFailure = BuildRegistrationFailure(
                process.Id,
                failureStage,
                $"process-tree-termination-requested; {BuildExceptionEvidence(ex)}");
            if (group is null)
            {
                TryTerminateUnregisteredProcess(process);
            }
        }
        finally
        {
            if (group is not null)
            {
                ReadAccountingAndDispose(group, kill: true, captureAccounting: false, out _);
            }

            duplicate?.Dispose();
        }

        return false;
    }

    private static void MarkResumeFailureReleased(
        SpawnRegistry? registry,
        int processId,
        Exception resumeException,
        Action<SpawnRegistry, int, string>? markReleased)
    {
        if (registry is null)
        {
            return;
        }

        const string Diagnostic = "spawn_registry: process resume failed";
        try
        {
            var release = markReleased ?? MarkRegistryReleased;
            release(registry, processId, Diagnostic);
            return;
        }
        catch (Exception firstReleaseException)
        {
            try
            {
                registry.MarkReleased(processId, Diagnostic + "; retry=1");
                return;
            }
            catch (Exception retryReleaseException)
            {
                resumeException.Data["resume_rollback_release_failure"] =
                    $"first={firstReleaseException.GetType().Name}:{SanitizeDiagnostic(firstReleaseException.Message)}," +
                    $"retry={retryReleaseException.GetType().Name}:{SanitizeDiagnostic(retryReleaseException.Message)}";
            }
        }
    }

    private static void MarkRegistryReleased(SpawnRegistry registry, int processId, string diagnostic) =>
        registry.MarkReleased(processId, diagnostic);

    private static string BuildRegistrationFailure(int processId, string stage, string cleanup) =>
        $"worker-process-registration-failed: pid={processId.ToString(System.Globalization.CultureInfo.InvariantCulture)}; stage={stage}; cleanup={cleanup}";

    private static string BuildRegistrationFailure(
        int processId,
        string stage,
        string cleanup,
        SpawnProcessIdentity? recordedIdentity,
        RegistrationIdentityObservation observedIdentity)
    {
        var mismatch = DescribeIdentityMismatch(recordedIdentity, observedIdentity);
        var conflict = mismatch switch
        {
            "none" => "duplicate-registration",
            "start-time" or "image" or "start-time,image" => "recycled-pid",
            _ => "indeterminate"
        };
        return BuildRegistrationFailure(processId, stage, cleanup) +
            $"; conflict={conflict}" +
            $"; recorded_started_at={FormatStartedAt(recordedIdentity)}" +
            $"; recorded_image={FormatImageName(recordedIdentity)}" +
            $"; observed_started_at={FormatStartedAt(observedIdentity.Identity)}" +
            $"; observed_image={FormatImageName(observedIdentity.Identity)}" +
            $"; mismatch={mismatch}";
    }

    private static RegistrationIdentityObservation ObserveRegistrationCandidateIdentity(Process process)
    {
        try
        {
            var identity = ReadIdentityOnce(process);
            return new RegistrationIdentityObservation(identity, identity is null ? "unavailable" : "ok");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException or UnauthorizedAccessException)
        {
            return new RegistrationIdentityObservation(null, "unreadable");
        }
    }

    private static string DescribeIdentityMismatch(
        SpawnProcessIdentity? recordedIdentity,
        RegistrationIdentityObservation observedIdentity)
    {
        if (recordedIdentity is null)
        {
            return "recorded-identity-unavailable";
        }

        if (observedIdentity.Identity is null)
        {
            return observedIdentity.ReadStatus == "unreadable"
                ? "observed-identity-unreadable"
                : "observed-identity-unavailable";
        }

        var startTimeMismatch = recordedIdentity.StartedAt != observedIdentity.Identity.StartedAt;
        var imageMismatch = !recordedIdentity.ImagePath.Equals(
            observedIdentity.Identity.ImagePath,
            StringComparison.OrdinalIgnoreCase);
        return (startTimeMismatch, imageMismatch) switch
        {
            (false, false) => "none",
            (true, false) => "start-time",
            (false, true) => "image",
            (true, true) => "start-time,image"
        };
    }

    private static string FormatStartedAt(SpawnProcessIdentity? identity) =>
        identity?.StartedAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture) ?? "unavailable";

    private static string FormatImageName(SpawnProcessIdentity? identity) =>
        identity is null || string.IsNullOrWhiteSpace(identity.ImagePath)
            ? "unavailable"
            : Path.GetFileName(identity.ImagePath);

    private sealed record RegistrationIdentityObservation(
        SpawnProcessIdentity? Identity,
        string ReadStatus);

    private static string BuildRegistrationDegradation(int processId, string stage, string readEvidence) =>
        $"worker-process-registration-degraded: pid={processId.ToString(System.Globalization.CultureInfo.InvariantCulture)}; stage={stage}; {readEvidence}; outcome=durable-registration-skipped-process-preserved";

    private static void RecordRegistrationDegradation(string diagnostic)
    {
        try
        {
            Console.WriteLine(diagnostic);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WorkerProcessJobs] Failed to emit registration degradation: {ex.GetType().Name}: {ex.Message}");
        }

        var dbPath = RegistryDbPath;
        if (string.IsNullOrWhiteSpace(dbPath))
        {
            return;
        }

        try
        {
            new SqliteRunEventStore(dbPath)
                .AppendAsync(new RunEventAppend(
                    RunEventTypes.ConductorSupervision,
                    GoalId: null,
                    Operation: "WORKER_PROCESS_REGISTRATION_DEGRADED",
                    Status: "degraded",
                    Detail: diagnostic,
                    PayloadJson: null))
                .GetAwaiter()
                .GetResult();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WorkerProcessJobs] Failed to persist registration degradation: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static Func<Process, SpawnProcessIdentityReadResult> BuildRegistrationIdentityReader(
        Func<Process, SpawnProcessIdentity?> readIdentity,
        Action<int>? identityReadDelay)
    {
        ArgumentNullException.ThrowIfNull(readIdentity);
        Action<int> delay = identityReadDelay ?? (static delayMilliseconds => Thread.Sleep(delayMilliseconds));
        return process =>
        {
            Exception? lastReadException = null;
            var result = SpawnProcessIdentityReader.ReadForRegistration(
                process,
                candidate =>
                {
                    try
                    {
                        return readIdentity(candidate);
                    }
                    catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException or UnauthorizedAccessException)
                    {
                        lastReadException = ex;
                        return null;
                    }
                },
                delay,
                IdentityReadAttempts,
                IdentityReadDelayMilliseconds);
            return !result.Succeeded && lastReadException is not null
                ? result with { Evidence = $"{result.Evidence}; {BuildExceptionEvidence(lastReadException)}" }
                : result;
        };
    }

    private static SpawnProcessIdentity? ReadIdentityOnce(Process process)
    {
        var imagePath = process.MainModule?.FileName;
        return string.IsNullOrWhiteSpace(imagePath)
            ? null
            : new SpawnProcessIdentity(
                process.Id,
                new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero),
                imagePath);
    }

    private static Func<Process, SpawnProcessIdentityReadResult> BuildSuspendedRegistrationIdentityReader(
        Func<int, WindowsNativeProcessInspection.ProcessLifecycleIdentityReadResult> readLifecycleIdentity)
    {
        ArgumentNullException.ThrowIfNull(readLifecycleIdentity);
        return process =>
        {
            var read = readLifecycleIdentity(process.Id);
            var evidence = $"status={read.Status.ToString().ToLowerInvariant()} attempts=1 " +
                $"source={read.Operation} native_error_code={read.NativeError.ToString(CultureInfo.InvariantCulture)}";
            return read.Status == ProcessInspectionStatus.Available &&
                   read.StartedAt is { } startedAt &&
                   !string.IsNullOrWhiteSpace(read.ExecutablePath)
                ? new SpawnProcessIdentityReadResult(
                    new SpawnProcessIdentity(process.Id, startedAt, read.ExecutablePath),
                    1,
                    evidence)
                : new SpawnProcessIdentityReadResult(null, 1, evidence);
        };
    }

    internal static string BuildExceptionEvidence(Exception exception)
    {
        var parts = new List<string>
        {
            $"exception={exception.GetType().Name}"
        };
        if (exception is Win32Exception win32)
        {
            var nativeErrorCode = win32.NativeErrorCode;
            parts.Add($"native_error_code={nativeErrorCode.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
            parts.Add($"native_message={SanitizeDiagnostic(new Win32Exception(nativeErrorCode).Message)}");
            parts.Add($"operation_message={SanitizeDiagnostic(exception switch
            {
                OwnedProcessAttachmentException attachment => attachment.OperationMessage,
                OwnedProcessLaunchException launch => launch.OperationMessage,
                _ => exception.Message
            })}");
            var jobEvidence = exception switch
            {
                OwnedProcessAttachmentException attachment => attachment.JobEvidence,
                OwnedProcessLaunchException launch => launch.JobEvidence,
                _ => null
            };
            if (!string.IsNullOrWhiteSpace(jobEvidence))
            {
                parts.Add(jobEvidence);
            }
        }
        else
        {
            parts.Add($"message={SanitizeDiagnostic(exception.Message)}");
        }

        if (exception.Data["resume_rollback_release_failure"] is string rollbackFailure)
        {
            parts.Add($"resume_rollback_release_failure={SanitizeDiagnostic(rollbackFailure)}");
        }

        return string.Join("; ", parts);
    }

    public static bool TryKillOrFallback(int processId)
    {
        return TryKillOrFallback(processId, allowProtectedDescendant: false, markRegistryReleased: true, out _);
    }

    public static bool TryKillOrFallback(int processId, out WorkerProcessJobAccounting? accounting)
    {
        return TryKillOrFallback(processId, allowProtectedDescendant: false, markRegistryReleased: true, out accounting);
    }

    internal static bool TryKillOrFallbackWithoutRegistry(int processId)
    {
        return TryKillOrFallback(processId, allowProtectedDescendant: false, markRegistryReleased: false, out _);
    }

    internal static bool WasDetachedByConductor(
        string ownerId,
        int processId,
        DateTimeOffset processRecordedAt)
    {
        try
        {
            return Registry?.WasDetachedByConductor(ownerId, processId, processRecordedAt) == true;
        }
        catch
        {
            return false;
        }
    }

    internal static bool TryKillRecordedOwnedChildAndWait(int processId, TimeSpan timeout)
    {
        return TryKillOrFallbackAndWait(processId, timeout, allowProtectedDescendant: true);
    }

    private static bool TryKillOrFallback(
        int processId,
        bool allowProtectedDescendant,
        bool markRegistryReleased,
        out WorkerProcessJobAccounting? accounting)
    {
        accounting = null;
        if (!CanKillProcess(processId, allowProtectedDescendant))
        {
            return false;
        }

        var removal = TryRemoveStaticRegistration(processId, out var job);
        if (removal == StaticRegistrationRemoval.OwnedByReturnedChild)
        {
            return false;
        }

        if (removal == StaticRegistrationRemoval.Removed)
        {
            if (ReadAccountingAndDispose(job, kill: true, captureAccounting: true, preferDuplicate: false, out accounting))
            {
                if (markRegistryReleased)
                {
                    Registry?.MarkReleased(processId, $"spawn_registry: killed pid={processId}");
                }

                return true;
            }
        }

        var fallbackOwnedTempRoots = TempRootJanitor.SnapshotOwnedRoots([processId])
            .Select(root => root with { CaptureSource = "worker-process-jobs/fallback-kill" })
            .ToArray();
        var fallbackKilled = TryKillPidTree(processId);
        if (fallbackKilled)
        {
            EmitTempRootReapResults(ReapOwnedTempRoots(fallbackOwnedTempRoots));
        }
        if (fallbackKilled && markRegistryReleased)
        {
            Registry?.MarkReleased(processId, $"spawn_registry: fallback-killed pid={processId}");
        }

        return fallbackKilled;
    }

    public static bool TryKillOrFallbackAndWait(int processId, TimeSpan timeout)
    {
        return TryKillOrFallbackAndWait(processId, timeout, allowProtectedDescendant: false);
    }

    private static bool TryKillOrFallbackAndWait(int processId, TimeSpan timeout, bool allowProtectedDescendant)
    {
        if (!TryKillOrFallback(processId, allowProtectedDescendant, markRegistryReleased: true, out _))
        {
            return false;
        }

        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (!IsProcessRunning(processId))
            {
                return true;
            }

            Thread.Sleep(50);
        }

        return !IsProcessRunning(processId);
    }

    public static void Release(int processId)
    {
        Release(processId, out _);
    }

    public static void Release(int processId, out WorkerProcessJobAccounting? accounting)
    {
        accounting = null;
        var removal = TryRemoveStaticRegistration(processId, out var job);
        if (removal == StaticRegistrationRemoval.OwnedByReturnedChild)
        {
            return;
        }

        if (removal == StaticRegistrationRemoval.Removed)
        {
            try
            {
                Registry?.MarkReleased(processId, $"spawn_registry: released pid={processId}");
            }
            finally
            {
                ReadAccountingAndDispose(job, kill: true, captureAccounting: true, preferDuplicate: false, out accounting);
            }

            return;
        }

        Registry?.MarkReleased(processId, $"spawn_registry: released pid={processId}");
    }

    /// <summary>
    /// Releases a registration and reports what was actually proven about its termination, so a caller
    /// that must decide whether it still owns anything can say so instead of assuming it.
    /// </summary>
    internal static WorkerProcessJobReleaseEvidence ReleaseWithCompletionEvidence(
        int processId,
        out WorkerProcessJobAccounting? accounting)
    {
        accounting = null;
        var removal = TryRemoveStaticRegistration(processId, out var job);
        if (removal != StaticRegistrationRemoval.Removed)
        {
            if (removal == StaticRegistrationRemoval.Missing)
            {
                Registry?.MarkReleased(processId, $"spawn_registry: released pid={processId}");
            }

            return new WorkerProcessJobReleaseEvidence(
                RegistrationFound: false,
                TerminationRequested: false,
                JobExitConfirmed: false);
        }

        WorkerProcessJobReleaseEvidence evidence;
        try
        {
            Registry?.MarkReleased(processId, $"spawn_registry: released pid={processId}");
        }
        finally
        {
            var terminationRequested = ReadAccountingAndDispose(
                job,
                kill: true,
                captureAccounting: true,
                preferDuplicate: false,
                out accounting,
                out var jobExitConfirmed);
            evidence = new WorkerProcessJobReleaseEvidence(
                RegistrationFound: true,
                TerminationRequested: terminationRequested,
                JobExitConfirmed: jobExitConfirmed);
        }

        return evidence;
    }

    internal static RegisteredJob GetRegisteredJobOrThrow(int processId, object lifecycleAuthority)
    {
        if (Jobs.TryGetValue(processId, out var job) &&
            job.IsAuthorizedBy(lifecycleAuthority))
        {
            return job;
        }

        throw new InvalidOperationException(
            $"worker-process-registration-missing: pid={processId}; stage=owned-child-transfer");
    }

    internal static bool TryGetRegisteredIdentity(int processId, out SpawnProcessIdentity identity)
    {
        if (Jobs.TryGetValue(processId, out var job) && job.Identity is { } registeredIdentity)
        {
            identity = registeredIdentity;
            return true;
        }

        identity = default!;
        return false;
    }

    internal static bool TryGetActiveProcessIds(int processId, out IReadOnlyList<int> processIds)
    {
        processIds = [];
        return Jobs.TryGetValue(processId, out var job) &&
            job.Group.TryGetActiveProcessIds(out processIds);
    }

    internal static void ReleaseOwned(
        int processId,
        RegisteredJob registration,
        object lifecycleAuthority,
        out WorkerProcessJobAccounting? accounting)
    {
        accounting = null;
        if (!registration.IsAuthorizedBy(lifecycleAuthority))
        {
            throw new InvalidOperationException(
                $"worker-process-owned-lifecycle-authority-mismatch: pid={processId}; stage=owned-child-release");
        }

        if (!((ICollection<KeyValuePair<int, RegisteredJob>>)Jobs).Remove(
                new KeyValuePair<int, RegisteredJob>(processId, registration)))
        {
            return;
        }

        try
        {
            Registry?.MarkReleased(processId, $"spawn_registry: released pid={processId}");
        }
        finally
        {
            ReadAccountingAndDispose(
                registration,
                kill: true,
                captureAccounting: true,
                preferDuplicate: false,
                out accounting);
        }
    }

    internal static bool TryHandOffToRuntimeOwnership(int processId, out string failure)
    {
        return TryDetach(
            processId,
            "worker-process-handoff-failed",
            "spawn_registry: runtime-owned",
            SpawnRegistryLifecycle.RuntimeOwned,
            expectedOwnerId: null,
            expectedProcessStartedAt: null,
            out _,
            out failure);
    }

    internal static bool TryDetachForGracefulStop(int processId, out string failure)
    {
        return TryDetach(
            processId,
            "worker-process-detach-failed",
            "spawn_registry: gracefully-detached",
            SpawnRegistryLifecycle.ConductorDetached,
            expectedOwnerId: null,
            expectedProcessStartedAt: null,
            out _,
            out failure);
    }

    internal static bool TryDetachForGracefulStop(
        TaskProcessRecord expectedProcess,
        string expectedOwnerId,
        out TaskProcessRecord detachedProcess,
        out bool safeToCancelOnFailure,
        out bool requeueWithoutTerminationOnFailure,
        out string failure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedOwnerId);
        detachedProcess = expectedProcess;
        requeueWithoutTerminationOnFailure = false;
        if (expectedProcess.ProcessIdentityStartedAt is not { } expectedProcessStartedAt &&
            !TryResolveLegacyDispatchIdentity(
                expectedProcess.ProcessId,
                expectedOwnerId,
                out expectedProcessStartedAt,
                out safeToCancelOnFailure,
                out requeueWithoutTerminationOnFailure,
                out failure))
        {
            return false;
        }

        detachedProcess = expectedProcess with { ProcessIdentityStartedAt = expectedProcessStartedAt };

        return TryDetach(
            expectedProcess.ProcessId,
            "worker-process-detach-failed",
            "spawn_registry: gracefully-detached",
            SpawnRegistryLifecycle.ConductorDetached,
            expectedOwnerId,
            expectedProcessStartedAt,
            out safeToCancelOnFailure,
            out failure);
    }

    private static bool TryResolveLegacyDispatchIdentity(
        int processId,
        string expectedOwnerId,
        out DateTimeOffset processStartedAt,
        out bool safeToCancelOnFailure,
        out bool requeueWithoutTerminationOnFailure,
        out string failure)
    {
        processStartedAt = default;
        safeToCancelOnFailure = false;
        requeueWithoutTerminationOnFailure = true;
        failure = $"worker-process-detach-failed: pid={processId}; stage=expected-dispatch-identity-missing";
        var registry = Registry;
        if (registry is null)
        {
            return false;
        }

        SpawnRegistryEntry[] entries;
        try
        {
            entries = registry.ListActive()
                .Where(entry => entry.ProcessId == processId)
                .ToArray();
        }
        catch (Exception ex)
        {
            requeueWithoutTerminationOnFailure = false;
            failure = $"worker-process-detach-failed: pid={processId}; stage=legacy-dispatch-identity-read; error={ex.GetType().Name}";
            return false;
        }

        if (entries.Length == 0)
        {
            return false;
        }

        if (entries.Length != 1)
        {
            requeueWithoutTerminationOnFailure = false;
            failure = $"worker-process-detach-failed: pid={processId}; stage=legacy-dispatch-identity-ambiguous";
            return false;
        }

        var entry = entries[0];
        if (!string.Equals(entry.OwnerId, expectedOwnerId, StringComparison.Ordinal))
        {
            requeueWithoutTerminationOnFailure = false;
            failure = $"worker-process-detach-failed: pid={processId}; stage=durable-owner-mismatch; expected_owner={expectedOwnerId}";
            return false;
        }

        if (entry.Lifecycle is not (SpawnRegistryLifecycle.RuntimeOwned or SpawnRegistryLifecycle.GracefullyDetached))
        {
            requeueWithoutTerminationOnFailure = false;
            failure = $"worker-process-detach-failed: pid={processId}; stage=legacy-dispatch-lifecycle-unexpected";
            return false;
        }

        processStartedAt = entry.ProcessStartedAt;
        requeueWithoutTerminationOnFailure = false;
        return true;
    }

    private static bool TryDetach(
        int processId,
        string failurePrefix,
        string lifecycleDiagnostic,
        SpawnRegistryLifecycle detachedLifecycle,
        string? expectedOwnerId,
        DateTimeOffset? expectedProcessStartedAt,
        out bool safeToCancelOnFailure,
        out string failure)
    {
        failure = string.Empty;
        safeToCancelOnFailure = true;
        var registry = Registry;
        if (expectedProcessStartedAt is { } expectedStartedAt &&
            Jobs.TryGetValue(processId, out var currentJob) &&
            (currentJob.Identity is not { } currentIdentity ||
             currentIdentity.StartedAt != expectedStartedAt))
        {
            safeToCancelOnFailure = false;
            failure = $"{failurePrefix}: pid={processId}; stage=expected-dispatch-attempt-mismatch";
            return false;
        }

        var removal = TryRemoveStaticRegistration(processId, out var job);
        if (removal == StaticRegistrationRemoval.OwnedByReturnedChild)
        {
            failure = $"{failurePrefix}: pid={processId}; stage=owned-child-authority";
            return false;
        }

        if (removal == StaticRegistrationRemoval.Missing)
        {
            SpawnRegistryEntry[] entries;
            try
            {
                entries = registry?.ListActive()
                    .Where(entry => entry.ProcessId == processId)
                    .ToArray() ?? [];
            }
            catch (Exception ex)
            {
                failure =
                    $"{failurePrefix}: pid={processId}; stage=durable-lifecycle-read; error={ex.GetType().Name}";
                return false;
            }

            if (entries.Length == 0)
            {
                return true;
            }

            if (entries.Length != 1)
            {
                failure = $"{failurePrefix}: pid={processId}; stage=durable-lifecycle-ambiguous";
                return false;
            }

            var entry = entries[0];
            if (!string.IsNullOrWhiteSpace(expectedOwnerId))
            {
                if (!string.Equals(entry.OwnerId, expectedOwnerId, StringComparison.Ordinal))
                {
                    safeToCancelOnFailure = false;
                    failure =
                        $"{failurePrefix}: pid={processId}; stage=durable-owner-mismatch; expected_owner={expectedOwnerId}";
                    return false;
                }

                if (expectedProcessStartedAt is { } expectedEntryStartedAt &&
                    entry.ProcessStartedAt != expectedEntryStartedAt)
                {
                    safeToCancelOnFailure = false;
                    failure = $"{failurePrefix}: pid={processId}; stage=durable-dispatch-attempt-mismatch";
                    return false;
                }
            }

            if (entry.Lifecycle == detachedLifecycle ||
                (detachedLifecycle == SpawnRegistryLifecycle.RuntimeOwned &&
                 entry.Lifecycle == SpawnRegistryLifecycle.GracefullyDetached))
            {
                return true;
            }

            if (detachedLifecycle == SpawnRegistryLifecycle.ConductorDetached &&
                registry is not null &&
                registry.TryMarkConductorDetached(
                    entry,
                    $"{lifecycleDiagnostic} pid={processId}"))
            {
                return true;
            }

            failure = $"{failurePrefix}: pid={processId}; stage=in-memory-ownership-missing";
            return false;
        }

        var detachedWithoutKill = false;
        var failureStage = "durable-lifecycle-transition";
        try
        {
            if (registry is not null && job.RequiresDurableDetach)
            {
                var identity = job.Identity;
                if (identity is null ||
                    !(detachedLifecycle == SpawnRegistryLifecycle.RuntimeOwned
                        ? registry.TryMarkRuntimeOwned(identity, $"{lifecycleDiagnostic} pid={processId}")
                        : registry.TryMarkConductorDetached(identity, $"{lifecycleDiagnostic} pid={processId}")))
                {
                    failure = $"{failurePrefix}: pid={processId}; stage={failureStage}";
                    return false;
                }
            }

            failureStage = "os-process-group-detach";
            if (!TryDetachAndDispose(job))
            {
                failure = $"{failurePrefix}: pid={processId}; stage={failureStage}";
                return false;
            }

            detachedWithoutKill = true;
            return true;
        }
        catch (Exception ex)
        {
            failure =
                $"{failurePrefix}: pid={processId}; stage={failureStage}; error={ex.GetType().Name}";
            return false;
        }
        finally
        {
            if (!detachedWithoutKill)
            {
                ReadAccountingAndDispose(job, kill: true, captureAccounting: false, preferDuplicate: false, out _);
                try
                {
                    registry?.MarkReleased(processId, $"spawn_registry: detach-failed-reaped pid={processId}");
                }
                catch
                {
                    // The process tree and in-memory ownership are already deterministically cleaned up.
                    // The caller must still receive false so it records conductor-cancel recovery state.
                }
            }
        }
    }

    internal static void ReleaseWithoutAccounting(int processId)
    {
        var removal = TryRemoveStaticRegistration(processId, out var job);
        if (removal == StaticRegistrationRemoval.OwnedByReturnedChild)
        {
            return;
        }

        if (removal == StaticRegistrationRemoval.Removed)
        {
            ReadAccountingAndDispose(job, kill: true, captureAccounting: false, preferDuplicate: false, out _);
        }

        Registry?.MarkReleased(processId, $"spawn_registry: released pid={processId}");
    }

    internal static bool ReadAccountingAndDispose(
        OwnedProcessGroup? group,
        bool kill,
        bool captureAccounting,
        out WorkerProcessJobAccounting? accounting)
    {
        return ReadAccountingAndDispose(
            group is null ? null : new RegisteredJob(group, null, null),
            kill,
            captureAccounting,
            preferDuplicate: false,
            out accounting);
    }

    internal static void Reap(
        int processId,
        Action<int>? waitForExit,
        out WorkerProcessJobAccounting? accounting)
    {
        accounting = null;
        var removal = TryRemoveStaticRegistration(processId, out var job);
        if (removal == StaticRegistrationRemoval.OwnedByReturnedChild)
        {
            return;
        }

        if (removal == StaticRegistrationRemoval.Missing)
        {
            Registry?.MarkReleased(processId, $"spawn_registry: released pid={processId}");
            return;
        }

        try
        {
            var ownedTempRoots = SnapshotOwnedTempRoots(job.Group, "worker-process-jobs/reap");
            try
            {
                job.Group.Kill();
            }
            catch
            {
                // Reaping falls back to caller PID cleanup; duplicate accounting remains best-effort.
            }

            waitForExit?.Invoke(processId);
            if (job.DuplicateAccountingHandle is not null)
            {
                OwnedProcessGroup.WaitForJobExit(job.DuplicateAccountingHandle, TimeSpan.FromSeconds(5));
            }
            EmitTempRootReapResults(ReapOwnedTempRoots(ownedTempRoots));

            if (job.DuplicateAccountingHandle is not null &&
                OwnedProcessGroup.TryReadAccounting(job.DuplicateAccountingHandle, out var duplicateAccounting))
            {
                accounting = duplicateAccounting with { AccountingSource = "duplicate" };
            }
            else
            {
                accounting = job.RegistrationSnapshot;
            }
        }
        finally
        {
            try { job.Group.Dispose(); } catch { }
            try { job.DuplicateAccountingHandle?.Dispose(); } catch { }
            Registry?.MarkReleased(processId, $"spawn_registry: released pid={processId}");
        }
    }

    private static bool ReadAccountingAndDispose(
        RegisteredJob? job,
        bool kill,
        bool captureAccounting,
        bool preferDuplicate,
        out WorkerProcessJobAccounting? accounting)
    {
        return ReadAccountingAndDispose(
            job,
            kill,
            captureAccounting,
            preferDuplicate,
            out accounting,
            out _);
    }

    private static bool ReadAccountingAndDispose(
        RegisteredJob? job,
        bool kill,
        bool captureAccounting,
        bool preferDuplicate,
        out WorkerProcessJobAccounting? accounting,
        out bool jobExitConfirmed)
    {
        accounting = null;
        jobExitConfirmed = false;
        if (job is null)
        {
            return !kill;
        }

        var ownedTempRoots = kill
            ? SnapshotOwnedTempRoots(job.Group, "worker-process-jobs/accounting-dispose")
            : [];

        try
        {
            if (captureAccounting &&
                preferDuplicate &&
                job.DuplicateAccountingHandle is not null &&
                OwnedProcessGroup.TryReadAccounting(job.DuplicateAccountingHandle, out var duplicateAccounting))
            {
                accounting = duplicateAccounting with { AccountingSource = "duplicate" };
            }
            else if (captureAccounting && job.Group.TryReadAccounting(out var capturedAccounting))
            {
                accounting = capturedAccounting with { AccountingSource = "live" };
            }
            else if (captureAccounting)
            {
                accounting = job.RegistrationSnapshot;
            }

            if (captureAccounting && accounting is null && OperatingSystem.IsWindows())
            {
                accounting = WorkerProcessJobAccounting.Empty;
            }
        }
        catch
        {
            // Accounting is best-effort; disposal remains mandatory.
            if (captureAccounting && OperatingSystem.IsWindows())
            {
                accounting = job.RegistrationSnapshot ?? WorkerProcessJobAccounting.Empty;
            }
        }

        var killed = !kill;
        try
        {
            if (kill)
            {
                job.Group.Kill();
                if (job.DuplicateAccountingHandle is not null)
                {
                    jobExitConfirmed = OwnedProcessGroup.WaitForJobExit(
                        job.DuplicateAccountingHandle,
                        TimeSpan.FromSeconds(5));
                }

                EmitTempRootReapResults(ReapOwnedTempRoots(ownedTempRoots));
            }

            killed = true;
        }
        catch
        {
            // Fall back to PID cleanup at the caller when the owned job cannot be closed.
        }
        finally
        {
            try { job.Group.Dispose(); } catch { }
            try { job.DuplicateAccountingHandle?.Dispose(); } catch { }
        }

        return killed;
    }

    private static IReadOnlyList<int> SnapshotOwnedProcessIds(OwnedProcessGroup group)
    {
        try
        {
            return group.TryGetActiveProcessIds(out var processIds)
                ? processIds
                : [];
        }
        catch
        {
            // PID capture is best-effort; the next test-host startup remains the backstop.
            return [];
        }
    }

    private static IReadOnlyList<TempRootJanitorOwnedRoot> SnapshotOwnedTempRoots(
        OwnedProcessGroup group,
        string captureSource) =>
        TempRootJanitor.SnapshotOwnedRoots(SnapshotOwnedProcessIds(group))
            .Select(root => root with { CaptureSource = captureSource })
            .ToArray();

    internal static IReadOnlyList<TempRootJanitorReapResult> ReapOwnedTempRoots(
        IEnumerable<int> processIds,
        IEnumerable<string>? sharedRoots = null) =>
        sharedRoots is null
            ? TempRootJanitor.ReapOwnedRoots(processIds)
            : TempRootJanitor.ReapOwnedRoots(processIds, sharedRoots);

    internal static IReadOnlyList<TempRootJanitorReapResult> ReapOwnedTempRoots(
        IEnumerable<TempRootJanitorOwnedRoot> ownedRoots) =>
        TempRootJanitor.ReapOwnedRoots(ownedRoots);

    internal static void EmitTempRootReapResults(
        IEnumerable<TempRootJanitorReapResult> results,
        Action<string>? emit = null)
    {
        ArgumentNullException.ThrowIfNull(results);
        foreach (var result in results)
        {
            var diagnostic = TempRootJanitor.FormatDiagnostic(result);
            try
            {
                if (emit is null)
                {
                    Console.Error.WriteLine(diagnostic);
                }
                else
                {
                    emit(diagnostic);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    $"[WorkerProcessJobs] Failed to emit temp-root reap receipt: {ex.GetType().Name}");
            }
        }

    }

    internal static SpawnProcessIdentity? TryReadDirectChildIdentity(
        int parentProcessId,
        OwnedProcessGroup group)
    {
        try
        {
            var ownedProcessIds = SnapshotOwnedProcessIds(group);
            if (ownedProcessIds.Count == 0)
            {
                return null;
            }

            var inspection = WindowsNativeProcessInspection.Read(ownedProcessIds);
            if (inspection.Failure is not null)
            {
                return null;
            }

            var child = inspection.Records.Values
                .Where(record =>
                    record.ParentProcessId == parentProcessId &&
                    record.Status == ProcessInspectionStatus.Available &&
                    record.StartedAt is not null &&
                    !string.IsNullOrWhiteSpace(record.ExecutablePath) &&
                    !ProcessObservationRoles.IsWindowsConsoleInfrastructure(record.ExecutablePath))
                .OrderBy(record => record.StartedAt)
                .ThenBy(record => record.ProcessId)
                .FirstOrDefault();
            return child is null
                ? null
                : new SpawnProcessIdentity(child.ProcessId, child.StartedAt!.Value, child.ExecutablePath!);
        }
        catch
        {
            return null;
        }
    }

    private static bool TryDetachAndDispose(RegisteredJob job)
    {
        try
        {
            if (!job.Group.TryDetachWithoutKill())
            {
                return false;
            }
        }
        catch
        {
            return false;
        }

        try { job.DuplicateAccountingHandle?.Dispose(); } catch { }
        return true;
    }

    private static StaticRegistrationRemoval TryRemoveStaticRegistration(
        int processId,
        out RegisteredJob? registration)
    {
        registration = null;
        while (Jobs.TryGetValue(processId, out var candidate))
        {
            if (candidate.RequiresOwnedRelease)
            {
                return StaticRegistrationRemoval.OwnedByReturnedChild;
            }

            if (((ICollection<KeyValuePair<int, RegisteredJob>>)Jobs).Remove(
                    new KeyValuePair<int, RegisteredJob>(processId, candidate)))
            {
                registration = candidate;
                return StaticRegistrationRemoval.Removed;
            }
        }

        return StaticRegistrationRemoval.Missing;
    }

    internal static bool HasRegisteredJob(int processId) => Jobs.ContainsKey(processId);

    internal static bool HasRegisteredJob(int processId, RegisteredJob registration) =>
        Jobs.TryGetValue(processId, out var current) && ReferenceEquals(current, registration);

    private enum StaticRegistrationRemoval
    {
        Missing,
        Removed,
        OwnedByReturnedChild
    }


    internal static IReadOnlyList<SpawnRegistryEntry> ListActiveRegistryEntriesForTests() =>
        Registry?.ListActive() ?? [];

    internal static bool HasActiveRegistryEntryForTests(int processId) =>
        ListActiveRegistryEntriesForTests().Any(entry => entry.ProcessId == processId);

    internal static bool HasActiveJobForTests(int processId) => Jobs.ContainsKey(processId);

    internal static void ClearRegistryForTests()
    {
        Registry = null;
        RegistryDbPath = null;
    }

    public static IReadOnlyList<int> ListLiveDescendantProcessIds(int ancestorProcessId)
    {
        return WindowsNativeProcessInspection.ListIdentityBoundDescendantProcessIds(ancestorProcessId);
    }

    public static IReadOnlyList<int> ListConservativeDescendantProcessIdsForRefusal(
        int ancestorProcessId,
        DateTimeOffset recordedAncestorStartedAt)
    {
        return WindowsNativeProcessInspection.ListConservativeDescendantProcessIdsForRefusal(
            ancestorProcessId,
            recordedAncestorStartedAt);
    }

    private static bool RegisterDurable(
        SpawnRegistry registry,
        SpawnProcessIdentity identity,
        SpawnProcessIdentity ownerIdentity,
        string? ownerId,
        out string failure)
    {
        failure = string.Empty;
        try
        {
            registry.Register(
                string.IsNullOrWhiteSpace(ownerId) ? $"pid:{identity.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture)}" : ownerId,
                identity,
                ownerIdentity);
            return true;
        }
        catch (Exception ex)
        {
            failure = BuildExceptionEvidence(ex);
            return false;
        }
    }

    private static void TryTerminateUnregisteredProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                _ = process.WaitForExit(5000);
            }
        }
        catch
        {
            // Best-effort fallback after job attachment itself failed. The caller still receives false.
        }
    }

    private static bool DefaultTryKillPidTree(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (process.HasExited)
            {
                return true;
            }
        }
        catch (ArgumentException)
        {
            return true;
        }
        catch (InvalidOperationException)
        {
            return true;
        }

        if (OperatingSystem.IsWindows())
        {
            return TryTaskkillProcessTree(processId);
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            process.Kill(entireProcessTree: true);
            process.WaitForExit(5000);
            return true;
        }
        catch (ArgumentException)
        {
            return true;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryTaskkillProcessTree(int processId)
    {
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "taskkill.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            process.StartInfo.ArgumentList.Add("/T");
            process.StartInfo.ArgumentList.Add("/F");
            process.StartInfo.ArgumentList.Add("/PID");
            process.StartInfo.ArgumentList.Add(processId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (!process.Start())
            {
                return false;
            }

            process.WaitForExit(5000);
            return process.ExitCode == 0 || !IsProcessRunning(processId);
        }
        catch
        {
            return false;
        }
    }

    // Parent pid plus start time for one process. Windows keeps a dead parent's pid in its children's
    // Toolhelp32 records and recycles pids, so a parent pid alone does not establish ancestry.
    internal readonly record struct ProcessAncestryFacts(int ParentProcessId, DateTime StartTimeUtc);

    internal delegate bool ProcessAncestryLookup(int processId, out ProcessAncestryFacts facts);

    private static bool IsProtectedProcessOrAncestor(int processId)
    {
        return TryGetProtectedPid(out var protectedPid) &&
            IsProtectedProcessOrAncestor(processId, protectedPid, ReadProcessAncestryFacts);
    }

    internal static bool IsProtectedProcessOrAncestor(
        int processId,
        int protectedProcessId,
        ProcessAncestryLookup readAncestryFacts)
    {
        return processId == protectedProcessId ||
            IsDescendantOf(protectedProcessId, processId, readAncestryFacts);
    }

    private static bool CanKillProcess(int processId, bool allowProtectedDescendant)
    {
        if (processId == Environment.ProcessId ||
            IsDescendantOf(Environment.ProcessId, processId))
        {
            return false;
        }

        if (IsProtectedProcess(processId) || ProtectedPidIsDescendantOf(processId))
        {
            return false;
        }

        return allowProtectedDescendant || !IsProtectedDescendant(processId);
    }

    private static bool IsProtectedProcess(int processId)
    {
        return TryGetProtectedPid(out var protectedPid) && processId == protectedPid;
    }

    private static bool IsProtectedDescendant(int processId)
    {
        return TryGetProtectedPid(out var protectedPid) && IsDescendantOf(processId, protectedPid);
    }

    private static bool ProtectedPidIsDescendantOf(int processId)
    {
        return TryGetProtectedPid(out var protectedPid) && IsDescendantOf(protectedPid, processId);
    }

    private static bool TryGetProtectedPid(out int processId)
    {
        var raw = Environment.GetEnvironmentVariable(ProtectedPidVariable);
        return int.TryParse(
            raw,
            System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture,
            out processId) &&
            processId > 0;
    }

    private static bool IsProcessRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static bool IsDescendantOf(int processId, int ancestorProcessId)
    {
        return OperatingSystem.IsWindows() &&
            IsDescendantOf(processId, ancestorProcessId, ReadProcessAncestryFacts);
    }

    // Walks parent pids from processId looking for ancestorProcessId. A hop is credited only when the
    // parent pid still names a live process that started earlier than the child it claims; anything
    // that cannot be verified ends the walk as "not an ancestor". Without that check a pid Windows has
    // just handed to a freshly spawned child is indistinguishable from a long-dead ancestor whose pid
    // still sits in its children's parent-pid records.
    internal static bool IsDescendantOf(
        int processId,
        int ancestorProcessId,
        ProcessAncestryLookup readAncestryFacts)
    {
        ArgumentNullException.ThrowIfNull(readAncestryFacts);
        if (processId <= 0 ||
            ancestorProcessId <= 0 ||
            !readAncestryFacts(processId, out var child))
        {
            return false;
        }

        var childProcessId = processId;
        for (var i = 0; i < 64; i++)
        {
            var parentProcessId = child.ParentProcessId;
            if (parentProcessId <= 0 || parentProcessId == childProcessId)
            {
                return false;
            }

            if (!readAncestryFacts(parentProcessId, out var parent) ||
                parent.StartTimeUtc >= child.StartTimeUtc)
            {
                return false;
            }

            if (parentProcessId == ancestorProcessId)
            {
                return true;
            }

            childProcessId = parentProcessId;
            child = parent;
        }

        return false;
    }

    // The production lookup: Toolhelp32 for the parent pid, Process.StartTime for liveness plus ordering.
    internal static ProcessAncestryLookup ReadProcessAncestryFactsForTests => ReadProcessAncestryFacts;

    private static bool ReadProcessAncestryFacts(int processId, out ProcessAncestryFacts facts)
    {
        facts = default;
        if (!TryGetParentProcessId(processId, out var parentProcessId) ||
            !TryGetProcessStartTimeUtc(processId, out var startTimeUtc))
        {
            return false;
        }

        facts = new ProcessAncestryFacts(parentProcessId, startTimeUtc);
        return true;
    }

    private static bool TryGetProcessStartTimeUtc(int processId, out DateTime startTimeUtc)
    {
        startTimeUtc = default;
        try
        {
            using var process = Process.GetProcessById(processId);
            if (process.HasExited)
            {
                return false;
            }

            startTimeUtc = process.StartTime.ToUniversalTime();
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }

    private static bool TryGetParentProcessId(int processId, out int parentProcessId)
    {
        parentProcessId = 0;
        var snapshot = CreateToolhelp32Snapshot(0x00000002, 0);
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1))
        {
            return false;
        }

        try
        {
            var entry = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
            if (!Process32First(snapshot, ref entry))
            {
                return false;
            }

            do
            {
                if (entry.th32ProcessID == (uint)processId)
                {
                    parentProcessId = (int)entry.th32ParentProcessID;
                    return parentProcessId > 0;
                }
            }
            while (Process32Next(snapshot, ref entry));
        }
        finally
        {
            CloseHandle(snapshot);
        }

        return false;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool Process32First(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool Process32Next(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESSENTRY32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }
}
