using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Collections.Concurrent;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed record WorkerProcessJobAccounting(
    long CpuMilliseconds,
    long PeakMemoryBytes,
    long IoBytes,
    string AccountingSource = "live")
{
    public static WorkerProcessJobAccounting Empty { get; } = new(0, 0, 0);
}

public static class WorkerProcessJobs
{
    private const string ProtectedPidVariable = "MCG_ORCHESTRATOR_PROTECTED_PID";
    private static readonly ConcurrentDictionary<int, RegisteredJob> Jobs = new();
    private static SpawnRegistry? Registry;

    internal static Func<int, bool> TryKillPidTree { get; set; } = DefaultTryKillPidTree;

    public static void ConfigureRegistry(string dbPath)
    {
        Registry = new SpawnRegistry(dbPath);
    }

    public static int SweepStartupOrphans()
    {
        var registry = Registry;
        if (registry is null)
        {
            return 0;
        }

        var reaped = 0;
        var sweeper = BuildSweeperEvidence();
        foreach (var entry in registry.ListActive())
        {
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
        $"victim_image={entry.ImagePath} owner={entry.OwnerId} owner_pid={entry.OwnerProcessId?.ToString() ?? "unknown"} " +
        $"owner_started_at={entry.OwnerProcessStartedAt?.ToString("O") ?? "unknown"} " +
        $"owner_liveness={ownerLiveness} owner_evidence={SanitizeDiagnostic(ownerEvidence)} " +
        $"victim_evidence={SanitizeDiagnostic(victimEvidence ?? "not-evaluated")} {sweeper}";

    private static string BuildSweeperEvidence()
    {
        var argv = SanitizeDiagnostic(string.Join(' ', Environment.GetCommandLineArgs()));
        if (argv.Length > 1024)
        {
            argv = argv[..1024] + "...";
        }

        return $"sweeper_pid={Environment.ProcessId} sweeper_argv={argv}";
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
            static candidate => SpawnProcessIdentityReader.ReadForRegistration(candidate).Identity,
            static candidate => SpawnProcessIdentityReader.ReadForRegistration(candidate).Identity,
            out registrationFailure);
    }

    public static void RegisterOrThrow(Process process, string? ownerId = null)
    {
        if (!TryRegister(process, ownerId, out var registrationFailure))
        {
            throw new InvalidOperationException(registrationFailure);
        }
    }

    internal static bool TryRegister(
        Process process,
        string? ownerId,
        Func<Process, SpawnProcessIdentity?> readIdentity)
    {
        return TryRegisterCore(process, ownerId, readIdentity, readIdentity, out _);
    }

    internal static bool TryRegister(
        Process process,
        string? ownerId,
        Func<Process, SpawnProcessIdentity?> readVictimIdentity,
        Func<Process, SpawnProcessIdentity?> readOwnerIdentity)
    {
        return TryRegisterCore(process, ownerId, readVictimIdentity, readOwnerIdentity, out _);
    }

    private static bool TryRegisterCore(
        Process process,
        string? ownerId,
        Func<Process, SpawnProcessIdentity?> readVictimIdentity,
        Func<Process, SpawnProcessIdentity?> readOwnerIdentity,
        out string registrationFailure)
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
        if (Jobs.ContainsKey(process.Id))
        {
            registrationFailure = BuildRegistrationFailure(
                process.Id,
                "duplicate-or-recycled-pid",
                "process-tree-termination-requested");
            TryTerminateUnregisteredProcess(process);
            return false;
        }

        var registry = Registry;
        OwnedProcessGroup? group = null;
        Microsoft.Win32.SafeHandles.SafeFileHandle? duplicate = null;
        var failureStage = "owned-process-group-attachment";
        try
        {
            group = OwnedProcessGroup.Attach(process);
            SpawnProcessIdentity? victimIdentity = null;
            SpawnProcessIdentity? ownerIdentity = null;
            if (registry is not null)
            {
                failureStage = "victim-identity-read";
                victimIdentity = readVictimIdentity(process);
                if (victimIdentity is null)
                {
                    registrationFailure = BuildRegistrationFailure(
                        process.Id,
                        failureStage,
                        "attached-process-tree-termination-requested");
                    ReadAccountingAndDispose(group, kill: true, captureAccounting: false, out _);
                    group = null;
                    return false;
                }

                failureStage = "owner-identity-read";
                using var ownerProcess = Process.GetCurrentProcess();
                ownerIdentity = readOwnerIdentity(ownerProcess);
                if (ownerIdentity is null)
                {
                    registrationFailure = BuildRegistrationFailure(
                        process.Id,
                        failureStage,
                        "attached-process-tree-termination-requested");
                    ReadAccountingAndDispose(group, kill: true, captureAccounting: false, out _);
                    group = null;
                    return false;
                }
            }

            failureStage = "job-publication";
            duplicate = group.TryDuplicateAccountingHandle(out var duplicateHandle) ? duplicateHandle : null;
            var snapshot = group.TryReadAccounting(out var registrationAccounting)
                ? registrationAccounting with { AccountingSource = "snapshot" }
                : null;
            var registeredJob = new RegisteredJob(group, duplicate, snapshot);
            if (Jobs.TryAdd(process.Id, registeredJob))
            {
                group = null;
                duplicate = null;
                if (registry is null || RegisterDurable(registry, victimIdentity!, ownerIdentity!, ownerId))
                {
                    return true;
                }

                registrationFailure = BuildRegistrationFailure(
                    process.Id,
                    "durable-registry-write",
                    "registered-process-tree-termination-requested");
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

            registrationFailure = BuildRegistrationFailure(
                process.Id,
                "duplicate-or-recycled-pid",
                "attached-process-tree-termination-requested");
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
                $"process-tree-termination-requested; exception={ex.GetType().Name}");
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

    private static string BuildRegistrationFailure(int processId, string stage, string cleanup) =>
        $"worker-process-registration-failed: pid={processId.ToString(System.Globalization.CultureInfo.InvariantCulture)}; stage={stage}; cleanup={cleanup}";

    public static bool TryKillOrFallback(int processId)
    {
        return TryKillOrFallback(processId, allowProtectedDescendant: false, markRegistryReleased: true, out _);
    }

    public static bool TryKillOrFallback(int processId, out WorkerProcessJobAccounting? accounting)
    {
        return TryKillOrFallback(processId, allowProtectedDescendant: false, markRegistryReleased: true, out accounting);
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

        if (Jobs.TryRemove(processId, out var job))
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

        var fallbackKilled = TryKillPidTree(processId);
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
        if (Jobs.TryRemove(processId, out var job))
        {
            ReadAccountingAndDispose(job, kill: true, captureAccounting: true, preferDuplicate: false, out accounting);
        }

        Registry?.MarkReleased(processId, $"spawn_registry: released pid={processId}");
    }

    internal static void ReleaseWithoutAccounting(int processId)
    {
        if (Jobs.TryRemove(processId, out var job))
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
        if (!Jobs.TryRemove(processId, out var job))
        {
            Registry?.MarkReleased(processId, $"spawn_registry: released pid={processId}");
            return;
        }

        try
        {
            try
            {
                job.Group.Kill();
            }
            catch
            {
                // Reaping falls back to caller PID cleanup; duplicate accounting remains best-effort.
            }

            waitForExit?.Invoke(processId);

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
        accounting = null;
        if (job is null)
        {
            return !kill;
        }

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
                    OwnedProcessGroup.WaitForJobExit(job.DuplicateAccountingHandle, TimeSpan.FromSeconds(5));
                }
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

    internal static bool HasRegisteredJob(int processId) => Jobs.ContainsKey(processId);

    internal static IReadOnlyList<SpawnRegistryEntry> ListActiveRegistryEntriesForTests() =>
        Registry?.ListActive() ?? [];

    internal static void ClearRegistryForTests() => Registry = null;

    public static IReadOnlyList<int> ListLiveDescendantProcessIds(int ancestorProcessId)
    {
        if (ancestorProcessId <= 0 || !OperatingSystem.IsWindows())
        {
            return [];
        }

        var parentByProcessId = new Dictionary<int, int>();
        var snapshot = CreateToolhelp32Snapshot(0x00000002, 0);
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1))
        {
            return [];
        }

        try
        {
            var entry = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
            if (!Process32First(snapshot, ref entry))
            {
                return [];
            }

            do
            {
                var processId = (int)entry.th32ProcessID;
                var parentProcessId = (int)entry.th32ParentProcessID;
                if (processId > 0 && parentProcessId > 0)
                {
                    parentByProcessId[processId] = parentProcessId;
                }
            }
            while (Process32Next(snapshot, ref entry));
        }
        finally
        {
            CloseHandle(snapshot);
        }

        return parentByProcessId.Keys
            .Where(processId => processId != ancestorProcessId)
            .Where(processId => IsDescendantOf(processId, ancestorProcessId, parentByProcessId))
            .Where(IsProcessRunning)
            .OrderBy(processId => processId)
            .ToArray();
    }

    private static bool RegisterDurable(
        SpawnRegistry registry,
        SpawnProcessIdentity identity,
        SpawnProcessIdentity ownerIdentity,
        string? ownerId)
    {
        try
        {
            registry.Register(
                string.IsNullOrWhiteSpace(ownerId) ? $"pid:{identity.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture)}" : ownerId,
                identity,
                ownerIdentity);
            return true;
        }
        catch
        {
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

    private sealed record RegisteredJob(
        OwnedProcessGroup Group,
        Microsoft.Win32.SafeHandles.SafeFileHandle? DuplicateAccountingHandle,
        WorkerProcessJobAccounting? RegistrationSnapshot);

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

    private static bool IsProtectedProcessOrAncestor(int processId)
    {
        return IsProtectedProcess(processId) || ProtectedPidIsDescendantOf(processId);
    }

    private static bool CanKillProcess(int processId, bool allowProtectedDescendant)
    {
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
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        var current = processId;
        for (var i = 0; i < 64; i++)
        {
            if (!TryGetParentProcessId(current, out var parentProcessId))
            {
                return false;
            }

            if (parentProcessId == ancestorProcessId)
            {
                return true;
            }

            current = parentProcessId;
        }

        return false;
    }

    private static bool IsDescendantOf(int processId, int ancestorProcessId, IReadOnlyDictionary<int, int> parentByProcessId)
    {
        var current = processId;
        for (var i = 0; i < 64; i++)
        {
            if (!parentByProcessId.TryGetValue(current, out var parentProcessId))
            {
                return false;
            }

            if (parentProcessId == ancestorProcessId)
            {
                return true;
            }

            current = parentProcessId;
        }

        return false;
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
