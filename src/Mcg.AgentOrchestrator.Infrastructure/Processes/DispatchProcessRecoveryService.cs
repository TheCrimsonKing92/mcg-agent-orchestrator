using System.Diagnostics;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal enum DispatchProcessVerdictKind
{
    Live,
    Hold,
    CompletedFromExitFile,
    HungWrapper,
    SuspectedHang,
    ProcessGone
}

internal sealed record DispatchProcessRefreshVerdict(
    DispatchProcessVerdictKind Kind,
    DispatchRecoveryDecision RecoveryDecision,
    DispatchWorktreeInspectionStatus WorktreeInspectionStatus,
    int? ExitCode = null,
    string Diagnostic = "",
    DispatchRecoveryAction HangRecoveryAction = DispatchRecoveryAction.Hold,
    TaskProcessResourceAccounting? ResourceAccounting = null);

internal sealed record DispatchHeartbeat(
    int ProcessId,
    int? ChildProcessId,
    string State,
    DateTimeOffset LastObservedAt,
    DateTimeOffset LastProgressAt,
    long StandardOutputBytes,
    long StandardErrorBytes,
    long? OwnedCpuMs = null,
    IReadOnlyList<int>? OwnedProcessIds = null,
    string? ProviderSessionId = null,
    string? WorktreeHeadSha = null,
    string? DirtyStateHash = null,
    IReadOnlyList<SpawnProcessIdentity>? OwnedProcessIdentities = null)
{
    public static DispatchHeartbeat Empty { get; } =
        new(0, null, "unknown", DateTimeOffset.MinValue, DateTimeOffset.MinValue, 0, 0);
}

internal sealed class DispatchProcessRecoveryService
{
    private const long CpuStartupBurstMs = 1000L;
    private readonly IClock _clock;
    private readonly TimeSpan _postOutputIdleTimeout;
    private readonly TimeSpan _progressStallTimeout;
    private readonly TimeSpan _startupHangTimeout;
    private readonly Func<int, bool> _isStillRunning;
    private readonly Func<int, bool> _tryKillOwnedProcess;
    private readonly Func<string, bool> _fileExists;
    private readonly Func<string, Stream> _openArtifactReadStream;
    private readonly Func<string, DateTimeOffset> _getLastWriteTimeUtc;
    private readonly Func<string, long> _getFileLength;
    private readonly Func<int, long?> _getPeakMemoryBytes;
    private readonly Func<int, SpawnProcessIdentity?> _readProcessIdentity;
    private readonly Func<string, ExitCodeReadResult> _readExitArtifact;
    private readonly Action<string, int, string> _writeExitArtifact;
    private readonly Func<TaskProcessRecord, bool, int, DispatchWorktreeInspectionStatus, DispatchRecoveryDecision> _evaluateRecovery;
    private readonly IDispatchDiagnosticWriter _diagnosticWriter;

    internal DispatchProcessRecoveryService(
        IClock? clock = null,
        TimeSpan? postOutputIdleTimeout = null,
        TimeSpan? progressStallTimeout = null,
        TimeSpan? startupHangTimeout = null,
        Func<int, bool>? isStillRunning = null,
        Func<int, bool>? tryKillOwnedProcess = null,
        Func<string, bool>? fileExists = null,
        Func<string, Stream>? openArtifactReadStream = null,
        Func<string, DateTimeOffset>? getLastWriteTimeUtc = null,
        Func<string, long>? getFileLength = null,
        Func<int, long?>? getPeakMemoryBytes = null,
        Func<string, ExitCodeReadResult>? readExitArtifact = null,
        Action<string, int, string>? writeExitArtifact = null,
        Func<TaskProcessRecord, bool, int, DispatchWorktreeInspectionStatus, DispatchRecoveryDecision>? evaluateRecovery = null,
        IDispatchDiagnosticWriter? diagnosticWriter = null,
        Func<int, SpawnProcessIdentity?>? readProcessIdentity = null)
    {
        _clock = clock ?? new SystemClock();
        _postOutputIdleTimeout = postOutputIdleTimeout ?? TimeSpan.FromMinutes(2);
        _progressStallTimeout = progressStallTimeout ?? TimeSpan.FromMinutes(15);
        _startupHangTimeout = startupHangTimeout ?? TimeSpan.FromSeconds(120);
        _isStillRunning = isStillRunning ?? IsStillRunning;
        _tryKillOwnedProcess = tryKillOwnedProcess ?? TryKillProcess;
        _fileExists = fileExists ?? File.Exists;
        _openArtifactReadStream = openArtifactReadStream ??
            (path => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
        _getLastWriteTimeUtc = getLastWriteTimeUtc ??
            (path => new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero));
        _getFileLength = getFileLength ?? (path => new FileInfo(path).Length);
        _getPeakMemoryBytes = getPeakMemoryBytes ?? ReadPeakMemoryBytes;
        _readProcessIdentity = readProcessIdentity ?? DispatchProcessIdentityEvidence.ReadCurrent;
        _readExitArtifact = readExitArtifact ?? DispatchExitArtifactReader.Read;
        _writeExitArtifact = writeExitArtifact ?? WriteExitArtifactBestEffort;
        var recoveryPolicy = new DispatchRecoveryPolicy(_clock);
        _evaluateRecovery = evaluateRecovery ?? recoveryPolicy.Evaluate;
        _diagnosticWriter = diagnosticWriter ?? new FileDiagnosticWriter();
    }

    internal DispatchProcessRefreshVerdict ClassifyRefresh(
        TaskSpec task,
        GoalId goalId,
        TaskProcessRecord processRecord,
        int staleRetryBudgetRemaining,
        bool usesCodexExitFileBehavior,
        bool requiresFileChangeEvidence,
        Func<DispatchWorktreeInspectionStatus> inspectWorktree,
        Func<bool> hasCodexFinalOutput,
        Func<bool> hasWorktreeProgress,
        Action<DispatchHeartbeat?> heartbeatObserved)
    {
        var observedHeartbeat = TryReadHeartbeat(GetHeartbeatPath(processRecord), out var refreshHeartbeat)
            ? refreshHeartbeat
            : null;
        var hasLiveTrackedProcess = AnyTrackedProcessStillRunning(processRecord);
        heartbeatObserved(observedHeartbeat);
        // A missing or temporarily unreadable identity cannot authorize ownership,
        // completion blocking, or termination. It also cannot prove that a launch-time
        // tracked process is dead while terminal exit evidence is still absent.
        var hasLiveProcess = hasLiveTrackedProcess ||
            AnyObservedProcessStillRunning(processRecord, observedHeartbeat);
        var worktreeInspectionStatus = DispatchWorktreeInspectionStatus.NotRequired;
        if (!hasLiveProcess &&
            !_fileExists(processRecord.ExitCodePath) &&
            task.LastDispatch is not null &&
            requiresFileChangeEvidence)
        {
            worktreeInspectionStatus = inspectWorktree();
        }

        var recoveryDecision = _evaluateRecovery(
            processRecord,
            hasLiveProcess,
            staleRetryBudgetRemaining,
            worktreeInspectionStatus);
        var exitFileExists = _fileExists(processRecord.ExitCodePath);
        if (TryClassifyExitFile(
                processRecord,
                recoveryDecision,
                worktreeInspectionStatus,
                heartbeatObserved,
                out var exitVerdict))
        {
            return exitVerdict;
        }

        if (hasLiveProcess)
        {
            if (!hasLiveTrackedProcess && !exitFileExists)
            {
                return Live(recoveryDecision, worktreeInspectionStatus);
            }

            if (IsAuthoritativeHold(recoveryDecision))
            {
                return Hold(recoveryDecision, worktreeInspectionStatus);
            }

            if (TryDetectHungCodexWrapper(
                    processRecord,
                    usesCodexExitFileBehavior,
                    hasCodexFinalOutput,
                    out var diagnostic))
            {
                return new DispatchProcessRefreshVerdict(
                    DispatchProcessVerdictKind.HungWrapper,
                    recoveryDecision,
                    worktreeInspectionStatus,
                    Diagnostic: diagnostic);
            }

            if (TryDetectHungSubscriptionWrapper(
                    processRecord,
                    usesCodexExitFileBehavior,
                    out var wrapperDiagnostic))
            {
                return new DispatchProcessRefreshVerdict(
                    DispatchProcessVerdictKind.HungWrapper,
                    recoveryDecision,
                    worktreeInspectionStatus,
                    Diagnostic: wrapperDiagnostic);
            }

            if (TryDetectStartupHang(processRecord, out var startupHangDiagnostic))
            {
                return new DispatchProcessRefreshVerdict(
                    DispatchProcessVerdictKind.SuspectedHang,
                    recoveryDecision,
                    worktreeInspectionStatus,
                    Diagnostic: startupHangDiagnostic,
                    HangRecoveryAction: DispatchRecoveryAction.Reap);
            }

            if (TryDetectProbableProgressStall(
                    processRecord,
                    requiresFileChangeEvidence,
                    hasWorktreeProgress,
                    out var stallDiagnostic))
            {
                return new DispatchProcessRefreshVerdict(
                    DispatchProcessVerdictKind.SuspectedHang,
                    recoveryDecision,
                    worktreeInspectionStatus,
                    Diagnostic: stallDiagnostic,
                    HangRecoveryAction: DispatchRecoveryAction.ClassifyBlocker);
            }

            return Live(recoveryDecision, worktreeInspectionStatus);
        }

        if (exitFileExists)
        {
            return Hold(recoveryDecision, worktreeInspectionStatus);
        }

        var resourceAccounting = ReapTrackedProcessJobs(processRecord, waitForExit: false);
        return new DispatchProcessRefreshVerdict(
            DispatchProcessVerdictKind.ProcessGone,
            recoveryDecision,
            worktreeInspectionStatus,
            ResourceAccounting: resourceAccounting);
    }

    private bool TryClassifyExitFile(
        TaskProcessRecord processRecord,
        DispatchRecoveryDecision recoveryDecision,
        DispatchWorktreeInspectionStatus worktreeInspectionStatus,
        Action<DispatchHeartbeat?> heartbeatObserved,
        out DispatchProcessRefreshVerdict verdict)
    {
        var hasHeartbeat = TryReadHeartbeat(GetHeartbeatPath(processRecord), out var heartbeat);
        var observedHeartbeat = hasHeartbeat ? heartbeat : null;
        heartbeatObserved(observedHeartbeat);
        var exitRead = _readExitArtifact(processRecord.ExitCodePath);
        if (exitRead.Kind == ExitCodeReadKind.Valid)
        {
            if (AnyOwnedWorkerProcessStillRunning(processRecord, observedHeartbeat))
            {
                verdict = Live(recoveryDecision, worktreeInspectionStatus);
                return false;
            }

        }
        else
        {
            var hasLiveObservedProcess = AnyObservedProcessStillRunning(processRecord, observedHeartbeat);
            if (exitRead.Kind == ExitCodeReadKind.Missing || hasLiveObservedProcess)
            {
                verdict = Live(recoveryDecision, worktreeInspectionStatus);
                return false;
            }

            exitRead = ReadExitCodeWithRetry(processRecord.ExitCodePath);
            if (exitRead.Kind != ExitCodeReadKind.Valid)
            {
                verdict = Hold(BuildExitArtifactHold(processRecord, exitRead), worktreeInspectionStatus);
                return true;
            }
        }

        var exitCode = exitRead.ExitCode!.Value;
        if (AnyOwnedWorkerProcessStillRunning(processRecord, observedHeartbeat))
        {
            verdict = Live(recoveryDecision, worktreeInspectionStatus);
            return false;
        }

        verdict = new DispatchProcessRefreshVerdict(
            DispatchProcessVerdictKind.CompletedFromExitFile,
            recoveryDecision,
            worktreeInspectionStatus,
            ExitCode: exitCode,
            Diagnostic: BuildRecoveryDiagnostic(recoveryDecision));
        return true;
    }

    private static DispatchRecoveryDecision BuildExitArtifactHold(
        TaskProcessRecord processRecord,
        ExitCodeReadResult exitRead)
    {
        var kind = exitRead.Kind.ToString().ToLowerInvariant();
        return new DispatchRecoveryDecision(
            DispatchRecoveryAction.Hold,
            DispatchRecoveryPolicy.ToActionName(DispatchRecoveryAction.Hold),
            processRecord.ExitCodePath,
            $"exit artifact unavailable; state={exitRead.Kind}; evidence={exitRead.Evidence}",
            $"exit-artifact-{kind}");
    }

    internal bool TryCompleteFromExitFile(
        TaskProcessRecord processRecord,
        DispatchRecoveryDecision recoveryDecision,
        Action<DispatchHeartbeat?> heartbeatObserved,
        out DispatchProcessRefreshVerdict verdict) =>
        TryClassifyExitFile(
            processRecord,
            recoveryDecision,
            DispatchWorktreeInspectionStatus.NotRequired,
            heartbeatObserved,
            out verdict);

    private static DispatchProcessRefreshVerdict Live(
        DispatchRecoveryDecision recoveryDecision,
        DispatchWorktreeInspectionStatus worktreeInspectionStatus) =>
        new(DispatchProcessVerdictKind.Live, recoveryDecision, worktreeInspectionStatus);

    private static DispatchProcessRefreshVerdict Hold(
        DispatchRecoveryDecision recoveryDecision,
        DispatchWorktreeInspectionStatus worktreeInspectionStatus) =>
        new(DispatchProcessVerdictKind.Hold, recoveryDecision, worktreeInspectionStatus);

    private bool TryDetectHungCodexWrapper(
        TaskProcessRecord processRecord,
        bool usesCodexExitFileBehavior,
        Func<bool> hasCodexFinalOutput,
        out string diagnostic)
    {
        diagnostic = string.Empty;
        if (!usesCodexExitFileBehavior || _fileExists(processRecord.ExitCodePath))
        {
            return false;
        }

        var lastOutputAt = GetLastOutputWriteTime(processRecord);
        var idleFor = _clock.UtcNow - lastOutputAt;
        if (idleFor < _postOutputIdleTimeout || !hasCodexFinalOutput())
        {
            return false;
        }

        diagnostic = $"Background dispatch wrapper appears hung after codex final output; no exit file was written after {FormatDuration(idleFor)} of idle logs. Marking dispatch failed with captured stdout/stderr evidence.";
        return true;
    }

    private bool TryDetectHungSubscriptionWrapper(
        TaskProcessRecord processRecord,
        bool usesCodexExitFileBehavior,
        out string diagnostic)
    {
        diagnostic = string.Empty;
        if (usesCodexExitFileBehavior || _fileExists(processRecord.ExitCodePath) ||
            !TryReadHeartbeat(GetHeartbeatPath(processRecord), out var heartbeat) ||
            heartbeat.ChildProcessId is not null)
        {
            return false;
        }

        var idleFor = _clock.UtcNow - heartbeat.LastProgressAt;
        if (idleFor < _postOutputIdleTimeout)
        {
            return false;
        }

        diagnostic =
            $"Background dispatch wrapper appears hung with stalled heartbeat for {FormatDuration(idleFor)}; no exit file was written. " +
            "Marking dispatch based on role completion evidence.";
        return true;
    }

    private bool TryDetectStartupHang(TaskProcessRecord processRecord, out string diagnostic)
    {
        diagnostic = string.Empty;
        if (_fileExists(processRecord.ExitCodePath) ||
            !TryReadHeartbeat(GetHeartbeatPath(processRecord), out var heartbeat))
        {
            return false;
        }

        var hasLiveChild = heartbeat.ChildProcessId is not null;
        var consumedStartupBurst = (heartbeat.OwnedCpuMs ?? 0L) > CpuStartupBurstMs;
        var producedOutput = heartbeat.StandardOutputBytes + heartbeat.StandardErrorBytes > 0L;
        if (hasLiveChild || consumedStartupBurst || producedOutput)
        {
            return false;
        }

        var aliveFor = _clock.UtcNow - processRecord.StartedAt;
        if (aliveFor < _startupHangTimeout)
        {
            return false;
        }

        diagnostic =
            $"Background dispatch never launched its tool process: childPid=null, " +
            $"ownedCpuMs={heartbeat.OwnedCpuMs}, stdout_bytes={heartbeat.StandardOutputBytes}, " +
            $"stderr_bytes={heartbeat.StandardErrorBytes}, alive_for={FormatDuration(aliveFor)}, " +
            $"startup_hang_timeout={FormatDuration(_startupHangTimeout)}. " +
            "No child process, startup CPU burst, or output since start; process tree killed and dispatch marked failed.";
        return true;
    }

    private bool TryDetectProbableProgressStall(
        TaskProcessRecord processRecord,
        bool requiresFileChangeEvidence,
        Func<bool> hasWorktreeProgress,
        out string diagnostic)
    {
        diagnostic = string.Empty;
        if (_fileExists(processRecord.ExitCodePath) ||
            !TryReadHeartbeat(GetHeartbeatPath(processRecord), out var heartbeat))
        {
            return false;
        }

        var idleFor = _clock.UtcNow - heartbeat.LastProgressAt;
        if (idleFor < _progressStallTimeout ||
            (requiresFileChangeEvidence && hasWorktreeProgress()))
        {
            return false;
        }

        var observedFor = _clock.UtcNow - heartbeat.LastObservedAt;
        diagnostic =
            "Background dispatch made no observable progress before the stall timeout; " +
            $"wrapper heartbeat state={heartbeat.State}, pid={heartbeat.ProcessId}, child_pid={heartbeat.ChildProcessId?.ToString() ?? "unknown"}, " +
            $"stdout_bytes={heartbeat.StandardOutputBytes}, stderr_bytes={heartbeat.StandardErrorBytes}, " +
            $"last_progress={heartbeat.LastProgressAt:u}, last_observed={heartbeat.LastObservedAt:u}, " +
            $"idle_for={FormatDuration(idleFor)}, heartbeat_age={FormatDuration(observedFor)}. " +
            "Wrapper process reaped and dispatch marked failed with captured stdout/stderr evidence.";
        return true;
    }

    internal static string GetHeartbeatPath(TaskProcessRecord processRecord)
    {
        const string exitSuffix = ".exit.txt";
        var directory = Path.GetDirectoryName(processRecord.ExitCodePath) ?? string.Empty;
        var fileName = Path.GetFileName(processRecord.ExitCodePath);
        return fileName.EndsWith(exitSuffix, StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(directory, fileName[..^exitSuffix.Length] + ".heartbeat.json")
            : processRecord.ExitCodePath + ".heartbeat.json";
    }

    internal bool TryReadHeartbeat(string path, out DispatchHeartbeat heartbeat)
    {
        heartbeat = DispatchHeartbeat.Empty;
        if (!_fileExists(path))
        {
            return false;
        }

        try
        {
            using var stream = _openArtifactReadStream(path);
            using var document = JsonDocument.Parse(stream);
            var root = document.RootElement;
            if (!TryGetDateTimeOffset(root, "lastObservedAt", out var lastObservedAt) ||
                !TryGetDateTimeOffset(root, "lastProgressAt", out var lastProgressAt))
            {
                return false;
            }

            heartbeat = new DispatchHeartbeat(
                GetInt32(root, "pid"),
                GetNullableInt32(root, "childPid"),
                GetString(root, "state"),
                lastObservedAt,
                lastProgressAt,
                GetInt64(root, "stdoutBytes"),
                GetInt64(root, "stderrBytes"),
                GetNullableInt64(root, "ownedCpuMs"),
                GetInt32Array(root, "ownedPids"),
                GetNullableString(root, "providerSessionId"),
                GetNullableString(root, "worktreeHeadSha"),
                GetNullableString(root, "dirtyStateHash"),
                DispatchProcessIdentityEvidence.Read(root));
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal ExitCodeReadResult ReadExitCodeWithRetry(string path)
    {
        const int attempts = 3;
        var result = _readExitArtifact(path);
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            if (result.Kind == ExitCodeReadKind.Valid)
            {
                return result;
            }

            if (attempt < attempts - 1)
            {
                Thread.Sleep(TimeSpan.FromMilliseconds(50));
                result = _readExitArtifact(path);
            }
        }

        return result;
    }

    internal static string BuildRecoveryDiagnostic(DispatchRecoveryDecision? decision)
    {
        if (decision is null)
        {
            return string.Empty;
        }

        var blocker = string.IsNullOrWhiteSpace(decision.Blocker)
            ? string.Empty
            : $" blocker='{decision.Blocker}'";
        return $"Dispatch recovery policy action='{decision.ActionName}' evidence='{decision.EvidencePath}' reason='{decision.Reason}'{blocker}.";
    }

    internal static DispatchRecoveryDecision WithAction(
        DispatchRecoveryAction action,
        DispatchRecoveryDecision basis,
        string reason) =>
        new(
            action,
            DispatchRecoveryPolicy.ToActionName(action),
            basis.EvidencePath,
            string.IsNullOrWhiteSpace(reason) ? basis.Reason : reason,
            basis.Blocker);

    private static bool IsAuthoritativeHold(DispatchRecoveryDecision decision) =>
        decision.Action == DispatchRecoveryAction.Hold &&
        (decision.Reason.Contains("recent heartbeat", StringComparison.OrdinalIgnoreCase) ||
         decision.Reason.Contains("CPU activity", StringComparison.OrdinalIgnoreCase) ||
         decision.Reason.Contains("output progress", StringComparison.OrdinalIgnoreCase));

    private DateTimeOffset GetLastOutputWriteTime(TaskProcessRecord processRecord)
    {
        var newest = processRecord.StartedAt;
        foreach (var path in new[] { processRecord.StandardOutputPath, processRecord.StandardErrorPath })
        {
            if (!_fileExists(path))
            {
                continue;
            }

            var lastWrite = _getLastWriteTimeUtc(path);
            if (lastWrite > newest)
            {
                newest = lastWrite;
            }
        }

        return newest;
    }

    private static string FormatDuration(TimeSpan duration) =>
        duration < TimeSpan.Zero ? TimeSpan.Zero.ToString("c") : duration.ToString("c");

    internal void TryWriteExitCode(string path, int exitCode, string reason) =>
        _writeExitArtifact(path, exitCode, reason);

    internal bool AnyTrackedProcessStillRunning(TaskProcessRecord processRecord) =>
        processRecord.CompletionTrackedProcessIds.Any(_isStillRunning);

    private bool AnyObservedProcessStillRunning(TaskProcessRecord processRecord, DispatchHeartbeat? heartbeat) =>
        GetObservedProcessIds(processRecord, heartbeat).Any(processId =>
            _isStillRunning(processId) &&
            DispatchProcessIdentityEvidence.IsRecordedOwner(
                processId,
                heartbeat?.OwnedProcessIdentities,
                _readProcessIdentity));

    private bool AnyOwnedWorkerProcessStillRunning(TaskProcessRecord processRecord, DispatchHeartbeat? heartbeat) =>
        GetOwnedWorkerProcessIds(processRecord, heartbeat).Any(processId =>
            _isStillRunning(processId) &&
            DispatchProcessIdentityEvidence.IsRecordedOwner(
                processId,
                heartbeat?.OwnedProcessIdentities,
                _readProcessIdentity));

    private static IReadOnlyList<int> GetObservedProcessIds(TaskProcessRecord processRecord, DispatchHeartbeat? heartbeat)
    {
        var processIds = new HashSet<int>(processRecord.CompletionTrackedProcessIds.Where(pid => pid > 0));
        if (heartbeat is not null)
        {
            if (heartbeat.ChildProcessId is > 0)
            {
                processIds.Add(heartbeat.ChildProcessId.Value);
            }

            if (heartbeat.OwnedProcessIds is { Count: > 0 })
            {
                foreach (var processId in heartbeat.OwnedProcessIds.Where(pid => pid > 0))
                {
                    processIds.Add(processId);
                }
            }
        }

        return processIds.ToArray();
    }

    private static IReadOnlyList<int> GetOwnedWorkerProcessIds(TaskProcessRecord processRecord, DispatchHeartbeat? heartbeat)
    {
        var processIds = new HashSet<int>();
        if (processRecord.OwnedProcessIds is { Count: > 0 })
        {
            foreach (var processId in processRecord.OwnedProcessIds.Where(pid =>
                         pid > 0 &&
                         (processRecord.NonBlockingProcessIds is not { Count: > 0 } ||
                          !processRecord.NonBlockingProcessIds.Contains(pid))))
            {
                processIds.Add(processId);
            }
        }

        if (heartbeat is not null)
        {
            if (heartbeat.ProcessId > 0)
            {
                processIds.Add(heartbeat.ProcessId);
            }

            if (heartbeat.ChildProcessId is > 0)
            {
                processIds.Add(heartbeat.ChildProcessId.Value);
            }

            if (heartbeat.OwnedProcessIds is { Count: > 0 })
            {
                foreach (var processId in heartbeat.OwnedProcessIds.Where(pid => pid > 0))
                {
                    processIds.Add(processId);
                }
            }
        }

        return processIds.ToArray();
    }

    internal void TryKillTrackedProcesses(
        TaskProcessRecord processRecord,
        bool waitForExit,
        bool bypassTrackedJobRegistry = false)
    {
        foreach (var processId in processRecord.TrackedProcessIds.Distinct())
        {
            if (bypassTrackedJobRegistry)
            {
                WorkerProcessJobs.TryKillOrFallbackWithoutRegistry(processId);
            }
            else
            {
                _tryKillOwnedProcess(processId);
            }

            if (waitForExit)
            {
                WaitForTrackedProcessExit(processId);
            }
        }
    }

    internal TaskProcessResourceAccounting? ReleaseTrackedProcessJobs(TaskProcessRecord processRecord)
    {
        var preReleaseSnapshot = SnapshotTrackedProcessAccounting(processRecord);
        long cpuMilliseconds = 0;
        long peakMemoryBytes = 0;
        long ioBytes = 0;
        var capturedAny = false;
        var accountingSource = "live";

        foreach (var processId in processRecord.TrackedProcessIds.Distinct())
        {
            WorkerProcessJobs.Release(processId, out var accounting);
            if (accounting is null)
            {
                continue;
            }

            cpuMilliseconds = SaturatingAdd(cpuMilliseconds, accounting.CpuMilliseconds);
            peakMemoryBytes = Math.Max(peakMemoryBytes, accounting.PeakMemoryBytes);
            ioBytes = SaturatingAdd(ioBytes, accounting.IoBytes);
            accountingSource = capturedAny
                ? MergeAccountingSource(accountingSource, accounting.AccountingSource)
                : accounting.AccountingSource;
            capturedAny = true;
        }

        return capturedAny
            ? new TaskProcessResourceAccounting(cpuMilliseconds, peakMemoryBytes, ioBytes, AccountingSource: accountingSource)
            : preReleaseSnapshot;
    }

    internal TaskProcessResourceAccounting? ReapTrackedProcessJobs(TaskProcessRecord processRecord, bool waitForExit)
    {
        var preReapSnapshot = SnapshotTrackedProcessAccounting(processRecord);
        TaskProcessResourceAccounting? jobAccounting = null;
        foreach (var processId in processRecord.TrackedProcessIds.Distinct())
        {
            WorkerProcessJobs.Reap(processId, waitForExit ? WaitForTrackedProcessExit : null, out var accounting);
            if (accounting is not null)
            {
                jobAccounting = MergeResourceAccounting(
                    jobAccounting,
                    new TaskProcessResourceAccounting(
                        accounting.CpuMilliseconds,
                        accounting.PeakMemoryBytes,
                        accounting.IoBytes,
                        Reaped: true,
                        AccountingSource: accounting.AccountingSource));
                continue;
            }

            _tryKillOwnedProcess(processId);
            if (waitForExit)
            {
                WaitForTrackedProcessExit(processId);
            }
        }

        return jobAccounting ?? (preReapSnapshot is null ? null : preReapSnapshot with { Reaped = true });
    }

    internal TaskProcessResourceAccounting? SnapshotTrackedProcessAccounting(TaskProcessRecord processRecord)
    {
        if (!TryReadHeartbeat(GetHeartbeatPath(processRecord), out var heartbeat))
        {
            return null;
        }

        var peakMemoryBytes = 0L;
        foreach (var processId in processRecord.TrackedProcessIds.Distinct())
        {
            peakMemoryBytes = Math.Max(peakMemoryBytes, _getPeakMemoryBytes(processId) ?? 0L);
        }

        if (heartbeat.OwnedCpuMs is null && peakMemoryBytes <= 0)
        {
            return null;
        }

        return new TaskProcessResourceAccounting(
            Math.Max(0L, heartbeat.OwnedCpuMs ?? 0L),
            peakMemoryBytes,
            0L,
            AccountingSource: "snapshot");
    }

    internal static bool IsDispatchHostReapCompletion(string standardError) =>
        standardError.Contains("[dispatch-host] terminating worker tree:", StringComparison.Ordinal);

    internal void TryWriteDiagnosticRecord(
        GoalId goalId,
        TaskId taskId,
        TaskSpec task,
        TaskProcessRecord processRecord,
        int exitCode,
        string standardOutput,
        string standardError,
        ProcessCommandLineSnapshot commandLineSnapshot)
    {
        try
        {
            const string exitSuffix = ".exit.txt";
            var fn = Path.GetFileName(processRecord.ExitCodePath);
            var prefix = fn.EndsWith(exitSuffix, StringComparison.OrdinalIgnoreCase)
                ? fn[..^exitSuffix.Length]
                : fn;
            var outputPath = processRecord.StandardOutputPath;
            var fileExists = _fileExists(outputPath);
            var fileLen = fileExists ? _getFileLength(outputPath) : 0L;
            var readLen = (long)standardOutput.Length;
            var stderrPath = processRecord.StandardErrorPath;
            var stderrLen = _fileExists(stderrPath) ? _getFileLength(stderrPath) : 0L;
            var classification = ClassifyDispatch(
                exitCode, fileLen, readLen, stderrLen, standardOutput, standardError, out var reason);
            var dispatchState = new DispatchStateSurface(
                _clock,
                _isStillRunning,
                readProcessIdentity: processId =>
                    commandLineSnapshot.TryGetRecord(processId, out var record) &&
                    record.Status == ProcessInspectionStatus.Available &&
                    record.StartedAt is { } startedAt &&
                    !string.IsNullOrWhiteSpace(record.ExecutablePath)
                        ? (startedAt, record.ExecutablePath)
                        : null).Evaluate(goalId, task, commandLineSnapshot);
            var record = new DispatchDiagnosticRecord(
                goalId.Value,
                taskId.Value,
                prefix,
                exitCode,
                outputPath,
                fileExists,
                fileLen,
                readLen,
                stderrLen,
                classification,
                reason,
                _clock.UtcNow.ToString("O"),
                dispatchState);
            _diagnosticWriter.WriteRecord(record);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[DispatchDiagnostic] Failed to record dispatch diagnostic: {ex.Message}");
        }
    }

    private static string ClassifyDispatch(
        int exitCode,
        long fileLen,
        long readLen,
        long stderrLen,
        string standardOutput,
        string standardError,
        out string reason)
    {
        if (exitCode == 0 && fileLen > 0)
        {
            reason = $"exit 0 with {fileLen} bytes in output file";
            return "success";
        }

        if (exitCode != 0 && ProviderLimitEvidenceParser.TryGetEvidenceLine(
                standardOutput,
                standardError,
                out var providerLimitEvidence))
        {
            reason = $"provider limit evidence: {providerLimitEvidence}";
            return "rate-limited";
        }

        if (exitCode != 0 && fileLen == 0 && readLen == 0 && stderrLen == 0)
        {
            reason = "root exited non-zero with zero bytes on both redirected streams";
            return "launch-failure";
        }

        reason = exitCode == 0
            ? $"exit 0; fileLen={fileLen}; readLen={readLen}"
            : $"exit {exitCode}; fileLen={fileLen}; readLen={readLen}; stderrLen={stderrLen}";
        return exitCode == 0 ? "success" : "failed";
    }

    private static bool TryGetDateTimeOffset(JsonElement root, string propertyName, out DateTimeOffset value)
    {
        value = default;
        return root.TryGetProperty(propertyName, out var property) &&
            property.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(property.GetString(), out value);
    }

    private static string GetString(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? "unknown"
            : "unknown";

    private static string? GetNullableString(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var property) &&
        property.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(property.GetString())
            ? property.GetString()
            : null;

    private static int GetInt32(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var property) && property.TryGetInt32(out var value) ? value : 0;

    private static int? GetNullableInt32(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var property) || property.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return property.TryGetInt32(out var value) ? value : null;
    }

    private static long GetInt64(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var property) && property.TryGetInt64(out var value) ? value : 0;

    private static long? GetNullableInt64(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var property) || property.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return property.TryGetInt64(out var value) ? value : null;
    }

    private static List<int> GetInt32Array(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var values = new List<int>();
        foreach (var item in property.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out var value))
            {
                values.Add(value);
            }
        }

        return values;
    }

    private static TaskProcessResourceAccounting? MergeResourceAccounting(
        TaskProcessResourceAccounting? left,
        TaskProcessResourceAccounting? right)
    {
        if (left is null)
        {
            return right;
        }

        if (right is null)
        {
            return left;
        }

        return new TaskProcessResourceAccounting(
            Math.Max(left.CpuMilliseconds, right.CpuMilliseconds),
            Math.Max(left.PeakMemoryBytes, right.PeakMemoryBytes),
            Math.Max(left.IoBytes, right.IoBytes),
            left.Reaped || right.Reaped,
            MergeAccountingSource(left.AccountingSource, right.AccountingSource));
    }

    private void WaitForTrackedProcessExit(int processId)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (_isStillRunning(processId) && DateTimeOffset.UtcNow < deadline)
        {
            Thread.Sleep(100);
        }
    }

    private static string MergeAccountingSource(string left, string right)
    {
        if (string.Equals(left, right, StringComparison.Ordinal))
        {
            return left;
        }

        if (string.Equals(left, "snapshot", StringComparison.Ordinal))
        {
            return right;
        }

        if (string.Equals(right, "snapshot", StringComparison.Ordinal))
        {
            return left;
        }

        return "mixed";
    }

    private static long SaturatingAdd(long left, long right)
    {
        if (left < 0 || right < 0)
        {
            return Math.Max(left, right);
        }

        return long.MaxValue - left < right ? long.MaxValue : left + right;
    }

    private static bool IsStillRunning(int processId)
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
    }

    private static bool TryKillProcess(int processId) => WorkerProcessJobs.TryKillOrFallback(processId);

    private static long? ReadPeakMemoryBytes(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.HasExited ? null : Math.Max(process.WorkingSet64, process.PeakWorkingSet64);
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static void WriteExitArtifactBestEffort(string path, int exitCode, string reason)
    {
        try
        {
            DispatchExitArtifacts.Write(
                path,
                DispatchExitArtifacts.Synthetic(exitCode, reason, DateTimeOffset.UtcNow));
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
