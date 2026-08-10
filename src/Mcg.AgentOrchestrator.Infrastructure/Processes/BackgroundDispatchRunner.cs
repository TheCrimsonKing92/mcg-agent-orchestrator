using System.Diagnostics;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public enum DispatchRecordCheckpointPhase
{
    BeforeProcessStart,
    ProcessMayHaveStarted
}

public sealed record DispatchRefreshOutcome(
    TaskProcessRecord ProcessRecord,
    TaskVerificationRecord? Verification,
    string? ResultCommit = null,
    string? ResultCommitProvenance = null,
    DispatchRecoveryDecision? RecoveryDecision = null,
    ProviderFailureKind ProviderFailureKind = ProviderFailureKind.Unknown,
    DispatchDiagnosticPayload? DiagnosticPayload = null,
    DispatchAutoRequeueDisposition? AutoRequeueDisposition = null);

public sealed record DispatchDiagnosticPayload(
    int ExitCode,
    string StandardOutput,
    string StandardError);

public sealed record DispatchAutoRequeueDisposition(string EventName, string Message, bool ShouldRequeue = true);

public sealed record DispatchProcessStartResult(
    TaskProcessRecord? ProcessRecord,
    WorkerSandboxPrepRecoverableAction? RecoveryAction,
    bool RequeueSkipped = false,
    string? FailureReason = null)
{
    public static DispatchProcessStartResult Started(TaskProcessRecord processRecord) => new(processRecord, null);

    public static DispatchProcessStartResult RequiresRecovery(WorkerSandboxPrepRecoverableAction action) => new(null, action);

    public static DispatchProcessStartResult Skipped() => new(null, null, RequeueSkipped: true);

    public static DispatchProcessStartResult Failed(string reason) => new(null, null, FailureReason: reason);
}

public sealed record InterruptedDispatchStateRead(
    GoalStatus? GoalStatus,
    WorkTaskStatus? TaskStatus,
    string? UnreadableEntity = null,
    string? Error = null,
    bool WasTaskCancelledByConductor = false,
    bool WasTaskGracefullyDetachedByConductor = false)
{
    public bool IsReadable => UnreadableEntity is null;

    public static InterruptedDispatchStateRead Unreadable(string entity, string error) =>
        new(null, null, entity, error);
}

public sealed class BackgroundDispatchRunner
{
    private const int ApparatusHoldObservationsBeforeEscalation = 2;
    private const int AnsweredApparatusHoldObservationsBeforeFailure = 3;
    private const string ApparatusHoldReceiptPrefix = "DispatchApparatusHoldObserved:";
    public const string DisableDispatchStartVariable = "MCG_ORCHESTRATOR_DISABLE_DISPATCH_START";
    public const string TestRewriteRealWorkerCommandsVariable = "MCG_ORCHESTRATOR_TEST_REWRITE_REAL_WORKER_COMMANDS";

    private static readonly TimeSpan DefaultPostOutputIdleTimeout = TimeSpan.FromMinutes(2);
    // Reap a launched worker that has made no progress -- no output growth AND less than
    // CpuProgressEpsilonMs of CPU growth per heartbeat -- for this long. Cut from 20m to 15m to recover
    // faster from the known codex-CLI mid-session hang (openai/codex #7156/#7187: an established-but-
    // black-holed API request that never times out, so codex sits at ~0 CPU indefinitely). The CPU-growth
    // progress signal keeps a genuinely working codex fresh, so a full stall of this length is almost
    // certainly the hang; the remaining margin still tolerates a long, quiet build/test.
    private static readonly TimeSpan DefaultProgressStallTimeout = TimeSpan.FromMinutes(15);
    // A startup-hang is a worker whose tool process never launched. The childPid/CPU-burst "invoked"
    // check in TryDetectStartupHang makes this window safe to keep short: a worker that DID launch and
    // is merely idling on the provider API (low local CPU, buffered output) is never flagged, so this
    // only bounds how long a genuinely never-started tool may sit before it is reaped.
    private static readonly TimeSpan DefaultStartupHangTimeout = TimeSpan.FromSeconds(120);

    // ownedCpuMs above this means the tool consumed real CPU since start (it launched and ran), beyond
    // the bare pwsh wrapper baseline - one of the signals that the tool was invoked.
    private const long CpuStartupBurstMs = 1000L;
    private const string TestSafeWorkerEchoCommand =
        "Write-Output 'WORKER_RESULT:'; " +
        "Write-Output 'files: none'; " +
        "Write-Output 'commands: test-safe real-worker rewrite'; " +
        "Write-Output 'tests: not-run - test-safe real-worker rewrite'; " +
        "Write-Output 'commit: none'; " +
        "Write-Output 'blockers: none'; " +
        "Write-Output 'model_fit: test-safe-rewrite - adequate - infrastructure test guard'; " +
        "Write-Output 'skills: none'; " +
        "Write-Output 'confidence: high'; " +
        "Write-Output 'END_WORKER_RESULT'";
    private static readonly string[] BuildServerCandidates = ["VBCSCompiler", "MSBuild"];
    private sealed record DispatchSpawnReceipt(
        string Command,
        string? ProviderSessionId,
        string? WorktreeHeadSha,
        string? DirtyStateHash);

    private readonly IClock _clock;
    private readonly TimeSpan _postOutputIdleTimeout;
    private readonly TimeSpan _progressStallTimeout;
    private readonly TimeSpan _startupHangTimeout;
    private readonly Func<int, bool> _isStillRunning;
    private readonly Func<int, bool> _tryKillOwnedProcess;
    private readonly bool _processStartDisabled;
    private readonly Func<string, IReadOnlyList<(int ProcessId, string ProcessName, string? CommandLine)>> _findBuildDaemons;
    private readonly Func<int, bool> _tryKillBuildDaemon;
    private readonly IDispatchDiagnosticWriter _diagnosticWriter;
    private readonly DispatchRecoveryPolicy _recoveryPolicy;
    private readonly WorkerProviderCatalog _workerProviders;
    private readonly Func<string, Stream> _openLogReadStream;
    private readonly Action? _beforeGoalWorktreeInspection;
    private readonly Func<ProcessStartInfo, Process?> _startProcess;
    private readonly Dictionary<ProcessLogCacheKey, ProcessLogSnapshot> _processLogCache = [];
    private readonly object _processLogCacheGate = new();
    private readonly ConcurrentDictionary<WorktreeInspectionCacheKey, GoalWorktreeInspectionResult> _worktreeInspectionCache = [];

    public BackgroundDispatchRunner(
        IClock? clock = null,
        TimeSpan? postOutputIdleTimeout = null,
        Func<int, bool>? isStillRunning = null,
        Func<int, bool>? tryKillOwnedProcess = null,
        bool? disableProcessStart = null,
        Func<string, IReadOnlyList<(int ProcessId, string ProcessName, string? CommandLine)>>? findBuildDaemons = null,
        Func<int, bool>? tryKillBuildDaemon = null,
        TimeSpan? progressStallTimeout = null,
        IDispatchDiagnosticWriter? diagnosticWriter = null,
        TimeSpan? startupHangTimeout = null,
        DispatchRecoveryPolicy? recoveryPolicy = null,
        WorkerProviderCatalog? workerProviders = null,
        Func<string, Stream>? openLogReadStream = null,
        Action? beforeGoalWorktreeInspection = null,
        Func<ProcessStartInfo, Process?>? startProcess = null)
    {
        _clock = clock ?? new SystemClock();
        _postOutputIdleTimeout = postOutputIdleTimeout ?? DefaultPostOutputIdleTimeout;
        _progressStallTimeout = progressStallTimeout ?? DefaultProgressStallTimeout;
        _startupHangTimeout = startupHangTimeout ?? DefaultStartupHangTimeout;
        _isStillRunning = isStillRunning ?? IsStillRunning;
        _tryKillOwnedProcess = tryKillOwnedProcess ?? TryKillProcess;
        _processStartDisabled = disableProcessStart ?? IsDispatchStartDisabledByEnvironment();
        _findBuildDaemons = findBuildDaemons ?? FindBuildDaemons;
        _tryKillBuildDaemon = tryKillBuildDaemon ?? TryKillBuildDaemonProcess;
        _diagnosticWriter = diagnosticWriter ?? new FileDiagnosticWriter();
        _recoveryPolicy = recoveryPolicy ?? new DispatchRecoveryPolicy(_clock);
        _workerProviders = workerProviders ?? WorkerProviderCatalog.Default();
        _openLogReadStream = openLogReadStream ??
            (path => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
        _beforeGoalWorktreeInspection = beforeGoalWorktreeInspection;
        _startProcess = startProcess ?? Process.Start;
    }

    private static bool IsDispatchStartDisabledByEnvironment()
    {
        var value = Environment.GetEnvironmentVariable(DisableDispatchStartVariable);
        return value is "1" || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTestRealWorkerCommandRewriteEnabled()
    {
        var value = Environment.GetEnvironmentVariable(TestRewriteRealWorkerCommandsVariable);
        return value is "1" || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }

    public void BeginRefreshCycle() => _worktreeInspectionCache.Clear();

    internal static string RewriteRealWorkerCommandForTests(string command, bool rewriteEnabled)
    {
        if (!rewriteEnabled || !LooksLikeRealSubscriptionWorkerCommand(command))
        {
            return command;
        }

        return TestSafeWorkerEchoCommand;
    }

    private static bool LooksLikeRealSubscriptionWorkerCommand(string command)
    {
        var normalized = Regex.Replace(command, @"\s+", " ").Trim();
        if (normalized.Length == 0)
        {
            return false;
        }

        return normalized.Contains("codex exec", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("@openai/codex", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("gpt-5.3-codex-spark", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("gpt-5-codex", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("gpt-5.5-codex", StringComparison.OrdinalIgnoreCase) ||
            IsClaudeSubscriptionCommand(normalized);
    }

    private static bool IsClaudeSubscriptionCommand(string normalizedCommand)
    {
        return Regex.IsMatch(normalizedCommand, @"(^|[;&|]\s*)claude(\.exe)?\s", RegexOptions.IgnoreCase) &&
            (normalizedCommand.Contains(" -p ", StringComparison.OrdinalIgnoreCase) ||
                normalizedCommand.Contains(" --print", StringComparison.OrdinalIgnoreCase) ||
                normalizedCommand.Contains(" --model ", StringComparison.OrdinalIgnoreCase));
    }

    public TaskProcessRecord StartLatestDispatch(
        AgentOrchestratorKernel kernel,
        GoalId goalId,
        TaskId taskId,
        string logRoot,
        Action<AgentOrchestratorKernel, GoalId, TaskId, DispatchRecordCheckpointPhase>? checkpointBeforeWorkerStart = null)
    {
        var result = TryStartLatestDispatch(kernel, goalId, taskId, logRoot, checkpointBeforeWorkerStart);
        if (result.RecoveryAction is { } action)
        {
            throw new InvalidOperationException(action.Reason);
        }

        if (result.FailureReason is { } failureReason)
        {
            throw new InvalidOperationException(failureReason);
        }

        return result.ProcessRecord
            ?? throw new InvalidOperationException("Dispatch start did not produce a process record.");
    }

    public DispatchProcessStartResult TryStartLatestDispatch(
        AgentOrchestratorKernel kernel,
        GoalId goalId,
        TaskId taskId,
        string logRoot,
        Action<AgentOrchestratorKernel, GoalId, TaskId, DispatchRecordCheckpointPhase>? checkpointBeforeWorkerStart = null,
        Func<GoalId, TaskId, InterruptedDispatchStateRead>? readCurrentState = null)
    {
        var task = kernel.GetTask(goalId, taskId);
        var dispatch = task.LastDispatch
            ?? throw new InvalidOperationException($"Task '{taskId}' has no dispatch to start.");

        if (DispatchFailureClassifier.IsSubscriptionRetryDeferred(task, _clock.UtcNow, out var retryAfter))
        {
            throw new InvalidOperationException($"Task '{taskId}' hit a recoverable subscription usage limit; retry after {retryAfter:u}.");
        }

        if (task.Status != WorkTaskStatus.Running)
        {
            throw new InvalidOperationException($"Task '{taskId}' status is {task.Status}; prepare or retry the dispatch before starting it.");
        }

        if (task.LastProcess is not null)
        {
            throw new InvalidOperationException($"Task '{taskId}' already has a dispatch process record; refresh, cancel, or retry before starting it again.");
        }

        if (_processStartDisabled)
        {
            throw new InvalidOperationException(
                $"Background dispatch process start is disabled in this environment ({DisableDispatchStartVariable}); refusing to launch the worker command. Test-spawned orchestrators must never start real subscription CLIs.");
        }

        Directory.CreateDirectory(logRoot);
        var prefix = $"{goalId.Value[..8]}-{taskId.Value[..8]}-{_clock.UtcNow:yyyyMMddHHmmss}";
        var stdoutPath = Path.Combine(logRoot, $"{prefix}.out.log");
        var stderrPath = Path.Combine(logRoot, $"{prefix}.err.log");
        var exitCodePath = Path.Combine(logRoot, $"{prefix}.exit.txt");
        var childExitRecordPath = Path.Combine(logRoot, $"{prefix}.child-exit.json");
        var hostDiagnosticPath = Path.Combine(logRoot, $"{prefix}.host.err.log");
        var heartbeatPath = Path.Combine(logRoot, $"{prefix}.heartbeat.json");
        var startGatePath = Path.Combine(logRoot, $"{prefix}.start-gate");

        var isLocalDispatch = IsLocalDispatch(dispatch);
        var parametersPath = Path.Combine(logRoot, $"{prefix}.dispatch.json");
        var workerProvider = ResolveWorkerProvider(dispatch);
        var spawnReceipt = BuildDispatchSpawnReceipt(dispatch, workerProvider);
        kernel.RecordDispatchSpawnReceipt(
            goalId,
            taskId,
            spawnReceipt.Command,
            spawnReceipt.ProviderSessionId,
            spawnReceipt.WorktreeHeadSha,
            spawnReceipt.DirtyStateHash);
        dispatch = kernel.GetTask(goalId, taskId).LastDispatch
            ?? throw new InvalidOperationException($"Task '{taskId}' lost its dispatch while recording spawn metadata.");

        // OS worker sandbox: implementation roles receive a Low-labeled writable worktree. Read-only
        // Codex roles also run Low so Codex can skip its expensive nested Windows sandbox setup, but
        // their worktree stays Medium and MIC therefore denies writes.
        var sandbox = WorkerSandboxOptions.FromEnvironment();
        var sandboxProvider = ResolveSandboxProvider(workerProvider);
        var sandboxWorktreeWritable = IsSandboxWorktreeWritable(task.RequiredRole);
        var useSandbox = ShouldUseOsSandbox(
            sandbox.Enabled,
            isLocalDispatch,
            task.RequiredRole,
            sandboxProvider);
        kernel.RecordDispatchSandboxLowIntegrity(goalId, taskId, useSandbox);
        var prepRecordPath = useSandbox ? Path.Combine(logRoot, $"{prefix}.prep.json") : null;
        var prepHeartbeatPath = useSandbox ? Path.Combine(logRoot, $"{prefix}.prep.heartbeat.json") : null;
        var prepExitCodePath = useSandbox ? Path.Combine(logRoot, $"{prefix}.prep.exit.txt") : null;

        var dispatchHostCommand = RewriteRealWorkerCommandForTests(dispatch.Command, IsTestRealWorkerCommandRewriteEnabled());
        var egressProxyOptions = CodexEgressProxyOptions.FromEnvironment();

        if (useSandbox)
        {
            DispatchProcessHost.WritePrepRecord(prepRecordPath!, new DispatchProcessHost.DispatchPrepRecord(
                DispatchProcessHost.PrepDispatchKind,
                goalId.Value,
                taskId.Value,
                dispatch.WorkingDirectory,
                prepHeartbeatPath!,
                prepExitCodePath!,
                _clock.UtcNow,
                sandboxProvider,
                sandboxWorktreeWritable,
                null));
        }

        DispatchProcessHost.WriteParameters(parametersPath, new DispatchProcessHost.DispatchRunParameters(
            dispatchHostCommand,
            dispatch.WorkingDirectory,
            stdoutPath,
            stderrPath,
            exitCodePath,
            heartbeatPath,
            ShutdownBuildServerOnExit: !isLocalDispatch,
            DisableSharedCompilation: !isLocalDispatch,
            SandboxLowIntegrity: useSandbox,
            Provider: sandboxProvider,
            PromptPath: dispatch.PromptPath,
            SandboxWorktreeWritable: sandboxWorktreeWritable,
            ProviderSessionId: dispatch.ProviderSessionId,
            WorktreeHeadSha: dispatch.WorktreeHeadSha,
            DirtyStateHash: dispatch.DirtyStateHash,
            CodexEgressProxyEnabled: egressProxyOptions.Enabled,
            CodexEgressProxyEnforce: egressProxyOptions.Enforce,
            CodexEgressProxyIdleTimeoutMs: egressProxyOptions.IdleTimeoutMs,
            CodexEgressProxyConnectTimeoutMs: egressProxyOptions.ConnectTimeoutMs,
            Kind: DispatchProcessHost.WorkerDispatchKind,
            PrepGoalId: useSandbox ? goalId.Value : null,
            PrepTaskId: useSandbox ? taskId.Value : null,
            PrepRecordPath: prepRecordPath,
            PrepHeartbeatPath: prepHeartbeatPath,
            PrepExitCodePath: prepExitCodePath,
            ChildExitRecordPath: childExitRecordPath,
            HostDiagnosticPath: hostDiagnosticPath));

        // Launch the native dispatch host detached: it outlives this CLI process, runs the worker
        // command through the resolved PowerShell host, performs sandbox prep off the conductor tick,
        // and writes logs/heartbeat/exit natively.
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = dispatch.WorkingDirectory
        };

        if (OperatingSystem.IsWindows())
        {
            startInfo.CreateNewProcessGroup = true;
        }

        startInfo.Environment[DispatchProcessHost.StartGatePathVariable] = startGatePath;

        startInfo.ArgumentList.Add("exec");
        startInfo.ArgumentList.Add(ResolveDispatchHostAssembly());
        startInfo.ArgumentList.Add(DispatchProcessHost.SubcommandName);
        startInfo.ArgumentList.Add(parametersPath);

        // Capture baseCommit immediately before spawning — the left boundary for file attribution.
        if (spawnReceipt.WorktreeHeadSha is not null)
            kernel.RecordDispatchBaseCommit(goalId, taskId, spawnReceipt.WorktreeHeadSha);

        var currentTask = kernel.GetTask(goalId, taskId);
        if (currentTask.InterruptedDispatchRecoveryId is { } interruptedDispatchId &&
            TryReadAutoRequeueBlocker(kernel, goalId, taskId, readCurrentState, out var blocker))
        {
            kernel.RecordTaskRequeueSkipped(
                goalId,
                taskId,
                interruptedDispatchId,
                blocker.BlockingEntity,
                blocker.TerminalState,
                blocker.Reason,
                blocker.Detail);
            if (blocker.Reason == "terminal-state")
            {
                kernel.ConcludeInterruptedDispatchRecovery(
                    goalId,
                    taskId,
                    blocker.GoalStatus,
                    blocker.TaskStatus);
            }
            return DispatchProcessStartResult.Skipped();
        }

        // Only persist automatic recovery preparation after the live state guard admits it. A
        // checkpoint before this read can merge the stale tick-owned task status over an operator
        // cancellation and make the subsequent read falsely appear non-terminal.
        checkpointBeforeWorkerStart?.Invoke(kernel, goalId, taskId, DispatchRecordCheckpointPhase.BeforeProcessStart);

        ProcessSpawnGuard.ClearInheritableStateDatabaseHandles();
        var process = _startProcess(startInfo)
            ?? throw new InvalidOperationException("Failed to start background dispatch process.");
        if (!WorkerProcessJobs.TryRegister(process, $"{goalId.Value}:{taskId.Value}", out var registrationFailure))
        {
            process.Dispose();
            kernel.ReportTaskProgress(goalId, taskId, WorkTaskStatus.Failed, registrationFailure);
            checkpointBeforeWorkerStart?.Invoke(kernel, goalId, taskId, DispatchRecordCheckpointPhase.ProcessMayHaveStarted);
            return DispatchProcessStartResult.Failed(registrationFailure);
        }

        var record = new TaskProcessRecord(
            process.Id,
            dispatch.Command,
            dispatch.WorkingDirectory,
            stdoutPath,
            stderrPath,
            exitCodePath,
            _clock.UtcNow,
            null,
            null,
            OwnedProcessIds: [process.Id],
            ChildExitRecordPath: childExitRecordPath);

        kernel.RecordTaskProcessStarted(goalId, taskId, record);
        try
        {
            checkpointBeforeWorkerStart?.Invoke(kernel, goalId, taskId, DispatchRecordCheckpointPhase.ProcessMayHaveStarted);
        }
        catch
        {
            TerminateUnreleasedDispatchHost(process);
            throw;
        }

        ReleaseDispatchHostStartGate(startGatePath);
        return DispatchProcessStartResult.Started(record);
    }

    private static void TerminateUnreleasedDispatchHost(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // The checkpoint failure is the actionable fault; process cleanup is best-effort.
        }
    }

    private static void ReleaseDispatchHostStartGate(string startGatePath)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(startGatePath)!);
            File.WriteAllText(startGatePath, "go");
        }
        catch
        {
            // Best-effort: if the gate cannot be written, the dispatch host fails closed rather than
            // launching a worker outside the supervisor job.
        }
    }

    private WorkerSandboxProvider ResolveSandboxProvider(TaskDispatchRecord dispatch)
    {
        return ResolveSandboxProvider(ResolveWorkerProvider(dispatch));
    }

    internal static WorkerSandboxProvider ResolveSandboxProvider(IWorkerProvider provider)
    {
        if (provider.Identity.Kind == ProviderKind.AnthropicClaudeCli)
        {
            return WorkerSandboxProvider.Claude;
        }

        if (provider.Identity.Kind is ProviderKind.OpenAICodexCli or ProviderKind.OpenAICodexSpark or ProviderKind.OpenAICodexOssCli)
        {
            return WorkerSandboxProvider.Codex;
        }

        if (provider.Identity.Kind == ProviderKind.OllamaQwenCodeCli)
        {
            return WorkerSandboxProvider.Ollama;
        }

        return WorkerSandboxProvider.Unknown;
    }

    internal static bool IsSandboxWorktreeWritable(AgentRole role) =>
        role is AgentRole.Developer or AgentRole.Tester;

    internal static bool ShouldUseOsSandbox(
        bool sandboxEnabled,
        bool isLocalDispatch,
        AgentRole role,
        WorkerSandboxProvider provider) =>
        sandboxEnabled &&
        !isLocalDispatch &&
        (IsSandboxWorktreeWritable(role) || provider == WorkerSandboxProvider.Codex);

    /// <summary>
    /// Scans tasks for an exit file and auto-reconciles any whose dispatched process
    /// has written its exit code. When <paramref name="onlyGoalId"/> is supplied, the
    /// sweep is limited to that goal; otherwise all goals are swept. Idempotent: a task
    /// whose process completion has already been applied as verification is skipped on
    /// repeat calls. Returns the number of tasks reconciled.
    /// </summary>
    public int SweepExitedProcesses(AgentOrchestratorKernel kernel, GoalId? onlyGoalId = null)
    {
        var reconciled = 0;
        foreach (var goal in kernel.Goals.Where(goal => onlyGoalId is null || goal.Id == onlyGoalId))
        {
            foreach (var task in goal.Tasks)
            {
                var process = task.LastProcess;
                if (task.Status is WorkTaskStatus.WaitingForHuman or
                                   WorkTaskStatus.Failed or
                                   WorkTaskStatus.Cancelled ||
                    process is null ||
                    process.WasCancelled ||
                    HasProcessOnlyCompletionAlreadyApplied(task, process) ||
                    HasRecordedCompletionForProcess(task, process))
                {
                    continue;
                }

                var recoveryDecision = _recoveryPolicy.Evaluate(process, AnyTrackedProcessStillRunning(process));
                if (!TryCompleteFromExitFile(kernel, goal.Id, task.Id, process, recoveryDecision, out var outcome))
                    continue;

                ApplyRefreshOutcomeAndWriteDiagnostics(kernel, goal.Id, task.Id, outcome);
                if (outcome.RecoveryDecision?.Action != DispatchRecoveryAction.Hold)
                {
                    reconciled++;
                }
            }
        }

        return reconciled;
    }

    private static bool HasRecordedCompletionForProcess(TaskSpec task, TaskProcessRecord process)
    {
        if (process.CompletedAt is null || process.ExitCode is null)
        {
            return false;
        }

        return task.VerificationHistory.Any(verification =>
            verification.ExitCode == process.ExitCode &&
            verification.Command.Equals(process.Command, StringComparison.Ordinal) &&
            verification.WorkingDirectory.Equals(process.WorkingDirectory, StringComparison.OrdinalIgnoreCase) &&
            verification.CompletedAt == process.CompletedAt);
    }

    private static bool HasProcessOnlyCompletionAlreadyApplied(TaskSpec task, TaskProcessRecord process) =>
        task.Status == WorkTaskStatus.Completed &&
        task.LastVerification is not null &&
        process.CompletedAt is not null &&
        process.ExitCode is not null;

    public TaskProcessRecord RefreshLatestProcess(AgentOrchestratorKernel kernel, GoalId goalId, TaskId taskId)
    {
        var outcome = ReconcileLatestProcess(kernel, goalId, taskId);
        ApplyRefreshOutcomeAndWriteDiagnostics(kernel, goalId, taskId, outcome);
        return outcome.ProcessRecord;
    }

    public void ApplyRefreshOutcomeAndWriteDiagnostics(
        AgentOrchestratorKernel kernel,
        GoalId goalId,
        TaskId taskId,
        DispatchRefreshOutcome outcome)
    {
        ApplyRefreshOutcome(kernel, goalId, taskId, outcome);
        if (outcome.DiagnosticPayload is not { } diagnostic)
            return;

        var task = kernel.GetTask(goalId, taskId);
        TryWriteDiagnosticRecord(
            goalId,
            taskId,
            task,
            outcome.ProcessRecord,
            diagnostic.ExitCode,
            diagnostic.StandardOutput,
            diagnostic.StandardError);
    }

    public DispatchRefreshOutcome ReconcileLatestProcess(AgentOrchestratorKernel kernel, GoalId goalId, TaskId taskId)
    {
        var task = kernel.GetTask(goalId, taskId);
        var processRecord = task.LastProcess
            ?? throw new InvalidOperationException($"Task '{taskId}' has no background process to refresh.");

        var hasLiveTrackedProcess = AnyTrackedProcessStillRunning(processRecord);
        var observedHeartbeat = TryReadHeartbeat(GetHeartbeatPath(processRecord), out var refreshHeartbeat)
            ? refreshHeartbeat
            : null;
        RecordProviderSessionFromHeartbeat(kernel, goalId, taskId, task, observedHeartbeat);
        var hasLiveProcess = AnyObservedProcessStillRunning(processRecord, observedHeartbeat);
        var worktreeInspectionStatus = DispatchWorktreeInspectionStatus.NotRequired;
        if (!hasLiveProcess &&
            !File.Exists(processRecord.ExitCodePath) &&
            task.LastDispatch is { } lastDispatch &&
            RequiresFileChangeEvidence(task))
        {
            var inspection = InspectGoalWorktree(
                processRecord.WorkingDirectory,
                goalId,
                lastDispatch.DispatchedAt);
            worktreeInspectionStatus = inspection.IsAvailable
                ? DispatchWorktreeInspectionStatus.Available(
                    !inspection.Evidence.IsClean,
                    processRecord.WorkingDirectory)
                : inspection.IsUnsafe
                    ? DispatchWorktreeInspectionStatus.Unsafe(
                        processRecord.WorkingDirectory,
                        inspection.UnavailableReason ?? "unknown",
                        inspection.GitReceipt)
                : DispatchWorktreeInspectionStatus.Unavailable(
                    processRecord.WorkingDirectory,
                    inspection.UnavailableReason ?? "unknown",
                    inspection.GitReceipt);
        }
        var recoveryDecision = _recoveryPolicy.Evaluate(
            processRecord,
            hasLiveProcess,
            DispatchRecoveryPolicy.GetStaleRetryBudgetRemaining(task),
            worktreeInspectionStatus);
        var exitFileExists = File.Exists(processRecord.ExitCodePath);
        if (TryCompleteFromExitFile(kernel, goalId, taskId, processRecord, recoveryDecision, out var completion))
            return completion;

        if (hasLiveProcess)
        {
            if (!hasLiveTrackedProcess && !exitFileExists)
            {
                return new DispatchRefreshOutcome(processRecord, null, RecoveryDecision: recoveryDecision);
            }

            if (IsAuthoritativeHold(recoveryDecision))
            {
                return new DispatchRefreshOutcome(processRecord, null, RecoveryDecision: recoveryDecision);
            }

            if (TryDetectHungCodexWrapper(task, processRecord, out var diagnostic))
            {
                return CompleteHungWrapperDispatch(
                    kernel,
                    goalId,
                    taskId,
                    task,
                    processRecord,
                    diagnostic,
                    recoveryDecision);
            }

            if (TryDetectHungSubscriptionWrapper(task, processRecord, out var wrapperDiagnostic))
            {
                return CompleteHungWrapperDispatch(
                    kernel,
                    goalId,
                    taskId,
                    task,
                    processRecord,
                    wrapperDiagnostic,
                    recoveryDecision);
            }

            if (TryDetectStartupHang(processRecord, out var startupHangDiagnostic))
            {
                return CompleteSuspectedHangDispatch(
                    kernel,
                    goalId,
                    taskId,
                    task,
                    processRecord,
                    startupHangDiagnostic,
                    DispatchRecoveryAction.Reap,
                    recoveryDecision);
            }

            if (TryDetectProbableProgressStall(task, goalId, processRecord, out var stallDiagnostic))
            {
                return CompleteSuspectedHangDispatch(
                    kernel,
                    goalId,
                    taskId,
                    task,
                    processRecord,
                    stallDiagnostic,
                    DispatchRecoveryAction.ClassifyBlocker,
                    recoveryDecision);
            }

            return new DispatchRefreshOutcome(processRecord, null, RecoveryDecision: recoveryDecision);
        }

        if (exitFileExists)
        {
            return new DispatchRefreshOutcome(processRecord, null, RecoveryDecision: recoveryDecision);
        }

        var staleResourceAccounting = ReapTrackedProcessJobs(processRecord, waitForExit: false);
        if (TryBuildStaleDispatchAutoRequeueOutcome(
                kernel,
                goalId,
                taskId,
                task,
                processRecord,
                recoveryDecision,
                staleResourceAccounting,
                out var autoRequeueOutcome))
        {
            return autoRequeueOutcome;
        }

        if (recoveryDecision.Action == DispatchRecoveryAction.Hold)
        {
            return new DispatchRefreshOutcome(processRecord, null, RecoveryDecision: recoveryDecision);
        }

        if (worktreeInspectionStatus.HasDirtyEvidence)
        {
            var interruptedDecision = new DispatchRecoveryDecision(
                DispatchRecoveryAction.PreserveInterruptedWork,
                DispatchRecoveryPolicy.ToActionName(DispatchRecoveryAction.PreserveInterruptedWork),
                processRecord.ExitCodePath,
                "process disappeared with dirty worktree evidence",
                "interrupted worker evidence requires operator verification");
            const string interruptedReason = "process missing with dirty worktree evidence";
            TryWriteExitCode(processRecord.ExitCodePath, 1, interruptedReason);
            return BuildCompletedProcessOutcome(
                kernel,
                goalId,
                taskId,
                processRecord,
                1,
                BuildRecoveryDiagnostic(interruptedDecision),
                interruptedDecision,
                staleResourceAccounting) with
                {
                    AutoRequeueDisposition = new DispatchAutoRequeueDisposition(
                    "InterruptedDispatchWorkPreserved",
                    BuildRecoveryDiagnostic(interruptedDecision),
                    ShouldRequeue: false)
                };
        }

        var staleDiagnostic = BuildRecoveryDiagnostic(recoveryDecision);
        return BuildCompletedProcessOutcome(
            kernel,
            goalId,
            taskId,
            processRecord,
            1,
            staleDiagnostic,
            recoveryDecision,
            staleResourceAccounting);
    }

    private bool TryCompleteFromExitFile(
        AgentOrchestratorKernel kernel,
        GoalId goalId,
        TaskId taskId,
        TaskProcessRecord processRecord,
        DispatchRecoveryDecision recoveryDecision,
        out DispatchRefreshOutcome outcome)
    {
        var hasHeartbeat = TryReadHeartbeat(GetHeartbeatPath(processRecord), out var heartbeat);
        var observedHeartbeat = hasHeartbeat ? heartbeat : null;
        RecordProviderSessionFromHeartbeat(kernel, goalId, taskId, kernel.GetTask(goalId, taskId), observedHeartbeat);
        var exitRead = ReadExitCode(processRecord.ExitCodePath);
        if (exitRead.Kind == ExitCodeReadKind.Valid)
        {
            if (AnyOwnedWorkerProcessStillRunning(processRecord, observedHeartbeat))
            {
                outcome = new DispatchRefreshOutcome(processRecord, null, RecoveryDecision: recoveryDecision);
                return false;
            }
        }
        else
        {
            if (exitRead.Kind == ExitCodeReadKind.Missing ||
                AnyObservedProcessStillRunning(processRecord, observedHeartbeat))
            {
                outcome = new DispatchRefreshOutcome(processRecord, null, RecoveryDecision: recoveryDecision);
                return false;
            }

            exitRead = ReadExitCodeWithRetry(processRecord.ExitCodePath);
            if (exitRead.Kind != ExitCodeReadKind.Valid)
            {
                var apparatusDecision = new DispatchRecoveryDecision(
                    DispatchRecoveryAction.Hold,
                    DispatchRecoveryPolicy.ToActionName(DispatchRecoveryAction.Hold),
                    processRecord.ExitCodePath,
                    $"exit artifact unavailable; state={exitRead.Kind}; evidence={exitRead.Evidence}",
                    $"exit-artifact-{exitRead.Kind.ToString().ToLowerInvariant()}");
                outcome = new DispatchRefreshOutcome(
                    processRecord,
                    null,
                    RecoveryDecision: apparatusDecision);
                return true;
            }
        }

        var exitCode = exitRead.ExitCode!.Value;

        if (AnyOwnedWorkerProcessStillRunning(processRecord, observedHeartbeat))
        {
            outcome = new DispatchRefreshOutcome(processRecord, null, RecoveryDecision: recoveryDecision);
            return false;
        }

        outcome = BuildCompletedProcessOutcome(
            kernel,
            goalId,
            taskId,
            processRecord,
            exitCode,
            BuildRecoveryDiagnostic(recoveryDecision),
            recoveryDecision);
        return true;
    }

    private static ExitCodeReadResult ReadExitCodeWithRetry(string path)
    {
        const int attempts = 3;
        var result = ReadExitCode(path);
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            if (result.Kind == ExitCodeReadKind.Valid)
            {
                return result;
            }

            if (attempt < attempts - 1)
            {
                Thread.Sleep(TimeSpan.FromMilliseconds(50));
                result = ReadExitCode(path);
            }
        }

        return result;
    }

    public static void ApplyRefreshOutcome(
        AgentOrchestratorKernel kernel,
        GoalId goalId,
        TaskId taskId,
        DispatchRefreshOutcome outcome)
    {
        var task = kernel.GetTask(goalId, taskId);
        var previousProcess = task.LastProcess;
        var verification = outcome.Verification;
        if (outcome.ResultCommit is not null)
        {
            kernel.RecordDispatchResultCommit(goalId, taskId, outcome.ResultCommit);
            verification = MarkCommittedChangesFromResultCommit(kernel.GetTask(goalId, taskId), verification);
            if (!string.IsNullOrWhiteSpace(outcome.ResultCommitProvenance))
            {
                kernel.RecordTaskNote(
                    goalId,
                    taskId,
                    $"TaskOutputCommitted: sha={outcome.ResultCommit}; provenance={outcome.ResultCommitProvenance}.");
            }
        }

        if (task.Status == WorkTaskStatus.Completed &&
            task.LastVerification is not null &&
            outcome.ProcessRecord.CompletedAt is not null &&
            previousProcess?.ProcessId == outcome.ProcessRecord.ProcessId)
        {
            verification = null;
        }

        kernel.RecordTaskProcessRefreshed(goalId, taskId, outcome.ProcessRecord, verification, outcome.ProviderFailureKind);
        if (verification is not null && outcome.ProcessRecord.ResourceAccounting is { } accounting)
        {
            kernel.RecordTaskNote(goalId, taskId, FormatResourceReceipt(goalId, taskId, accounting));
        }

        if (outcome.AutoRequeueDisposition is { } disposition)
        {
            kernel.RecordTaskNote(goalId, taskId, $"{disposition.EventName}: {disposition.Message}");
            if (disposition.ShouldRequeue)
            {
                kernel.RequeueInterruptedDispatch(goalId, taskId, disposition.Message);
            }
        }

        if (outcome.RecoveryDecision is
            {
                Action: DispatchRecoveryAction.Hold,
                Blocker: { Length: > 0 } blocker
            } apparatusHold)
        {
            RecordBoundedApparatusHold(kernel, goalId, taskId, apparatusHold, blocker);
        }
    }

    private static void RecordBoundedApparatusHold(
        AgentOrchestratorKernel kernel,
        GoalId goalId,
        TaskId taskId,
        DispatchRecoveryDecision decision,
        string blocker)
    {
        var priorObservations = kernel.GetTimeline(goalId).Count(evt =>
            evt.TaskId == taskId &&
            evt.Kind == ProgressKind.TaskNote &&
            evt.Message.StartsWith(ApparatusHoldReceiptPrefix, StringComparison.Ordinal) &&
            evt.Message.Contains($"blocker='{blocker}'", StringComparison.Ordinal));
        var observation = priorObservations + 1;
        var diagnostic = BuildRecoveryDiagnostic(decision);
        kernel.RecordTaskNote(
            goalId,
            taskId,
            $"{ApparatusHoldReceiptPrefix} observation={observation}/{ApparatusHoldObservationsBeforeEscalation}; {diagnostic}");

        if (observation < ApparatusHoldObservationsBeforeEscalation)
        {
            return;
        }

        var fingerprint = $"dispatch-apparatus-hold:{taskId.Value}:{blocker}";
        var requestResult = kernel.RequestHumanInputDeduplicated(
            goalId,
            taskId,
            $"Dispatch recovery cannot determine the worker outcome after {observation} observations because blocker '{blocker}' remains. " +
            $"Inspect and repair or remove the apparatus artifact at '{decision.EvidencePath}', then answer this request to resume. {diagnostic}",
            HumanWaitKind.RecoveryChoice,
            isAutoDefaultable: false,
            isDismissible: false,
            isAnswerRequired: true,
            isExternallyBlocked: false,
            questionFingerprint: fingerprint,
            blockerFingerprint: fingerprint,
            recordDuplicateSuppression: false);
        if (!requestResult.WasSuppressedByAnswer || requestResult.Request.AnsweredAt is null)
        {
            return;
        }

        var answeredObservations = kernel.GetTimeline(goalId).Count(evt =>
            evt.TaskId == taskId &&
            evt.Kind == ProgressKind.TaskNote &&
            evt.OccurredAt >= requestResult.Request.AnsweredAt &&
            evt.Message.StartsWith(ApparatusHoldReceiptPrefix, StringComparison.Ordinal) &&
            evt.Message.Contains($"blocker='{blocker}'", StringComparison.Ordinal));
        var task = kernel.GetTask(goalId, taskId);
        if (answeredObservations >= AnsweredApparatusHoldObservationsBeforeFailure &&
            task.Status is not (WorkTaskStatus.Completed or WorkTaskStatus.Failed or WorkTaskStatus.Cancelled))
        {
            kernel.ReportTaskProgress(
                goalId,
                taskId,
                WorkTaskStatus.Failed,
                $"Dispatch apparatus remained indeterminate for {answeredObservations} observations after answered recovery request " +
                $"{requestResult.Request.Id.Value[..8]}; blocker='{blocker}'; evidence='{decision.EvidencePath}'.");
        }
    }

    private bool TryBuildStaleDispatchAutoRequeueOutcome(
        AgentOrchestratorKernel kernel,
        GoalId goalId,
        TaskId taskId,
        TaskSpec task,
        TaskProcessRecord processRecord,
        DispatchRecoveryDecision recoveryDecision,
        TaskProcessResourceAccounting? resourceAccounting,
        out DispatchRefreshOutcome outcome)
    {
        outcome = default!;
        if (!IsSafeStaleDispatchCandidate(recoveryDecision))
        {
            return false;
        }

        if (!TryBuildSafeStaleDispatchAutoRequeueDisposition(
                kernel,
                goalId,
                taskId,
                task,
                processRecord,
                out var disposition,
                out var evidenceDiagnostic))
        {
            return false;
        }

        if (DispatchRecoveryPolicy.GetStaleAutoRequeueBudgetRemaining(task) <= 0)
        {
            const string capBlocker = "stale-dispatch auto-requeue cap exhausted";
            var capEvidenceDiagnostic = evidenceDiagnostic.Replace(
                "auto-requeueing instead of escalating mechanical recovery",
                "auto-requeue cap exhausted; escalating mechanical recovery",
                StringComparison.Ordinal);
            var capDecision = new DispatchRecoveryDecision(
                DispatchRecoveryAction.BudgetExhausted,
                DispatchRecoveryPolicy.ToActionName(DispatchRecoveryAction.BudgetExhausted),
                recoveryDecision.EvidencePath,
                capBlocker,
                capBlocker);
            var capDiagnostic = AppendDiagnostic(BuildRecoveryDiagnostic(capDecision), capEvidenceDiagnostic);
            outcome = BuildCompletedProcessOutcome(
                kernel,
                goalId,
                taskId,
                processRecord,
                1,
                capDiagnostic,
                capDecision,
                resourceAccounting) with
                {
                    AutoRequeueDisposition = disposition with
                    {
                        EventName = "StaleDispatchAutoRequeueCapExhausted",
                        Message = capEvidenceDiagnostic,
                        ShouldRequeue = false
                    }
                };
            return true;
        }

        var retryDecision = WithAction(DispatchRecoveryAction.RetryStale, recoveryDecision, recoveryDecision.Reason);
        var diagnostic = AppendDiagnostic(BuildRecoveryDiagnostic(retryDecision), evidenceDiagnostic);
        outcome = BuildCompletedProcessOutcome(
            kernel,
            goalId,
            taskId,
            processRecord,
            1,
            diagnostic,
            retryDecision,
            resourceAccounting) with
            {
                AutoRequeueDisposition = disposition
            };
        return true;
    }

    private static bool IsSafeStaleDispatchCandidate(DispatchRecoveryDecision recoveryDecision)
    {
        if (recoveryDecision.Action == DispatchRecoveryAction.MarkStale)
        {
            return string.IsNullOrWhiteSpace(recoveryDecision.Blocker);
        }

        return recoveryDecision.Action == DispatchRecoveryAction.BudgetExhausted &&
            string.Equals(recoveryDecision.Blocker, "stale-dispatch retry budget exhausted", StringComparison.Ordinal);
    }

    private bool TryBuildSafeStaleDispatchAutoRequeueDisposition(
        AgentOrchestratorKernel kernel,
        GoalId goalId,
        TaskId taskId,
        TaskSpec task,
        TaskProcessRecord processRecord,
        out DispatchAutoRequeueDisposition disposition,
        out string diagnostic)
    {
        disposition = default!;
        diagnostic = string.Empty;
        var heartbeat = ProcessLogReader.ReadHeartbeat(processRecord, _clock.UtcNow);
        var stdoutFileBytes = SafeFileLength(processRecord.StandardOutputPath);
        var stderrFileBytes = SafeFileLength(processRecord.StandardErrorPath);
        var stdout = ReadProcessLogBestEffort(processRecord, processRecord.StandardOutputPath).DecisionText;
        var stderr = ReadProcessLogBestEffort(processRecord, processRecord.StandardErrorPath).DecisionText;
        var workerResultPresent = HasWorkerResultArtifact(processRecord.WorkingDirectory, stdout);
        var taskOutputCommitted = HasTaskOutputCommittedForDispatch(kernel.GetGoal(goalId), taskId, task.LastDispatch);
        GoalWorktreeDispatchEvidence? worktreeEvidence = null;
        var worktreeEvidenceAvailable = false;
        if (task.LastDispatch is { } dispatch &&
            TryInspectGoalWorktree(processRecord.WorkingDirectory, goalId, dispatch.DispatchedAt, out var inspectedWorktree))
        {
            worktreeEvidenceAvailable = true;
            worktreeEvidence = inspectedWorktree;
        }

        var requiresWorktreeProof = RequiresFileChangeEvidence(task);
        var hasAnyPostDispatchCommit = worktreeEvidence?.HasCommitAfterDispatch == true;
        var hasRelevantPostDispatchCommit = worktreeEvidence?.HasRelevantCommitAfterDispatch == true;
        var worktreeClean = worktreeEvidence?.IsClean == true;
        var commitsAfterDispatch = worktreeEvidence is null
            ? "n/a"
            : worktreeEvidence.CommitsAfterDispatch.ToString();
        var hasOutputBytes =
            heartbeat.StandardOutputBytes > 0 ||
            heartbeat.StandardErrorBytes > 0 ||
            stdoutFileBytes > 0 ||
            stderrFileBytes > 0;

        if (hasOutputBytes ||
            workerResultPresent ||
            taskOutputCommitted ||
            (requiresWorktreeProof && !worktreeEvidenceAvailable) ||
            worktreeEvidence?.IsClean == false ||
            hasAnyPostDispatchCommit)
        {
            return false;
        }

        var autoRequeueBudgetRemaining = DispatchRecoveryPolicy.GetStaleAutoRequeueBudgetRemaining(task);
        var autoRequeuesSpent = DispatchRecoveryPolicy.DefaultStaleDispatchAutoRequeues - autoRequeueBudgetRemaining;
        var attempt = autoRequeueBudgetRemaining > 0
            ? autoRequeuesSpent + 1
            : autoRequeuesSpent;
        var inventory =
            $"heartbeat_state={heartbeat.State}; heartbeat_available={heartbeat.IsAvailable.ToString().ToLowerInvariant()}; " +
            $"stdout_bytes={heartbeat.StandardOutputBytes}; stderr_bytes={heartbeat.StandardErrorBytes}; " +
            $"stdout_file_bytes={stdoutFileBytes}; stderr_file_bytes={stderrFileBytes}; " +
            $"worker_result_present={workerResultPresent.ToString().ToLowerInvariant()}; " +
            $"task_output_committed={taskOutputCommitted.ToString().ToLowerInvariant()}; " +
            $"worktree_evidence_available={worktreeEvidenceAvailable.ToString().ToLowerInvariant()}; " +
            $"worktree_clean={(worktreeEvidenceAvailable ? worktreeClean.ToString().ToLowerInvariant() : "n/a")}; " +
            $"commits_after_dispatch={commitsAfterDispatch}; " +
            $"has_relevant_commit={hasRelevantPostDispatchCommit.ToString().ToLowerInvariant()}; " +
            $"owned_cpu_ms={heartbeat.OwnedCpuMs}; " +
            $"auto_requeue={attempt}/{DispatchRecoveryPolicy.DefaultStaleDispatchAutoRequeues}";
        diagnostic = "Stale dispatch corpse produced no worker output, worker result, committed output, or worktree changes; " +
            "auto-requeueing instead of escalating mechanical recovery. " + inventory + ".";
        disposition = new DispatchAutoRequeueDisposition("StaleDispatchAutoRequeued", diagnostic);
        return true;
    }

    private static TaskVerificationRecord? MarkCommittedChangesFromResultCommit(TaskSpec task, TaskVerificationRecord? verification)
    {
        if (verification is null ||
            verification.HasCommittedChanges ||
            task.LastDispatch is not { } dispatch ||
            string.IsNullOrWhiteSpace(dispatch.BaseCommit) ||
            string.IsNullOrWhiteSpace(dispatch.ResultCommit) ||
            string.Equals(dispatch.BaseCommit, dispatch.ResultCommit, StringComparison.OrdinalIgnoreCase))
        {
            return verification;
        }

        return verification with { HasCommittedChanges = true };
    }

    private DispatchRefreshOutcome BuildCompletedProcessOutcome(
        AgentOrchestratorKernel kernel,
        GoalId goalId,
        TaskId taskId,
        TaskProcessRecord processRecord,
        int exitCode,
        string? standardErrorDiagnostic = null,
        DispatchRecoveryDecision? recoveryDecision = null,
        TaskProcessResourceAccounting? capturedResourceAccounting = null,
        string? orchestratorFailureReason = null)
    {
        var observedExitCode = exitCode;
        var exitArtifactAlreadyExisted = File.Exists(processRecord.ExitCodePath);
        var outputSnapshot = ReadProcessLogBestEffort(processRecord, processRecord.StandardOutputPath);
        var errorSnapshot = ReadProcessLogBestEffort(processRecord, processRecord.StandardErrorPath);
        var decisionStandardOutput = outputSnapshot.DecisionText;
        var decisionStandardError = errorSnapshot.DecisionText;
        string? finalPlannerRejectionDiagnostic = null;
        var hasChildExitRecord = TryReadChildExitRecord(processRecord.ChildExitRecordPath, out var childExitRecord);
        var wrapperExitReconciled = false;
        var resourceAccounting = capturedResourceAccounting ?? ReleaseTrackedProcessJobs(processRecord);
        if (resourceAccounting is not null &&
            !resourceAccounting.Reaped &&
            IsDispatchHostReapCompletion(decisionStandardError))
        {
            resourceAccounting = resourceAccounting with { Reaped = true };
        }
        var task = kernel.GetTask(goalId, taskId);
        var goal = kernel.GetGoal(goalId);
        var hasRoleCapability = DispatchRoleOutputCapabilities.TryGet(task.RequiredRole, out var dispatchRoleCapability);
        var completeNonBlockedWorkerResult = hasRoleCapability && HasSuccessfulWorkerResult(
            processRecord.WorkingDirectory,
            decisionStandardOutput,
            decisionStandardError,
            allowNoChangedFiles: true,
            requireNoBlockers: true,
            roleCapability: dispatchRoleCapability);
        var successfulChildResultAvailable =
            observedExitCode != 0 &&
            hasChildExitRecord &&
            childExitRecord.ExitCode == 0 &&
            completeNonBlockedWorkerResult;
        var completionContractSucceeded = true;

        if (task.RequiredRole == AgentRole.Researcher &&
            RequiresDurableResearchArtifact(goal, task) &&
            (exitCode == 0 || successfulChildResultAvailable))
        {
            var capturedResearchOutput = ResearcherOutputContract.ReadCapturedOutputTail(processRecord.StandardOutputPath);
            var researchContract = ResearcherOutputContract.Resolve(capturedResearchOutput);
            if (!researchContract.Succeeded || researchContract.Research is null)
            {
                exitCode = 1;
                completionContractSucceeded = false;
                standardErrorDiagnostic = AppendDiagnostic(
                    AppendDiagnostic(
                        standardErrorDiagnostic ?? string.Empty,
                        researchContract.Diagnostic),
                    DispatchFailureDiagnosticMarker.Format(DispatchFailureDiagnosticMarker.ResearcherOutputContractRejected));
            }
            else if (!ResearcherOutputContract.TryPersistDurableReceipt(
                         processRecord.StandardOutputPath,
                         researchContract.Research,
                         out var appendDiagnostic))
            {
                exitCode = 1;
                completionContractSucceeded = false;
                standardErrorDiagnostic = AppendDiagnostic(
                    AppendDiagnostic(
                        standardErrorDiagnostic ?? string.Empty,
                        $"Researcher output contract could not persist the accepted artifact: {appendDiagnostic}. Retry Researcher for contract repair."),
                    DispatchFailureDiagnosticMarker.Format(DispatchFailureDiagnosticMarker.ResearcherArtifactPersistenceFailed));
            }
        }

        if (task.RequiredRole == AgentRole.Planner && (exitCode == 0 || successfulChildResultAvailable))
        {
            var acceptanceCriteria = RequiresDurablePlanArtifact(goal, task)
                ? goal.RefinedSpec?.AcceptanceCriteria ?? []
                : null;
            var capturedPlannerOutput = PlannerOutputContract.ReadCapturedOutputTail(processRecord.StandardOutputPath);
            var evidenceRequest = AgentOutputDirectives.ParseHumanInputRequest(decisionStandardOutput, AgentRole.Planner);
            var plannerContract = evidenceRequest.IsMalformed
                ? new PlannerOutputContractResult(false, null, null, evidenceRequest.Diagnostic!)
                : PlannerOutputContract.Resolve(
                    capturedPlannerOutput,
                    decisionStandardError,
                    processRecord.WorkingDirectory,
                    acceptanceCriteria: acceptanceCriteria);
            if (!plannerContract.Succeeded || plannerContract.Plan is null)
            {
                exitCode = 1;
                completionContractSucceeded = false;
                var rejectionDiagnostic = plannerContract.Diagnostic;
                if (!PlannerOutputContract.TryPersistRejectionDiagnostic(
                        processRecord.StandardErrorPath,
                        rejectionDiagnostic,
                        out var persistenceDiagnostic))
                {
                    rejectionDiagnostic = AppendDiagnostic(
                        rejectionDiagnostic,
                        $"Full Planner rejection could not be persisted to '{processRecord.StandardErrorPath}': {persistenceDiagnostic}");
                }

                finalPlannerRejectionDiagnostic = rejectionDiagnostic;
                standardErrorDiagnostic = AppendDiagnostic(
                    standardErrorDiagnostic ?? string.Empty,
                    DispatchFailureDiagnosticMarker.Format(DispatchFailureDiagnosticMarker.PlannerOutputContractRejected));
            }
            else
            {
                var durableSource = plannerContract.IngestedPath ?? processRecord.StandardOutputPath;
                if (!PlannerOutputContract.TryPersistDurableReceipt(
                        processRecord.StandardOutputPath,
                        durableSource,
                        plannerContract.Plan,
                        out var appendDiagnostic))
                {
                    exitCode = 1;
                    completionContractSucceeded = false;
                    standardErrorDiagnostic = AppendDiagnostic(
                        AppendDiagnostic(
                            standardErrorDiagnostic ?? string.Empty,
                            $"Planner output contract could not persist the accepted plan: {appendDiagnostic}. Retry Planner for contract repair."),
                        DispatchFailureDiagnosticMarker.Format(DispatchFailureDiagnosticMarker.PlannerPlanPersistenceFailed));
                }
            }
        }

        var providerFailureKind = ParseProviderFailureKind(task.LastDispatch, observedExitCode, decisionStandardOutput, decisionStandardError);
        var workerResultPresent = HasWorkerResultArtifact(
            processRecord.WorkingDirectory,
            decisionStandardOutput);
        var hasCommittedChanges = false;
        var orchestratorCommitted = false;
        var completedWorktreeInspection = RequiresFileChangeEvidence(task)
            ? InspectGoalWorktree(
                processRecord.WorkingDirectory,
                goalId,
                task.LastDispatch!.DispatchedAt,
                forceRefresh: true)
            : null;
        var worktreeEvidenceAvailable = completedWorktreeInspection is { IsAvailable: true };
        var initialWorktreeEvidence = worktreeEvidenceAvailable
            ? completedWorktreeInspection!.Evidence
            : GoalWorktreeDispatchEvidence.Unknown;
        hasCommittedChanges = initialWorktreeEvidence.HasRelevantCommitAfterDispatch;
        var hasQualifyingDirtyChanges =
            worktreeEvidenceAvailable &&
            !initialWorktreeEvidence.IsClean &&
            initialWorktreeEvidence.DirtyPaths.Count > 0;
        var relevantChangeEvidenceAvailable =
            worktreeEvidenceAvailable && (hasCommittedChanges || hasQualifyingDirtyChanges);
        var reconciliationOriginRule = successfulChildResultAvailable
            ? ClassifyReconciliationOriginRule(
                task,
                processRecord,
                observedExitCode,
                decisionStandardOutput,
                decisionStandardError,
                workerResultPresent,
                hasCommittedChanges,
                providerFailureKind,
                childExitRecord)
            : null;
        var reconcileWrapperExit = ShouldReconcileWrapperExit(new WrapperExitReconciliationEvidence(
            observedExitCode,
            hasChildExitRecord ? childExitRecord.ExitCode : null,
            completeNonBlockedWorkerResult,
            completionContractSucceeded,
            hasRoleCapability,
            dispatchRoleCapability,
            relevantChangeEvidenceAvailable,
            !string.IsNullOrWhiteSpace(orchestratorFailureReason)));
        if (completedWorktreeInspection is { IsAvailable: true, Evidence: var worktreeEvidence })
        {
            // Default path: a Developer/Tester that edited the worktree and showed verification
            // evidence does not need to self-commit. The orchestrator stages and commits the dirty
            // diff after guards pass. Dirty-but-unverified edits are left dirty and fail.
            var originalExitCode = exitCode;
            var commitAttempted = false;
            var commitAttempt = default(CommitWorktreeEditsResult);
            var sandboxCommitBlocked = HasSandboxCommitBlockedEvidence(
                task.RequiredRole,
                decisionStandardOutput,
                decisionStandardError);
            var sandboxCommitOnBehalfEvidence =
                sandboxCommitBlocked || providerFailureKind == ProviderFailureKind.Sandbox1312;
            var lowIntegrityConfinementEvidence = HasLowIntegrityConfinementEvidence(
                task.LastDispatch,
                processRecord,
                decisionStandardError,
                sandboxCommitOnBehalfEvidence);
            var successfulWorkerResult = completeNonBlockedWorkerResult;
            if (TryFindFailedWorkerBuildCheck(
                    processRecord.WorkingDirectory,
                    decisionStandardOutput,
                    decisionStandardError,
                    out var failedBuildCheckDiagnostic))
            {
                exitCode = 1;
                standardErrorDiagnostic = AppendDiagnostic(
                    AppendDiagnostic(
                        standardErrorDiagnostic ?? string.Empty,
                        failedBuildCheckDiagnostic),
                    DispatchFailureDiagnosticMarker.Format(DispatchFailureDiagnosticMarker.WorkerBuildCheckFailed));
            }

            var provider = ResolveWorkerProvider(task.LastDispatch);
            var normalIntegrityCommitEvidence =
                task.LastDispatch.SandboxLowIntegrity != true &&
                (successfulWorkerResult || worktreeEvidence.HasRelevantCommitAfterDispatch);
            var shouldCommitDirtyWorktree =
                (recoveryDecision?.Action != DispatchRecoveryAction.PreserveInterruptedWork || reconcileWrapperExit) &&
                ((exitCode == 0 && (normalIntegrityCommitEvidence || lowIntegrityConfinementEvidence)) ||
                 reconcileWrapperExit ||
                 (task.LastDispatch.SandboxLowIntegrity && sandboxCommitOnBehalfEvidence) ||
                 (originalExitCode != 0 && successfulWorkerResult && !provider.Capabilities.CanSelfCommit && lowIntegrityConfinementEvidence));

            if (!worktreeEvidence.IsClean &&
                shouldCommitDirtyWorktree)
            {
                commitAttempt = TryCommitWorktreeEdits(
                    processRecord.WorkingDirectory,
                    BuildOrchestratorCommitSubject(task, decisionStandardOutput, decisionStandardError),
                    worktreeEvidence.DirtyPaths);
                commitAttempted = true;
                if (commitAttempt.Succeeded &&
                    TryInspectGoalWorktree(
                        processRecord.WorkingDirectory,
                        goalId,
                        task.LastDispatch!.DispatchedAt,
                        out worktreeEvidence,
                        forceRefresh: true) &&
                    worktreeEvidence.IsClean && worktreeEvidence.HasRelevantCommitAfterDispatch)
                {
                    orchestratorCommitted = true;
                    hasCommittedChanges = true;
                    if (!reconcileWrapperExit)
                    {
                        exitCode = 0;
                    }
                    standardErrorDiagnostic = AppendDiagnostic(
                        standardErrorDiagnostic ?? string.Empty,
                        "Orchestrator committed the worker's verified worktree edits. " +
                        $"branch={worktreeEvidence.Branch}; head={worktreeEvidence.Head}; commits_after_dispatch={worktreeEvidence.CommitsAfterDispatch}.");
                    if (sandboxCommitBlocked)
                    {
                        standardErrorDiagnostic = AppendDiagnostic(
                            standardErrorDiagnostic,
                            "Classified worker git metadata write failure as non-fatal; orchestrator commit-on-behalf is the commit path. " +
                            $"index_lock={TryResolveIndexLockPath(processRecord.WorkingDirectory)}.");
                    }
                    else if (originalExitCode != 0 && successfulWorkerResult && !provider.Capabilities.CanSelfCommit)
                    {
                        standardErrorDiagnostic = AppendDiagnostic(
                            standardErrorDiagnostic,
                            "Accepted non-zero worker exit because a complete non-failing WORKER_RESULT and dirty worktree edits were present; " +
                            "orchestrator commit-on-behalf is the commit path.");
                    }
                }
            }

            if (!orchestratorCommitted && !worktreeEvidence.IsClean && (exitCode == 0 || reconcileWrapperExit))
            {
                // Exited 0 but left uncommitted edits the orchestrator could not land (no verification
                // evidence, or the commit failed) — not acceptable.
                exitCode = 1;
                if (task.LastDispatch.SandboxLowIntegrity && !lowIntegrityConfinementEvidence)
                {
                    standardErrorDiagnostic = AppendDiagnostic(
                        standardErrorDiagnostic ?? string.Empty,
                        "Low-integrity dispatch exited 0 with a dirty worktree, but deterministic low-integrity confinement evidence was absent; refusing orchestrator commit-on-behalf.");
                }

                if (commitAttempted && !commitAttempt.Succeeded && commitAttempt.Diagnostic.Length > 0)
                {
                    standardErrorDiagnostic = AppendDiagnostic(
                        standardErrorDiagnostic ?? string.Empty,
                        BuildCommitOnBehalfFailureDiagnostic(commitAttempt.Diagnostic, worktreeEvidence));
                }
                else
                {
                    standardErrorDiagnostic = AppendDiagnostic(
                        standardErrorDiagnostic ?? string.Empty,
                        "Developer/Tester dispatch exited 0 but left the worktree dirty. " +
                        $"branch={worktreeEvidence.Branch}; head={worktreeEvidence.Head}; worktree={worktreeEvidence.WorktreeStatus}; " +
                        $"commits_after_dispatch={worktreeEvidence.CommitsAfterDispatch}; status_short={worktreeEvidence.StatusShort}.");
                }
            }
            else if (!orchestratorCommitted && worktreeEvidence.IsClean)
            {
                var reconciledRoleStillRequiresChangeEvidence =
                    reconcileWrapperExit &&
                    dispatchRoleCapability == DispatchRoleOutputCapability.RequiresChangeEvidence;
                var requiresCommitEvidence =
                    RequiresPostDispatchCommitEvidence(task, decisionStandardOutput, decisionStandardError, workerResultPresent) &&
                    (reconciledRoleStillRequiresChangeEvidence ||
                     (!HasCompletedVerification(decisionStandardOutput, decisionStandardError) &&
                      !AllowsNoChangeCompletion(task, decisionStandardOutput, decisionStandardError))) &&
                    !worktreeEvidence.HasRelevantCommitAfterDispatch;

                if (requiresCommitEvidence)
                {
                    // The role had to land a relevant change and didn't — fail regardless of exit code
                    // (a Developer that produced nothing is a real failure, not exit-code noise).
                    exitCode = 1;
                    standardErrorDiagnostic = AppendDiagnostic(
                        AppendDiagnostic(
                            standardErrorDiagnostic ?? string.Empty,
                            "Developer/Tester dispatch did not produce required relevant file-change evidence. " +
                            $"branch={worktreeEvidence.Branch}; head={worktreeEvidence.Head}; worktree={worktreeEvidence.WorktreeStatus}; " +
                            $"commits_after_dispatch={worktreeEvidence.CommitsAfterDispatch}; changed_paths={worktreeEvidence.ChangedPathsSummary}."),
                        DispatchFailureDiagnosticMarker.Format(DispatchFailureDiagnosticMarker.RequiredFileChangeEvidenceMissing));
                }
            }

            // WORKER_RESULT is advisory only. Substance is proven from git ground truth
            // (relevant commit after dispatch + clean worktree, checked above) and the
            // acceptance test run — not from the worker's self-reported field shape, which
            // produced recurring false-failures across many distinct WORKER_RESULT schemas.
            // The self-report is still parsed for the model-fit note when recording the
            // verification (TaskSpec.RecordVerification via ModelFitEvidence); it never
            // gates the dispatch.
        }
        else if (completedWorktreeInspection is { IsAvailable: false } unavailableInspection)
        {
            standardErrorDiagnostic = AppendDiagnostic(
                standardErrorDiagnostic ?? string.Empty,
                $"Completed dispatch worktree inspection {(unavailableInspection.IsUnsafe ? "unsafe" : "unavailable")}; " +
                $"unavailable_reason={unavailableInspection.UnavailableReason ?? "unknown"}; " +
                $"git_receipt={unavailableInspection.GitReceipt}.");
            if (string.Equals(unavailableInspection.UnavailableReason, "git-inspection-failed", StringComparison.Ordinal))
            {
                exitCode = 1;
                standardErrorDiagnostic = AppendDiagnostic(
                    standardErrorDiagnostic ?? string.Empty,
                    DispatchFailureDiagnosticMarker.Format(DispatchFailureDiagnosticMarker.WorktreeInspectionFailed));
            }
        }

        if (reconcileWrapperExit &&
            (dispatchRoleCapability != DispatchRoleOutputCapability.RequiresChangeEvidence || hasCommittedChanges))
        {
            var exitCodeEvidenceName = exitArtifactAlreadyExisted
                ? "observed_wrapper_exit_code"
                : "synthesized_wrapper_exit_code";
            exitCode = 0;
            wrapperExitReconciled = true;
            standardErrorDiagnostic = AppendDiagnostic(
                standardErrorDiagnostic ?? string.Empty,
                "Reconciled non-zero wrapper completion because the selected child and its complete, non-blocked WORKER_RESULT succeeded; " +
                $"{exitCodeEvidenceName}={observedExitCode}; child_exit_code=0; logical_exit_code=0.");
        }
        else if (observedExitCode != 0 &&
                 hasChildExitRecord &&
                 childExitRecord.ExitCode == 0 &&
                 !completeNonBlockedWorkerResult)
        {
            var exitCodeEvidenceName = exitArtifactAlreadyExisted
                ? "observed_wrapper_exit_code"
                : "synthesized_wrapper_exit_code";
            standardErrorDiagnostic = AppendDiagnostic(
                AppendDiagnostic(
                    standardErrorDiagnostic ?? string.Empty,
                    "Wrapper process exited nonzero after the selected child succeeded, but no complete, usable, non-blocked WORKER_RESULT was available; " +
                    $"{exitCodeEvidenceName}={observedExitCode}; child_exit_code=0."),
                DispatchFailureDiagnosticMarker.Format(DispatchFailureDiagnosticMarker.WrapperProcessExitFailure));
        }
        if (task.LastDispatch is { } completedDispatch && !IsLocalDispatch(completedDispatch))
        {
            var reapNote = ReapWorktreeBuildDaemons(processRecord.WorkingDirectory);
            if (reapNote is not null)
            {
                standardErrorDiagnostic = AppendDiagnostic(standardErrorDiagnostic ?? string.Empty, reapNote);
            }
        }

        if (resourceAccounting is not null)
        {
            standardErrorDiagnostic = AppendDiagnostic(
                standardErrorDiagnostic ?? string.Empty,
                FormatResourceReceipt(goalId, taskId, resourceAccounting));
        }

        if (!string.IsNullOrWhiteSpace(orchestratorFailureReason))
        {
            // The exit artifact records what the dispatch host observed. A detector disposition is a
            // separate fact and must not rewrite that artifact or fabricate a contradictory exit code.
            exitCode = observedExitCode;
        }

        var humanInputDirective = AgentOutputDirectives.ParseHumanInputRequest(decisionStandardOutput, task.RequiredRole);
        var humanInputQuestion = humanInputDirective.Directive?.Question;
        // Keep orchestrator-ingested plan text in the captured stdout artifact, whose path is
        // recorded below, but out of the worker decision stream and bounded verification
        // snapshot. Kernel classification reparses the snapshot for directives and blockers.
        var standardOutput = outputSnapshot.BoundedText;
        var standardError = AppendDiagnostic(
            AppendDiagnostic(errorSnapshot.BoundedText, standardErrorDiagnostic),
            finalPlannerRejectionDiagnostic);
        if (!exitArtifactAlreadyExisted)
        {
            TryWriteExitCode(
                processRecord.ExitCodePath,
                exitCode,
                recoveryDecision is null
                    ? "orchestrator synthesized completion without a host artifact"
                    : $"orchestrator recovery action={recoveryDecision.ActionName}");
        }
        _ = DispatchExitArtifacts.TryRead(processRecord.ExitCodePath, out var exitArtifact);
        var completed = processRecord with
        {
            CompletedAt = _clock.UtcNow,
            ExitCode = exitCode,
            ResourceAccounting = resourceAccounting,
            ChildProcessId = hasChildExitRecord ? childExitRecord.ProcessId : null,
            ChildExitCode = hasChildExitRecord ? childExitRecord.ExitCode : null,
            ExitArtifactOrigin = exitArtifact?.Origin ?? DispatchExitArtifactOrigin.None,
            ExitArtifactReason = exitArtifact?.Reason
        };

        // Capture resultCommit after all orchestrator commits — the right boundary for file attribution.
        var resultCommit = TryGetWorktreeHead(processRecord.WorkingDirectory);
        var resultCommitProvenance = hasCommittedChanges
            ? orchestratorCommitted ? "orchestrator" : "worker"
            : null;

        // Worker-self-reported stdout bytes from the heartbeat — a flush-race-proof signal of real output.
        var heartbeatStdoutBytes = TryReadHeartbeat(GetHeartbeatPath(processRecord), out var completionHeartbeat)
            ? completionHeartbeat.StandardOutputBytes
            : (long?)null;

        var verification = new TaskVerificationRecord(
            processRecord.Command,
            processRecord.WorkingDirectory,
            exitCode,
            standardOutput,
            standardError,
            completed.CompletedAt.Value,
            StandardOutputPath: processRecord.StandardOutputPath,
            StandardErrorPath: processRecord.StandardErrorPath,
            WorkerResultPresent: workerResultPresent,
            HasCommittedChanges: hasCommittedChanges,
            HeartbeatStandardOutputBytes: heartbeatStdoutBytes,
            ProviderFailureKind: providerFailureKind,
            HumanInputQuestion: humanInputQuestion,
            DispatchStartedAt: processRecord.StartedAt,
            ChildProcessId: completed.ChildProcessId,
            ChildExitCode: completed.ChildExitCode,
            OrchestratorFailureReason: orchestratorFailureReason,
            HumanInputQuestionFingerprint: humanInputDirective.Directive?.QuestionFingerprint,
            HumanInputBlockerFingerprint: humanInputDirective.Directive?.BlockerFingerprint,
            ObservedRootExitCode: observedExitCode,
            ReconciledToSuccess: wrapperExitReconciled,
            ReconciliationOriginRule: wrapperExitReconciled ? reconciliationOriginRule : null);

        var outcome = new DispatchRefreshOutcome(
            completed,
            verification,
            resultCommit,
            resultCommitProvenance,
            recoveryDecision,
            providerFailureKind,
            new DispatchDiagnosticPayload(exitCode, standardOutput, standardError));
        EvictProcessLogCache(processRecord);
        return outcome;
    }

    private static bool ShouldReconcileWrapperExit(WrapperExitReconciliationEvidence evidence) =>
        evidence.ObservedRootExitCode != 0 &&
        evidence.ChildExitCode == 0 &&
        evidence.HasCompleteNonBlockedWorkerResult &&
        evidence.CompletionContractSucceeded &&
        evidence.HasKnownRoleCapability &&
        (evidence.RoleCapability != DispatchRoleOutputCapability.RequiresChangeEvidence ||
         evidence.HasRelevantChangeEvidence) &&
        !evidence.HasFatalOrchestratorFailure;

    private static string? ClassifyReconciliationOriginRule(
        TaskSpec task,
        TaskProcessRecord processRecord,
        int observedRootExitCode,
        string standardOutput,
        string standardError,
        bool workerResultPresent,
        bool hasCommittedChanges,
        ProviderFailureKind providerFailureKind,
        DispatchProcessHost.DispatchChildExitRecord childExitRecord)
    {
        var verification = new TaskVerificationRecord(
            processRecord.Command,
            processRecord.WorkingDirectory,
            observedRootExitCode,
            standardOutput,
            standardError,
            DateTimeOffset.UtcNow,
            StandardOutputPath: processRecord.StandardOutputPath,
            StandardErrorPath: processRecord.StandardErrorPath,
            WorkerResultPresent: workerResultPresent,
            HasCommittedChanges: hasCommittedChanges,
            ProviderFailureKind: providerFailureKind,
            DispatchStartedAt: processRecord.StartedAt,
            ChildProcessId: childExitRecord.ProcessId,
            ChildExitCode: childExitRecord.ExitCode,
            ObservedRootExitCode: observedRootExitCode);
        var origin = DispatchFailureClassifier.Classify(
            task,
            verification,
            providerFailureKind,
            workerResultPresent,
            hasCommittedChanges);
        return TaskOutcomeClassifier.TryExtractRule(origin.ClassifierReceipt);
    }

    private static bool RequiresDurableResearchArtifact(Goal goal, TaskSpec researcher)
    {
        var researcherIndex = goal.Tasks.ToList().FindIndex(candidate => candidate.Id == researcher.Id);
        var plannerIndex = goal.Tasks.ToList().FindIndex(candidate => candidate.RequiredRole == AgentRole.Planner);
        return researcherIndex >= 0 && plannerIndex > researcherIndex;
    }

    private static bool RequiresDurablePlanArtifact(Goal goal, TaskSpec planner)
    {
        var plannerIndex = goal.Tasks.ToList().FindIndex(candidate => candidate.Id == planner.Id);
        return plannerIndex > 0 &&
            goal.Tasks.Take(plannerIndex).Any(candidate => candidate.RequiredRole == AgentRole.Researcher);
    }

    private static string? TryGetWorktreeHead(string workingDirectory)
    {
        try
        {
            if (!Directory.Exists(workingDirectory))
                return null;
            var result = GitCli.Run(workingDirectory, "rev-parse", "HEAD");
            return result.Succeeded ? result.Output.Trim() : null;
        }
        catch
        {
            return null;
        }
    }

    private static DispatchSpawnReceipt BuildDispatchSpawnReceipt(TaskDispatchRecord dispatch, IWorkerProvider provider)
    {
        var command = dispatch.Command;
        string? providerSessionId = null;

        if (provider.Identity.Kind == ProviderKind.AnthropicClaudeCli)
        {
            providerSessionId = TryReadClaudeSessionId(command) ?? Guid.NewGuid().ToString();
            if (!ContainsClaudeSessionIdOption(command))
            {
                command = $"{command} --session-id {providerSessionId}";
            }
        }

        return new DispatchSpawnReceipt(
            command,
            providerSessionId,
            TryGetWorktreeHead(dispatch.WorkingDirectory),
            TryGetDirtyStateHash(dispatch.WorkingDirectory));
    }

    private static bool ContainsClaudeSessionIdOption(string command) =>
        Regex.IsMatch(command, @"(?<!\S)--session-id(?!\S)", RegexOptions.CultureInvariant);

    private static string? TryReadClaudeSessionId(string command)
    {
        var match = Regex.Match(
            command,
            @"(?<!\S)--session-id\s+(?:""(?<id>[^""]+)""|'(?<id>[^']+)'|(?<id>\S+))",
            RegexOptions.CultureInvariant);
        return match.Success && match.Groups["id"].Value is { Length: > 0 } value
            ? value
            : null;
    }

    private static string? TryGetDirtyStateHash(string workingDirectory)
    {
        try
        {
            if (!Directory.Exists(workingDirectory))
                return null;

            var result = GitCli.Run(workingDirectory, "status", "--porcelain=v1", "--untracked-files=all");
            if (!result.Succeeded)
                return null;

            var normalized = result.Output.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant();
        }
        catch
        {
            return null;
        }
    }

    internal static bool RequiresFileChangeEvidence(TaskSpec task)
    {
        return task.LastDispatch is { } dispatch &&
            !IsLocalDispatch(dispatch) &&
            task.RequiredRole is AgentRole.Developer or AgentRole.Tester;
    }

    private DispatchRefreshOutcome CompleteHungWrapperDispatch(
        AgentOrchestratorKernel kernel,
        GoalId goalId,
        TaskId taskId,
        TaskSpec task,
        TaskProcessRecord processRecord,
        string hungDiagnostic,
        DispatchRecoveryDecision recoveryDecision)
    {
        var resourceAccounting = ReapTrackedProcessJobs(processRecord, waitForExit: true);
        var exitCode = 1;
        var completionDiagnostic = hungDiagnostic;

        if (TryBuildHungWrapperRescueNote(task, processRecord, goalId, out var rescueNote, out var deniedNote))
        {
            exitCode = 0;
            completionDiagnostic = AppendDiagnostic(completionDiagnostic, rescueNote);
        }
        else if (!string.IsNullOrWhiteSpace(deniedNote))
        {
            completionDiagnostic = AppendDiagnostic(completionDiagnostic, deniedNote);
        }

        return BuildCompletedProcessOutcome(
            kernel,
            goalId,
            taskId,
            processRecord,
            exitCode,
            completionDiagnostic,
            WithAction(DispatchRecoveryAction.Reap, recoveryDecision, completionDiagnostic),
            resourceAccounting);
    }

    private DispatchRefreshOutcome CompleteSuspectedHangDispatch(
        AgentOrchestratorKernel kernel,
        GoalId goalId,
        TaskId taskId,
        TaskSpec task,
        TaskProcessRecord processRecord,
        string detectorDiagnostic,
        DispatchRecoveryAction recoveryAction,
        DispatchRecoveryDecision recoveryDecision)
    {
        var resourceAccounting = ReapTrackedProcessJobs(processRecord, waitForExit: true);
        var exitRead = ReadExitCodeWithRetry(processRecord.ExitCodePath);
        var exitCode = exitRead.Kind == ExitCodeReadKind.Valid
            ? exitRead.ExitCode!.Value
            : 1;
        var completionDiagnostic = detectorDiagnostic;
        string? orchestratorFailureReason = detectorDiagnostic;

        if (TryBuildHungWrapperRescueNote(task, processRecord, goalId, out var rescueNote, out var deniedNote))
        {
            exitCode = exitRead.Kind == ExitCodeReadKind.Valid
                ? exitRead.ExitCode!.Value
                : 0;
            orchestratorFailureReason = null;
            completionDiagnostic = AppendDiagnostic(completionDiagnostic, rescueNote);
        }
        else if (!string.IsNullOrWhiteSpace(deniedNote))
        {
            completionDiagnostic = AppendDiagnostic(completionDiagnostic, deniedNote);
        }

        return BuildCompletedProcessOutcome(
            kernel,
            goalId,
            taskId,
            processRecord,
            exitCode,
            completionDiagnostic,
            WithAction(recoveryAction, recoveryDecision, completionDiagnostic),
            resourceAccounting,
            orchestratorFailureReason);
    }

    private bool TryBuildHungWrapperRescueNote(
        TaskSpec task,
        TaskProcessRecord processRecord,
        GoalId goalId,
        out string rescueNote,
        out string deniedNote)
    {
        rescueNote = string.Empty;
        deniedNote = string.Empty;

        var standardOutput = ReadProcessLogBestEffort(processRecord, processRecord.StandardOutputPath).DecisionText;
        var standardError = ReadProcessLogBestEffort(processRecord, processRecord.StandardErrorPath).DecisionText;
        var hasPopulatedStandardOutput =
            SafeFileLength(processRecord.StandardOutputPath) > 0L ||
            (TryReadHeartbeat(GetHeartbeatPath(processRecord), out var heartbeat) &&
             heartbeat.StandardOutputBytes > 0L);
        var hasSuccessfulWorkerResult = HasSuccessfulWorkerResult(
            processRecord.WorkingDirectory,
            standardOutput,
            standardError,
            allowNoChangedFiles: true,
            requireNoBlockers: true,
            DispatchRoleOutputCapabilities.TryGet(task.RequiredRole, out var roleCapability)
                ? roleCapability
                : null);
        if (CanCompleteHungWrapperWithoutChangeEvidence(
                task,
                hasPopulatedStandardOutput,
                hasSuccessfulWorkerResult,
                out var capabilityGapDiagnostic))
        {
            rescueNote =
                $"Wrapper process reaped; task completed because read-only role {task.RequiredRole} " +
                "produced populated standard output with a complete, non-blocked WORKER_RESULT.";
            return true;
        }

        if (!string.IsNullOrWhiteSpace(capabilityGapDiagnostic))
        {
            deniedNote = capabilityGapDiagnostic;
            return false;
        }

        if (DispatchRoleOutputCapabilities.TryGet(task.RequiredRole, out var capability) &&
            capability == DispatchRoleOutputCapability.ReadOnly)
        {
            deniedNote =
                $"Hung-wrapper rescue denied for read-only role {task.RequiredRole}: completion requires " +
                "populated standard output and a complete, non-blocked WORKER_RESULT.";
            return false;
        }

        if (RequiresFileChangeEvidence(task) &&
            task.LastDispatch is { } dispatch &&
            InspectGoalWorktree(processRecord.WorkingDirectory, goalId, dispatch.DispatchedAt) is
                { IsAvailable: true, Evidence: var worktreeEvidence } &&
            worktreeEvidence.IsClean && worktreeEvidence.HasRelevantCommitAfterDispatch)
        {
            rescueNote =
                "Wrapper process reaped; task completed based on relevant file-change evidence " +
                $"(branch={worktreeEvidence.Branch}; head={worktreeEvidence.Head}; " +
                $"commits_after_dispatch={worktreeEvidence.CommitsAfterDispatch}).";
            return true;
        }

        return false;
    }

    internal static bool CanCompleteHungWrapperWithoutChangeEvidence(
        TaskSpec task,
        bool hasPopulatedStandardOutput,
        bool hasSuccessfulWorkerResult,
        out string? capabilityGapDiagnostic)
    {
        var recognized = DispatchRoleOutputCapabilities.TryGet(task.RequiredRole, out var capability);
        capabilityGapDiagnostic = recognized
            ? null
            : "HungWrapperUnrecognizedRoleCapability: no completion-evidence capability is mapped for " +
              $"role value '{(int)task.RequiredRole}' ({task.RequiredRole}); refusing rescue.";
        return recognized &&
            capability == DispatchRoleOutputCapability.ReadOnly &&
            hasPopulatedStandardOutput &&
            hasSuccessfulWorkerResult;
    }

    private static void RecordProviderSessionFromHeartbeat(
        AgentOrchestratorKernel kernel,
        GoalId goalId,
        TaskId taskId,
        TaskSpec task,
        DispatchHeartbeat? heartbeat)
    {
        if (heartbeat is null ||
            string.IsNullOrWhiteSpace(heartbeat.ProviderSessionId) ||
            !string.IsNullOrWhiteSpace(task.LastDispatch?.ProviderSessionId))
        {
            return;
        }

        kernel.RecordDispatchProviderSessionId(goalId, taskId, heartbeat.ProviderSessionId);
    }

    private static bool RequiresPostDispatchCommitEvidence(
        TaskSpec task,
        string standardOutput,
        string standardError,
        bool workerResultPresent)
    {
        return task.RequiredRole switch
        {
            AgentRole.Developer => true,
            AgentRole.Tester => !IsVerificationOnlyTesterCompletion(task, standardOutput, standardError, workerResultPresent),
            _ => false
        };
    }

    private static bool IsVerificationOnlyTesterCompletion(
        TaskSpec task,
        string standardOutput,
        string standardError,
        bool workerResultPresent)
    {
        return task.RequiredRole == AgentRole.Tester &&
            !TesterTaskRequestsFileChanges(task) &&
            HasCompletedVerification(standardOutput, standardError);
    }

    // A verification-role worker proves it did its job with recognised verification evidence.
    // WORKER_RESULT shape alone is not enough: evidence-less clean dispatches must fail so the
    // orchestrator does not convert a well-formed self-report into proof that checks actually passed.
    private static bool HasClassifiedVerificationEvidence(string standardOutput, string standardError)
    {
        return DispatchFailureClassifier.HasVerificationEvidenceInOutput(standardOutput, standardError);
    }

    private static bool HasCompletedVerification(string standardOutput, string standardError)
    {
        return HasClassifiedVerificationEvidence(standardOutput, standardError) &&
            !TryFindFailingTestsInWorkerResult($"{standardOutput}\n{standardError}", out _);
    }

    private static bool HasSandboxCommitBlockedEvidence(
        AgentRole role,
        string standardOutput,
        string standardError)
    {
        return DispatchFailureClassifier.IsSandboxCommitBlockedFailure(
            role,
            1,
            standardOutput,
            standardError);
    }

    private static bool HasLowIntegrityConfinementEvidence(
        TaskDispatchRecord? dispatch,
        TaskProcessRecord processRecord,
        string standardError,
        bool sandboxCommitOnBehalfEvidence)
    {
        if (dispatch?.SandboxLowIntegrity != true)
        {
            return false;
        }

        return sandboxCommitOnBehalfEvidence ||
            HasCompletedSandboxPreparationEvent(standardError) ||
            HasLowIntegritySetupArtifact(processRecord.WorkingDirectory);
    }

    private static bool HasCompletedSandboxPreparationEvent(string standardError)
    {
        foreach (var line in standardError.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!line.Contains("sandbox-prep", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (root.TryGetProperty("event", out var evt) &&
                    root.TryGetProperty("phase", out var phase) &&
                    string.Equals(evt.GetString(), "sandbox-prep", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(phase.GetString(), "complete", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            catch (JsonException)
            {
            }
        }

        return false;
    }

    private static bool HasLowIntegritySetupArtifact(string workingDirectory)
    {
        try
        {
            return File.Exists(Path.Combine(workingDirectory, ".mcg-sandbox", DispatchProcessHost.LowIntegritySetupArtifactName));
        }
        catch
        {
            return false;
        }
    }

    private ProviderFailureKind ParseProviderFailureKind(
        TaskDispatchRecord? dispatch,
        int exitCode,
        string standardOutput,
        string standardError)
    {
        if (dispatch is null)
        {
            return ProviderFailureKind.Unknown;
        }

        return ResolveWorkerProvider(dispatch).ParseOutcome(new WorkerProviderOutcome(
            exitCode,
            standardOutput,
            standardError));
    }

    private static string TryResolveIndexLockPath(string workingDirectory)
    {
        try
        {
            return GoalWorktrees.InspectGitMetadataAccess(workingDirectory).IndexLockPath;
        }
        catch
        {
            return Path.Combine(workingDirectory, ".git", "index.lock");
        }
    }

    private static bool HasWorkerResultArtifact(string workingDirectory, string standardOutput)
    {
        if (WorkerResultParser.TryParseFields(standardOutput, out _, out _))
        {
            return true;
        }

        foreach (var fileName in new[] { "WORKER_RESULT.md", "WORKER_RESULT.txt" })
        {
            var path = Path.Combine(workingDirectory, fileName);
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                if (WorkerResultParser.TryParseFields(ReadDecisionBestEffort(path), out _, out _))
                {
                    return true;
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return false;
    }

    private static bool HasSuccessfulWorkerResult(
        string workingDirectory,
        string standardOutput,
        string standardError,
        bool allowNoChangedFiles = false,
        bool requireNoBlockers = false,
        DispatchRoleOutputCapability? roleCapability = null)
    {
        if (WorkerResultParser.TryParseSuccessfulResult(
                $"{standardOutput}\n{standardError}",
                out _,
                out _,
                allowNoChangedFiles,
                requireNoBlockers,
                allowReadOnlyTestStatuses: roleCapability == DispatchRoleOutputCapability.ReadOnly))
        {
            return true;
        }

        foreach (var fileName in new[] { "WORKER_RESULT.md", "WORKER_RESULT.txt" })
        {
            var path = Path.Combine(workingDirectory, fileName);
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                if (WorkerResultParser.TryParseSuccessfulResult(
                        ReadDecisionBestEffort(path),
                        out _,
                        out _,
                        allowNoChangedFiles,
                        requireNoBlockers,
                        allowReadOnlyTestStatuses: roleCapability == DispatchRoleOutputCapability.ReadOnly))
                {
                    return true;
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return false;
    }

    private static bool TryFindFailedWorkerBuildCheck(
        string workingDirectory,
        string standardOutput,
        string standardError,
        out string diagnostic)
    {
        if (TryFindFailedWorkerBuildCheckInText($"{standardOutput}\n{standardError}", out diagnostic))
        {
            return true;
        }

        foreach (var fileName in new[] { "WORKER_RESULT.md", "WORKER_RESULT.txt" })
        {
            var path = Path.Combine(workingDirectory, fileName);
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                if (TryFindFailedWorkerBuildCheckInText(ReadDecisionBestEffort(path), out diagnostic))
                {
                    return true;
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        diagnostic = string.Empty;
        return false;
    }

    private static bool TryFindFailedWorkerBuildCheckInText(string text, out string diagnostic)
    {
        if (!WorkerResultParser.TryParseResult(text, out var result, out _) ||
            !WorkerResultParser.WorkerBuildCheckTestsReportFailure(result, out var tests))
        {
            diagnostic = string.Empty;
            return false;
        }

        diagnostic = $"WORKER_RESULT reported failed worker build check: {tests}";
        return true;
    }

    private static bool TryFindFailingTestsInWorkerResult(string text, out string tests)
    {
        tests = string.Empty;
        return WorkerResultParser.TryParseResult(text, out var result, out _) &&
            WorkerResultParser.TestsReportFailure(result, out tests);
    }

    private static bool TesterTaskRequestsFileChanges(TaskSpec task)
    {
        var text = $"{task.Description}\n{task.VerificationPlan}".ToLowerInvariant();
        return Regex.IsMatch(
            text,
            @"\b(add|create|write|implement|update|modify|edit|fix)\b.{0,80}\b(test|tests|coverage|fixture|fixtures|source|file|files)\b|" +
            @"\b(test|tests|coverage|fixture|fixtures|source|file|files)\b.{0,80}\b(add|create|write|implement|update|modify|edit|fix)\b",
            RegexOptions.CultureInvariant);
    }

    private static bool HasExplicitNoChangeRationale(string standardOutput, string standardError)
    {
        var output = $"{standardOutput}\n{standardError}";
        return output.Contains("NO_CHANGE:", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("No-change rationale:", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("No changes needed:", StringComparison.OrdinalIgnoreCase);
    }

    private static bool AllowsNoChangeCompletion(TaskSpec task, string standardOutput, string standardError)
    {
        return task.RequiredRole != AgentRole.Developer &&
            HasExplicitNoChangeRationale(standardOutput, standardError);
    }

    // Commits the worker's uncommitted worktree edits from the orchestrator after verification guards
    // pass. The dirty path list is filtered from git status so generated/noise paths are not absorbed
    // into the recovery commit.
    private static CommitWorktreeEditsResult TryCommitWorktreeEdits(string workingDirectory, string subject, IReadOnlyList<string> dirtyPaths)
    {
        try
        {
            if (dirtyPaths.Count == 0)
            {
                return CommitWorktreeEditsResult.Failed("Orchestrator commit-on-behalf skipped: no commit-worthy dirty paths.");
            }

            var addArgs = new List<string>(dirtyPaths.Count + 3) { "add", "-A", "--" };
            addArgs.AddRange(dirtyPaths);
            var add = GitCli.Run(workingDirectory, addArgs.ToArray());
            if (!add.Succeeded)
            {
                return CommitWorktreeEditsResult.FromGitFailure("add", addArgs, add);
            }

            var staged = GitCli.Run(workingDirectory, "diff", "--cached", "--name-only");
            if (staged.ExitCode != 0 || string.IsNullOrWhiteSpace(staged.Output))
            {
                // Nothing to commit (e.g. only the excluded sandbox scratch was dirty) — leave the
                // dispatch to fail/report rather than create an empty commit.
                return staged.ExitCode == 0
                    ? CommitWorktreeEditsResult.Failed("Orchestrator commit-on-behalf found no staged changes after git add.")
                    : CommitWorktreeEditsResult.FromGitFailure("diff", ["diff", "--cached", "--name-only"], staged);
            }

            var substantive = GitCli.Run(
                workingDirectory,
                "diff",
                "--cached",
                "--ignore-all-space",
                "--quiet",
                "--exit-code",
                "--");
            if (substantive.ExitCode == 0)
            {
                return CommitWorktreeEditsResult.Failed("Orchestrator commit-on-behalf found no substantive staged changes after ignoring whitespace.");
            }

            if (substantive.ExitCode != 1)
            {
                return CommitWorktreeEditsResult.FromGitFailure(
                    "diff",
                    ["diff", "--cached", "--ignore-all-space", "--quiet", "--exit-code", "--"],
                    substantive);
            }

            var commitArgs = new[] { "commit", "-m", subject };
            var commit = GitCli.Run(workingDirectory, commitArgs);
            return commit.Succeeded
                ? CommitWorktreeEditsResult.Success
                : CommitWorktreeEditsResult.FromGitFailure("commit", commitArgs, commit);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return CommitWorktreeEditsResult.Failed(
                $"Orchestrator commit-on-behalf failed with {ex.GetType().Name}: {NormalizeDiagnosticText(ex.Message)}");
        }
    }

    private static string BuildOrchestratorCommitSubject(TaskSpec task, string standardOutput, string standardError)
    {
        var title = NormalizeCommitSubjectPart(task.Description);
        var summary = ExtractWorkerSummary(standardOutput, standardError);
        var subject = string.IsNullOrWhiteSpace(summary)
            ? title
            : $"{title}: {summary}";
        return TruncateCommitSubject(subject);
    }

    private static string ExtractWorkerSummary(string standardOutput, string standardError)
    {
        if (WorkerResultParser.TryParseFields($"{standardOutput}\n{standardError}", out var fields, out _) &&
            fields.TryGetValue("summary", out var summary))
        {
            return NormalizeCommitSubjectPart(summary);
        }

        foreach (var line in $"{standardOutput}\n{standardError}".Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 ||
                WorkerResultParser.IsOpener(trimmed) ||
                WorkerResultParser.IsEndMarker(trimmed) ||
                trimmed.Contains(':', StringComparison.Ordinal))
            {
                continue;
            }

            return NormalizeCommitSubjectPart(trimmed);
        }

        return string.Empty;
    }

    private static string NormalizeCommitSubjectPart(string value)
    {
        return Regex.Replace(value.Trim(), @"\s+", " ");
    }

    private static string TruncateCommitSubject(string subject)
    {
        const int MaxSubjectLength = 72;
        subject = NormalizeCommitSubjectPart(subject);
        return subject.Length <= MaxSubjectLength
            ? subject
            : subject[..MaxSubjectLength].TrimEnd();
    }

    private static string BuildCommitOnBehalfFailureDiagnostic(
        string gitFailureDiagnostic,
        GoalWorktreeDispatchEvidence worktreeEvidence)
    {
        return gitFailureDiagnostic + " " +
            "Developer/Tester dispatch exited 0 but left the worktree dirty. " +
            "Commit-on-behalf failure is retryable; worktree preserved. " +
            "operator_action=inspect the preserved worktree, resolve the named git failure, then rerun refresh-dispatch for this task; " +
            $"branch={worktreeEvidence.Branch}; head={worktreeEvidence.Head}; worktree={worktreeEvidence.WorktreeStatus}; " +
            $"commits_after_dispatch={worktreeEvidence.CommitsAfterDispatch}; status_short={worktreeEvidence.StatusShort}.";
    }

    private GoalWorktreeInspectionResult InspectGoalWorktree(
        string workingDirectory,
        GoalId goalId,
        DateTimeOffset dispatchedAt,
        bool forceRefresh = false)
    {
        var key = new WorktreeInspectionCacheKey(workingDirectory, goalId, dispatchedAt);
        if (!forceRefresh && _worktreeInspectionCache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        _beforeGoalWorktreeInspection?.Invoke();
        var result = InspectGoalWorktreeCore(workingDirectory, goalId, dispatchedAt);
        _worktreeInspectionCache[key] = result;
        return result;
    }

    private bool TryInspectGoalWorktree(
        string workingDirectory,
        GoalId goalId,
        DateTimeOffset dispatchedAt,
        out GoalWorktreeDispatchEvidence evidence,
        bool forceRefresh = false)
    {
        var inspection = InspectGoalWorktree(workingDirectory, goalId, dispatchedAt, forceRefresh);
        evidence = inspection.Evidence;
        return inspection.IsAvailable;
    }

    private static GoalWorktreeInspectionResult InspectGoalWorktreeCore(
        string workingDirectory,
        GoalId goalId,
        DateTimeOffset dispatchedAt)
    {
        if (!Directory.Exists(workingDirectory))
        {
            return GoalWorktreeInspectionResult.Unavailable("directory-missing", "git-not-run");
        }

        if (!File.Exists(Path.Combine(workingDirectory, ".git")))
        {
            return GoalWorktreeInspectionResult.Unavailable("git-metadata-missing", "git-not-run");
        }

        var branch = GitCli.Run(workingDirectory, "branch", "--show-current");
        var expectedBranch = GoalWorktrees.BranchName(goalId);
        if (branch.ExitCode != 0)
        {
            return GoalWorktreeInspectionResult.Unavailable(
                "branch-inspection-failed",
                BuildGitInspectionReceipt("branch", branch));
        }

        if (!string.Equals(branch.Output.Trim(), expectedBranch, StringComparison.Ordinal))
        {
            return GoalWorktreeInspectionResult.Unsafe(
                "branch-mismatch",
                $"expected={expectedBranch}; actual={NormalizeDiagnosticText(branch.Output)}");
        }

        var head = GitCli.Run(workingDirectory, "rev-parse", "--short", "HEAD");
        var status = GitCli.Run(workingDirectory, "status", "--short", "--untracked-files=all");
        var dispatch = GitCli.Run(workingDirectory, "log", "--format=%H", $"--since={dispatchedAt:O}");
        var changedPaths = GitCli.Run(workingDirectory, "log", "--name-only", "--format=", $"--since={dispatchedAt:O}");
        var failedGitOperation = new[]
        {
            (Name: "head", Result: head),
            (Name: "status", Result: status),
            (Name: "dispatch-log", Result: dispatch),
            (Name: "changed-paths", Result: changedPaths)
        }.FirstOrDefault(item => !item.Result.Succeeded || item.Result.DrainTimedOut);
        if (failedGitOperation.Name is not null)
        {
            return GoalWorktreeInspectionResult.Unavailable(
                "git-inspection-failed",
                BuildGitInspectionReceipt(failedGitOperation.Name, failedGitOperation.Result));
        }

        var commitsAfterDispatch = dispatch.Output
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Length;
        var pathsChangedAfterDispatch = changedPaths.Output
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var filteredStatusOutput = GitCli.FilterCommitWorthyStatus(status.Output);
        var evidence = new GoalWorktreeDispatchEvidence(
            branch.Output.Trim(),
            head.Output.Trim(),
            string.IsNullOrWhiteSpace(filteredStatusOutput),
            string.IsNullOrWhiteSpace(filteredStatusOutput) ? "clean" : "dirty",
            FormatStatusShort(new GitCli.GitResult(status.ExitCode, filteredStatusOutput, string.Empty)),
            commitsAfterDispatch,
            pathsChangedAfterDispatch,
            GitCli.ParseCommitWorthyStatusPaths(filteredStatusOutput));
        return GoalWorktreeInspectionResult.Available(evidence);
    }

    private static string BuildGitInspectionReceipt(string operation, GitCli.GitResult result)
    {
        var detail = string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error;
        var normalizedDetail = NormalizeDiagnosticText(detail);
        return $"operation={operation}; exit_code={result.ExitCode}; drain_timed_out={result.DrainTimedOut.ToString().ToLowerInvariant()}; " +
            $"detail={normalizedDetail[..Math.Min(normalizedDetail.Length, 256)]}";
    }

    private sealed record WorktreeInspectionCacheKey(
        string WorkingDirectory,
        GoalId GoalId,
        DateTimeOffset DispatchedAt);

    private static string FormatChangedPaths(IReadOnlyList<string> changedPaths)
    {
        if (changedPaths.Count == 0)
        {
            return "none";
        }

        var entries = changedPaths.Take(8).ToArray();
        return string.Join(" | ", entries);
    }

    private static string FormatStatusShort(GitCli.GitResult status)
    {
        if (status.ExitCode != 0)
        {
            return "unavailable";
        }

        var entries = status.Output
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Take(8)
            .ToArray();
        return entries.Length == 0
            ? "clean"
            : string.Join(" | ", entries);
    }

    public TaskProcessRecord CancelLatestProcess(
        AgentOrchestratorKernel kernel,
        GoalId goalId,
        TaskId taskId) =>
        CancelLatestProcess(kernel, goalId, taskId, cancelledByConductor: false);

    private TaskProcessRecord CancelLatestProcess(
        AgentOrchestratorKernel kernel,
        GoalId goalId,
        TaskId taskId,
        bool cancelledByConductor,
        bool bypassTrackedJobRegistry = false)
    {
        var task = kernel.GetTask(goalId, taskId);
        var processRecord = task.LastProcess
            ?? throw new InvalidOperationException($"Task '{taskId}' has no background process to cancel.");

        TaskProcessResourceAccounting? resourceAccounting = null;
        if (processRecord.IsRunning)
        {
            if (bypassTrackedJobRegistry)
            {
                resourceAccounting = SnapshotTrackedProcessAccounting(processRecord);
                TryKillTrackedProcesses(processRecord, waitForExit: true, bypassTrackedJobRegistry: true);
                if (resourceAccounting is not null)
                {
                    resourceAccounting = resourceAccounting with { Reaped = true };
                }
            }
            else
            {
                try
                {
                    resourceAccounting = ReapTrackedProcessJobs(processRecord, waitForExit: true);
                }
                catch (ArgumentException)
                {
                    // Process already exited; still record the user-requested cancellation.
                }
            }
        }

        if (!bypassTrackedJobRegistry)
        {
            resourceAccounting ??= ReleaseTrackedProcessJobs(processRecord);
        }
        var cancelled = processRecord with
        {
            CompletedAt = _clock.UtcNow,
            WasCancelled = true,
            ResourceAccounting = resourceAccounting,
            WasCancelledByConductor = cancelledByConductor
        };

        kernel.RecordTaskProcessCancelled(goalId, taskId, cancelled);
        if (resourceAccounting is not null)
        {
            kernel.RecordTaskNote(goalId, taskId, FormatResourceReceipt(goalId, taskId, resourceAccounting));
        }

        EvictProcessLogCache(processRecord);
        return cancelled;
    }

    public int CancelRunningProcessesForGoal(AgentOrchestratorKernel kernel, GoalId goalId)
    {
        var goal = kernel.GetGoal(goalId);
        var cancelled = 0;

        foreach (var task in goal.Tasks)
        {
            if (task.LastProcess is not { IsRunning: true })
            {
                continue;
            }

            CancelLatestProcess(kernel, goalId, task.Id, cancelledByConductor: true);
            cancelled++;
        }

        return cancelled;
    }

    public int DetachRunningProcessesForGoal(AgentOrchestratorKernel kernel, GoalId goalId)
    {
        var goal = kernel.GetGoal(goalId);
        var detached = 0;
        foreach (var task in goal.Tasks)
        {
            if (task.LastProcess is not { IsRunning: true } process)
            {
                continue;
            }

            if (WorkerProcessJobs.TryDetachForGracefulStop(process.ProcessId, out var detachFailure))
            {
                kernel.RecordTaskProcessGracefullyDetached(
                    goalId,
                    task.Id,
                    process with { WasGracefullyDetachedByConductor = true });
                EvictProcessLogCache(process);
                detached++;
                continue;
            }

            CancelLatestProcess(
                kernel,
                goalId,
                task.Id,
                cancelledByConductor: true,
                bypassTrackedJobRegistry: true);
            kernel.RecordTaskNote(
                goalId,
                task.Id,
                $"{detachFailure}; task marked conductor-cancelled so a successor can requeue it.");
        }

        return detached;
    }

    public int RequeueInterruptedDispatches(
        AgentOrchestratorKernel kernel,
        Func<GoalId, TaskId, InterruptedDispatchStateRead>? readCurrentState = null)
    {
        var recovered = 0;
        foreach (var goal in kernel.Goals.Where(goal =>
                     goal.Status == GoalStatus.Active || IsAutoRequeueTerminalGoalStatus(goal.Status)).ToArray())
        {
            foreach (var task in goal.Tasks.ToArray())
            {
                if (task.Status == WorkTaskStatus.Cancelled &&
                    task.LastProcess is { WasCancelledByConductor: true })
                {
                    if (task.LastProcess is { } cancelledProcess)
                    {
                        EvictProcessLogCache(cancelledProcess);
                    }

                    recovered += TryAutoRequeue(
                        kernel,
                        goal.Id,
                        task.Id,
                        "Auto-requeued interrupted dispatch after conductor loop stop.",
                        readCurrentState)
                        ? 1
                        : 0;
                    continue;
                }

                if (task.Status == WorkTaskStatus.Failed &&
                    task.LastProcess is
                    {
                        WasCancelled: false,
                        CompletedAt: not null,
                        ExitArtifactOrigin: DispatchExitArtifactOrigin.Synthetic,
                        ChildExitCode: null
                    } detachedFailure &&
                    (detachedFailure.WasGracefullyDetachedByConductor ||
                     WorkerProcessJobs.WasGracefullyDetached(
                         $"{goal.Id.Value}:{task.Id.Value}",
                         detachedFailure.ProcessId,
                         detachedFailure.StartedAt)) &&
                    task.LastVerification?.WorkerResultPresent != true &&
                    !AnyTrackedProcessStillRunning(detachedFailure))
                {
                    EvictProcessLogCache(detachedFailure);
                    recovered += TryAutoRequeue(
                        kernel,
                        goal.Id,
                        task.Id,
                        "Auto-requeued gracefully detached dispatch after synthetic missing-exit recovery.",
                        readCurrentState,
                        hasDurableGracefulDetachEvidence: true)
                        ? 1
                        : 0;
                    continue;
                }

                if (task.Status != WorkTaskStatus.Running ||
                    task.LastProcess is not { IsRunning: true } process ||
                    ReadExitCode(process.ExitCodePath).Kind != ExitCodeReadKind.Missing ||
                    AnyTrackedProcessStillRunning(process))
                {
                    continue;
                }

                EvictProcessLogCache(process);
                recovered += TryAutoRequeue(
                    kernel,
                    goal.Id,
                    task.Id,
                    "Auto-requeued orphaned running dispatch; no tracked process is alive.",
                    readCurrentState)
                    ? 1
                    : 0;
            }
        }

        return recovered;
    }

    private bool TryAutoRequeue(
        AgentOrchestratorKernel kernel,
        GoalId goalId,
        TaskId taskId,
        string message,
        Func<GoalId, TaskId, InterruptedDispatchStateRead>? readCurrentState,
        bool hasDurableGracefulDetachEvidence = false)
    {
        var currentTask = kernel.GetTask(goalId, taskId);
        var interruptedDispatch = currentTask.LastDispatch;
        if (interruptedDispatch is null)
        {
            return false;
        }

        var dispatchId = BuildDispatchId(goalId, taskId, interruptedDispatch);
        var priorSkip = kernel.GetGoal(goalId).Timeline.FirstOrDefault(evt =>
            evt.Kind == ProgressKind.TaskRequeueSkipped &&
            evt.RequeueSkipped?.DispatchId == dispatchId);
        if (currentTask.InterruptedDispatchRecoveryId == dispatchId ||
            priorSkip?.RequeueSkipped?.Reason == "terminal-state")
        {
            return false;
        }

        if (TryReadAutoRequeueBlocker(
                kernel,
                goalId,
                taskId,
                readCurrentState,
                out var blocker,
                hasDurableGracefulDetachEvidence))
        {
            kernel.RecordTaskRequeueSkipped(
                goalId,
                taskId,
                dispatchId,
                blocker.BlockingEntity,
                blocker.TerminalState,
                blocker.Reason,
                blocker.Detail);
            if (blocker.Reason == "terminal-state")
            {
                kernel.ConcludeInterruptedDispatchRecovery(
                    goalId,
                    taskId,
                    blocker.GoalStatus,
                    blocker.TaskStatus);
            }
            return false;
        }

        kernel.RequeueInterruptedDispatch(goalId, taskId, message, dispatchId);
        return true;
    }

    private static bool TryReadAutoRequeueBlocker(
        AgentOrchestratorKernel kernel,
        GoalId goalId,
        TaskId taskId,
        Func<GoalId, TaskId, InterruptedDispatchStateRead>? readCurrentState,
        out AutoRequeueBlocker blocker,
        bool hasDurableGracefulDetachEvidence = false)
    {
        InterruptedDispatchStateRead state;
        try
        {
            state = readCurrentState?.Invoke(goalId, taskId) ?? ReadCurrentState(kernel, goalId, taskId);
        }
        catch (Exception ex)
        {
            blocker = new AutoRequeueBlocker("task", null, "state-unreadable", ex.Message, null, null);
            return true;
        }

        if (!state.IsReadable || state.GoalStatus is null || state.TaskStatus is null)
        {
            blocker = new AutoRequeueBlocker(
                state.UnreadableEntity is "goal" ? "goal" : "task",
                null,
                "state-unreadable",
                state.Error ?? "current task or goal state was unavailable",
                state.GoalStatus,
                state.TaskStatus);
            return true;
        }

        if (!IsAutoRequeueTaskStatusAllowed(
                state.TaskStatus.Value,
                state.WasTaskCancelledByConductor,
                state.WasTaskGracefullyDetachedByConductor ||
                (hasDurableGracefulDetachEvidence && state.TaskStatus == WorkTaskStatus.Failed)))
        {
            blocker = new AutoRequeueBlocker(
                "task",
                state.TaskStatus.Value.ToString(),
                "terminal-state",
                null,
                state.GoalStatus,
                state.TaskStatus);
            return true;
        }

        if (!IsAutoRequeueGoalStatusAllowed(state.GoalStatus.Value))
        {
            blocker = new AutoRequeueBlocker(
                "goal",
                state.GoalStatus.Value.ToString(),
                "terminal-state",
                null,
                state.GoalStatus,
                state.TaskStatus);
            return true;
        }

        blocker = default!;
        return false;
    }

    public static InterruptedDispatchStateRead ReadCurrentState(
        AgentOrchestratorKernel kernel,
        GoalId goalId,
        TaskId taskId)
    {
        Goal goal;
        try
        {
            goal = kernel.GetGoal(goalId);
        }
        catch (Exception ex)
        {
            return InterruptedDispatchStateRead.Unreadable("goal", ex.Message);
        }

        try
        {
            var task = kernel.GetTask(goalId, taskId);
            return new InterruptedDispatchStateRead(
                goal.Status,
                task.Status,
                WasTaskCancelledByConductor: task.WasCancelledByConductor,
                WasTaskGracefullyDetachedByConductor: task.LastProcess?.WasGracefullyDetachedByConductor == true);
        }
        catch (Exception ex)
        {
            return InterruptedDispatchStateRead.Unreadable("task", ex.Message);
        }
    }

    private static bool IsAutoRequeueTaskStatusAllowed(
        WorkTaskStatus status,
        bool wasCancelledByConductor,
        bool wasGracefullyDetachedByConductor) =>
        status is WorkTaskStatus.Pending or WorkTaskStatus.Assigned or WorkTaskStatus.Running ||
        status == WorkTaskStatus.Cancelled && wasCancelledByConductor ||
        status == WorkTaskStatus.Failed && wasGracefullyDetachedByConductor;

    private static bool IsAutoRequeueGoalStatusAllowed(GoalStatus status) =>
        status == GoalStatus.Active;

    private static bool IsAutoRequeueTerminalGoalStatus(GoalStatus status) => status is
        GoalStatus.Cancelled or
        GoalStatus.Superseded or
        GoalStatus.Completed;

    private static string BuildDispatchId(GoalId goalId, TaskId taskId, TaskDispatchRecord dispatch)
    {
        var raw = string.Join('\u001f',
            goalId.Value,
            taskId.Value,
            dispatch.DispatchedAt.ToUniversalTime().ToString("O"),
            dispatch.WorkerName,
            dispatch.WorkingDirectory,
            dispatch.ProviderSessionId,
            dispatch.Command);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)))[..16].ToLowerInvariant();
    }

    private sealed record AutoRequeueBlocker(
        string BlockingEntity,
        string? TerminalState,
        string Reason,
        string? Detail,
        GoalStatus? GoalStatus,
        WorkTaskStatus? TaskStatus);

    private string? ReapWorktreeBuildDaemons(string workingDirectory)
    {
        try
        {
            var daemons = _findBuildDaemons(workingDirectory);
            if (daemons.Count == 0)
            {
                return null;
            }

            var reaped = new List<string>();
            var failed = new List<string>();

            foreach (var (pid, name, _) in daemons)
            {
                bool killed;
                try
                {
                    killed = _tryKillBuildDaemon(pid);
                }
                catch
                {
                    killed = false;
                }

                if (killed)
                {
                    reaped.Add($"{name} PID {pid}");
                }
                else
                {
                    failed.Add($"PID {pid}");
                }
            }

            var parts = new List<string>();
            if (reaped.Count > 0)
            {
                parts.Add($"Reaped worktree build daemon(s): {string.Join(", ", reaped)}.");
            }

            if (failed.Count > 0)
            {
                parts.Add($"Note: failed to stop {string.Join(", ", failed)}.");
            }

            return parts.Count > 0 ? string.Join(" ", parts) : null;
        }
        catch
        {
            return null;
        }
    }

    private static List<(int ProcessId, string ProcessName, string? CommandLine)> FindBuildDaemons(string workingDirectory)
    {
        var processesByPid = new Dictionary<int, string>();

        foreach (var name in BuildServerCandidates)
        {
            try
            {
                foreach (var proc in Process.GetProcessesByName(name))
                {
                    using (proc)
                    {
                        processesByPid[proc.Id] = proc.ProcessName;
                    }
                }
            }
            catch
            {
            }
        }

        if (processesByPid.Count == 0)
        {
            return [];
        }

        var commandLines = ProcessCommandLines.Read(processesByPid.Keys);
        var result = new List<(int, string, string?)>();

        foreach (var (pid, name) in processesByPid)
        {
            commandLines.TryGetValue(pid, out var cmdLine);
            if (ShouldReapBuildDaemon(workingDirectory, cmdLine))
            {
                result.Add((pid, name, cmdLine));
            }
        }

        return result;
    }

    internal static bool ShouldReapBuildDaemon(string workingDirectory, string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return false;
        }

        var normalizedPath = Path.GetFullPath(workingDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return commandLine.Contains(normalizedPath, StringComparison.OrdinalIgnoreCase) ||
               commandLine.Contains(workingDirectory, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryKillBuildDaemonProcess(int processId)
    {
        try
        {
            var proc = Process.GetProcessById(processId);
            if (!proc.HasExited)
            {
                proc.Kill(entireProcessTree: false);
                proc.WaitForExit(3000);
            }

            return true;
        }
        catch (ArgumentException)
        {
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsStillRunning(int processId)
    {
        try
        {
            var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static ExitCodeReadResult ReadExitCode(string path)
        => DispatchExitArtifactReader.Read(path);

    private static bool TryReadChildExitRecord(
        string? path,
        out DispatchProcessHost.DispatchChildExitRecord record)
    {
        record = new DispatchProcessHost.DispatchChildExitRecord(0, 0, default);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return false;
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var parsed = JsonSerializer.Deserialize<DispatchProcessHost.DispatchChildExitRecord>(
                stream,
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            if (parsed is null || parsed.ProcessId <= 0)
            {
                return false;
            }

            record = parsed;
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

    private ProcessLogSnapshot ReadProcessLogBestEffort(TaskProcessRecord processRecord, string path)
    {
        var key = new ProcessLogCacheKey(processRecord.ExitCodePath, path);
        var length = SafeFileLength(path);
        lock (_processLogCacheGate)
        {
            if (_processLogCache.TryGetValue(key, out var cached) && cached.Length == length)
            {
                return cached;
            }
        }

        var snapshot = ReadProcessLogFileBestEffort(path, length);
        if (snapshot.ReadSucceeded)
        {
            lock (_processLogCacheGate)
            {
                _processLogCache[key] = snapshot;
            }
        }

        return snapshot;
    }

    private ProcessLogSnapshot ReadProcessLogFileBestEffort(string path, long length)
    {
        if (!File.Exists(path))
        {
            return new ProcessLogSnapshot(length, false, string.Empty, string.Empty, ReadSucceeded: true);
        }

        try
        {
            using var stream = _openLogReadStream(path);
            using var reader = new StreamReader(stream);
            return ReadProcessLogSnapshot(reader, path, length);
        }
        catch (IOException)
        {
            var message = $"[log locked at refresh — see {path}]";
            return new ProcessLogSnapshot(length, false, message, message, ReadSucceeded: false);
        }
        catch (UnauthorizedAccessException)
        {
            var message = $"[log unreadable at refresh — see {path}]";
            return new ProcessLogSnapshot(length, false, message, message, ReadSucceeded: false);
        }
    }

    private void EvictProcessLogCache(TaskProcessRecord processRecord)
    {
        lock (_processLogCacheGate)
        {
            _processLogCache.Remove(new ProcessLogCacheKey(processRecord.ExitCodePath, processRecord.StandardOutputPath));
            _processLogCache.Remove(new ProcessLogCacheKey(processRecord.ExitCodePath, processRecord.StandardErrorPath));
        }
    }

    private static string ReadDecisionBestEffort(string path)
    {
        if (!File.Exists(path))
        {
            return string.Empty;
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return ReadDecisionText(reader);
        }
        catch (IOException)
        {
            return $"[log locked at refresh — see {path}]";
        }
        catch (UnauthorizedAccessException)
        {
            return $"[log unreadable at refresh — see {path}]";
        }
    }

    private static string ReadDecisionText(TextReader reader)
    {
        const int MaxDecisionChars = VerificationTextBounds.MaxRetainedChars;
        var retained = new StringBuilder(Math.Min(MaxDecisionChars, VerificationTextBounds.BoundThreshold));
        var prefixRemaining = VerificationTextBounds.PreviewHeadChars;
        var inWorkerResult = false;

        while (true)
        {
            var line = reader.ReadLine();
            if (line is null)
            {
                break;
            }

            // Same single-charge rule as ProcessDecisionLine; see the comment there for the measured failure
            // this prevents.
            var normalized = NormalizeWorkerResultMarker(line);
            var isOpener = IsWorkerResultOpener(normalized);
            var isEndMarker = !isOpener && IsWorkerResultEndMarker(normalized);
            var isDecisionContent = isOpener || isEndMarker || inWorkerResult || IsDecisionSignificantLine(line);

            if (prefixRemaining > 0)
            {
                var take = Math.Min(prefixRemaining, line.Length);
                if (!isDecisionContent)
                {
                    AppendDecisionLine(retained, line[..take], MaxDecisionChars);
                }

                prefixRemaining -= take;
            }

            if (isOpener)
            {
                inWorkerResult = true;
            }

            if (isDecisionContent)
            {
                AppendDecisionLine(retained, line, MaxDecisionChars);
            }

            if (isEndMarker)
            {
                inWorkerResult = false;
            }
        }

        return retained.ToString().TrimEnd();
    }

    private static ProcessLogSnapshot ReadProcessLogSnapshot(TextReader reader, string path, long length)
    {
        const int MaxDecisionChars = VerificationTextBounds.MaxRetainedChars;
        var decision = new StringBuilder(Math.Min(MaxDecisionChars, VerificationTextBounds.BoundThreshold));
        var prefixRemaining = VerificationTextBounds.PreviewHeadChars;
        var inWorkerResult = false;
        var lineBuffer = new StringBuilder();
        var retainedPrefix = new StringBuilder(VerificationTextBounds.BoundThreshold);
        var tail = new char[VerificationTextBounds.PreviewTailChars];
        var tailStart = 0;
        var tailCount = 0;
        var totalChars = 0L;
        var buffer = new char[4096];
        var previousWasCarriageReturn = false;

        void ProcessDecisionLine()
        {
            var line = lineBuffer.ToString();
            lineBuffer.Clear();
            var containsFinalOutput = ContainsCodexFinalOutput(line);

            // A line must be charged to the retention budget ONCE. The head preview and the decision content
            // used to both append the same line when a WORKER_RESULT block began inside the first
            // PreviewHeadChars, spending that prefix twice against one MaxDecisionChars budget.
            //
            // Measured on goal 0b81147a: the block opened at byte 3,153 of a 16,690-char reviewer log, so
            // 8,192 of prefix plus a ~12,912-char block came to ~21,104 against the 20,000 cap. The overflow
            // fell on the END of the block - where model_fit, skills and confidence live - and those are
            // REQUIRED fields. Losing them made WorkerResultParser report the block absent, which set
            // WorkerResultPresent=false, which made PrepareReviewFindingRecord early-return without ever
            // setting MergedReviewFindings, which made the convergence brief report "merged finding state was
            // EMPTY" while twelve valid findings sat in the log. The reviewer was blamed for submitting
            // nothing.
            var normalized = NormalizeWorkerResultMarker(line);
            var isOpener = IsWorkerResultOpener(normalized);
            var isEndMarker = !isOpener && IsWorkerResultEndMarker(normalized);
            var isDecisionContent =
                isOpener || isEndMarker || inWorkerResult || IsDecisionSignificantLine(line, containsFinalOutput);

            if (prefixRemaining > 0)
            {
                var take = Math.Min(prefixRemaining, line.Length);
                if (!isDecisionContent)
                {
                    AppendDecisionLine(decision, line[..take], MaxDecisionChars);
                }

                // Consume the head budget either way: it measures how far into the log we are, not how much
                // of it we chose to retain.
                prefixRemaining -= take;
            }

            if (isOpener)
            {
                inWorkerResult = true;
            }

            if (isDecisionContent)
            {
                AppendDecisionLine(decision, line, MaxDecisionChars);
            }

            if (isEndMarker)
            {
                inWorkerResult = false;
            }
        }

        while (true)
        {
            var read = reader.Read(buffer, 0, buffer.Length);
            if (read == 0)
            {
                break;
            }

            if (retainedPrefix.Length < VerificationTextBounds.BoundThreshold)
            {
                retainedPrefix.Append(buffer, 0, Math.Min(read, VerificationTextBounds.BoundThreshold - retainedPrefix.Length));
            }

            for (var index = 0; index < read; index++)
            {
                var ch = buffer[index];
                if (tailCount < tail.Length)
                {
                    tail[(tailStart + tailCount) % tail.Length] = ch;
                    tailCount++;
                }
                else
                {
                    tail[tailStart] = ch;
                    tailStart = (tailStart + 1) % tail.Length;
                }

                if (ch == '\r')
                {
                    ProcessDecisionLine();
                    previousWasCarriageReturn = true;
                }
                else if (ch == '\n')
                {
                    if (!previousWasCarriageReturn)
                    {
                        ProcessDecisionLine();
                    }

                    previousWasCarriageReturn = false;
                }
                else
                {
                    lineBuffer.Append(ch);
                    previousWasCarriageReturn = false;
                }
            }

            totalChars += read;
        }

        if (lineBuffer.Length > 0)
        {
            ProcessDecisionLine();
        }

        var bounded = BuildBoundedText(retainedPrefix, tail, tailStart, tailCount, totalChars, path);
        var decisionText = decision.ToString().TrimEnd();
        return new ProcessLogSnapshot(
            length,
            ContainsCodexFinalOutput(decisionText),
            decisionText,
            bounded);
    }

    private static string BuildBoundedText(
        StringBuilder retainedPrefix,
        char[] tail,
        int tailStart,
        int tailCount,
        long totalChars,
        string path)
    {
        if (totalChars <= VerificationTextBounds.BoundThreshold)
        {
            return retainedPrefix.ToString();
        }

        var head = retainedPrefix.ToString(0, VerificationTextBounds.PreviewHeadChars);
        var boundedTail = BuildTailText(tail, tailStart, tailCount);
        return VerificationTextBounds.BuildBoundedText(head, boundedTail, totalChars, path);
    }

    private sealed record ProcessLogCacheKey(string ProcessRecordKey, string Path);
    private sealed record ProcessLogSnapshot(
        long Length,
        bool FinalOutputSeen,
        string DecisionText,
        string BoundedText,
        bool ReadSucceeded = true);

    private static void AppendDecisionLine(StringBuilder target, string line, int maxChars)
    {
        if (string.IsNullOrEmpty(line) || target.Length >= maxChars)
        {
            return;
        }

        if (target.Length > 0)
        {
            if (target.Length + Environment.NewLine.Length >= maxChars)
            {
                return;
            }

            target.AppendLine();
        }

        var remaining = maxChars - target.Length;
        target.Append(line, 0, Math.Min(line.Length, remaining));
    }

    private static bool IsDecisionSignificantLine(string line) =>
        IsDecisionSignificantLine(line, ContainsCodexFinalOutput(line));

    private static bool IsDecisionSignificantLine(string line, bool containsCodexFinalOutput)
    {
        return DispatchFailureClassifier.HasVerificationEvidenceInOutput(line, string.Empty) ||
            line.Contains("PLANNER_EVIDENCE_REQUEST:", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("HUMAN_INPUT:", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("HUMAN INPUT:", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("NO_CHANGE:", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("No-change rationale:", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("No changes needed:", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("index.lock", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("blocked on committing", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("CreateProcessAsUserW", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("1312", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("specified logon session does not exist", StringComparison.OrdinalIgnoreCase) ||
            (line.Contains(".git", StringComparison.OrdinalIgnoreCase) &&
             line.Contains("Permission denied", StringComparison.OrdinalIgnoreCase)) ||
            line.Contains("sandbox-prep", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("[dispatch-host] terminating worker tree:", StringComparison.Ordinal) ||
            containsCodexFinalOutput ||
            IsProviderDecisionLine(line) ||
            line.Contains("Model fit:", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Changed files:", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Files changed:", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsProviderDecisionLine(string line)
    {
        return line.Contains("usage limit", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("rate limit", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("429", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("insufficient_quota", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("websocket", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("connection refused", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("ECONNREFUSED", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Unable to connect", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("could not resolve host", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("temporary failure in name resolution", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Forbidden", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("access token", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("invalid model", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("unknown model", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("model_not_found", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("does not exist", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("not supported", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("unsupported", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("400", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsWorkerResultOpener(string normalizedLine) =>
        string.Equals(normalizedLine.TrimEnd(':').Trim(), "WORKER_RESULT", StringComparison.OrdinalIgnoreCase);

    private static bool IsWorkerResultEndMarker(string normalizedLine) =>
        string.Equals(normalizedLine.Trim(), "END_WORKER_RESULT", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeWorkerResultMarker(string text)
    {
        var trimmed = text.Trim();
        var buffer = new char[trimmed.Length];
        var length = 0;
        foreach (var ch in trimmed)
        {
            if (ch is not ('#' or '*' or '`'))
            {
                buffer[length++] = ch;
            }
        }

        return new string(buffer, 0, length).Trim();
    }

    internal static string ReadBoundedBestEffort(string path)
    {
        if (!File.Exists(path))
        {
            return string.Empty;
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return ReadBoundedText(reader, path);
        }
        catch (IOException)
        {
            return $"[log locked at refresh — see {path}]";
        }
        catch (UnauthorizedAccessException)
        {
            return $"[log unreadable at refresh — see {path}]";
        }
    }

    private static string ReadBoundedText(TextReader reader, string path)
    {
        var retainedPrefix = new StringBuilder(VerificationTextBounds.BoundThreshold);
        var tail = new char[VerificationTextBounds.PreviewTailChars];
        var tailStart = 0;
        var tailCount = 0;
        var totalChars = 0L;
        var buffer = new char[4096];

        while (true)
        {
            var read = reader.Read(buffer, 0, buffer.Length);
            if (read == 0)
            {
                break;
            }

            if (retainedPrefix.Length < VerificationTextBounds.BoundThreshold)
            {
                retainedPrefix.Append(buffer, 0, Math.Min(read, VerificationTextBounds.BoundThreshold - retainedPrefix.Length));
            }

            for (var index = 0; index < read; index++)
            {
                if (tailCount < tail.Length)
                {
                    tail[(tailStart + tailCount) % tail.Length] = buffer[index];
                    tailCount++;
                }
                else
                {
                    tail[tailStart] = buffer[index];
                    tailStart = (tailStart + 1) % tail.Length;
                }
            }

            totalChars += read;
        }

        if (totalChars <= VerificationTextBounds.BoundThreshold)
        {
            return retainedPrefix.ToString();
        }

        var head = retainedPrefix.ToString(0, VerificationTextBounds.PreviewHeadChars);
        var tailText = BuildTailText(tail, tailStart, tailCount);
        return VerificationTextBounds.BuildBoundedText(head, tailText, totalChars, path);
    }

    private static string BuildTailText(char[] tail, int tailStart, int tailCount)
    {
        if (tailCount == 0)
        {
            return string.Empty;
        }

        if (tailStart + tailCount <= tail.Length)
        {
            return new string(tail, tailStart, tailCount);
        }

        var suffixLength = tail.Length - tailStart;
        var builder = new StringBuilder(tailCount);
        builder.Append(tail, tailStart, suffixLength);
        builder.Append(tail, 0, tailCount - suffixLength);
        return builder.ToString();
    }

    private static long SafeFileLength(string path)
    {
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : 0L;
        }
        catch (IOException)
        {
            return 0L;
        }
        catch (UnauthorizedAccessException)
        {
            return 0L;
        }
    }

    private static bool HasTaskOutputCommittedForDispatch(Goal goal, TaskId taskId, TaskDispatchRecord? dispatch)
    {
        if (dispatch is null)
        {
            return false;
        }

        return goal.Timeline.Any(evt =>
            evt.TaskId == taskId &&
            evt.OccurredAt >= dispatch.DispatchedAt &&
            evt.Message.Contains("TaskOutputCommitted", StringComparison.Ordinal));
    }

    private bool TryDetectHungCodexWrapper(TaskSpec task, TaskProcessRecord processRecord, out string diagnostic)
    {
        diagnostic = string.Empty;
        if (!UsesCodexExitFileBehavior(task.LastDispatch) || File.Exists(processRecord.ExitCodePath))
        {
            return false;
        }

        var lastOutputAt = GetLastOutputWriteTime(processRecord);
        var idleFor = _clock.UtcNow - lastOutputAt;
        if (idleFor < _postOutputIdleTimeout)
        {
            return false;
        }

        var standardOutput = ReadProcessLogBestEffort(processRecord, processRecord.StandardOutputPath);
        var standardError = ReadProcessLogBestEffort(processRecord, processRecord.StandardErrorPath);
        if (!standardOutput.FinalOutputSeen && !standardError.FinalOutputSeen)
        {
            return false;
        }
        diagnostic = $"Background dispatch wrapper appears hung after codex final output; no exit file was written after {FormatDuration(idleFor)} of idle logs. Marking dispatch failed with captured stdout/stderr evidence.";
        return true;
    }

    private bool TryDetectHungSubscriptionWrapper(TaskSpec task, TaskProcessRecord processRecord, out string diagnostic)
    {
        diagnostic = string.Empty;
        // Codex dispatches have their own output-content detector; skip them here.
        if (UsesCodexExitFileBehavior(task.LastDispatch) || File.Exists(processRecord.ExitCodePath))
        {
            return false;
        }

        if (!TryReadHeartbeat(GetHeartbeatPath(processRecord), out var heartbeat))
        {
            return false;
        }

        // A null childPid means the worker process has exited. Combined with a stalled
        // progress heartbeat and no exit file, this is the hung-wrapper signature:
        // the worker finished but a grandchild inherited the pipe and blocked the drain.
        if (heartbeat.ChildProcessId is not null)
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

    private bool TryDetectStartupHang(
        TaskProcessRecord processRecord,
        out string diagnostic)
    {
        diagnostic = string.Empty;
        if (File.Exists(processRecord.ExitCodePath) ||
            !TryReadHeartbeat(GetHeartbeatPath(processRecord), out var heartbeat))
        {
            return false;
        }

        // Treat as a startup-hang ONLY if the tool process was never invoked. Any positive sign the
        // tool launched and ran rules it out, however long it then idles: API-bound CLI workers
        // (claude/codex in -p/exec mode) burn a brief startup CPU burst then wait at near-zero local
        // CPU on the provider with output buffered, so a healthy worker's cumulative CPU stays low.
        // The "tool was invoked" signals are a live child process, a startup CPU burst above the
        // bare-wrapper baseline, or any produced output.
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
        TaskSpec task,
        GoalId goalId,
        TaskProcessRecord processRecord,
        out string diagnostic)
    {
        diagnostic = string.Empty;
        if (File.Exists(processRecord.ExitCodePath) ||
            !TryReadHeartbeat(GetHeartbeatPath(processRecord), out var heartbeat))
        {
            return false;
        }

        var idleFor = _clock.UtcNow - heartbeat.LastProgressAt;
        if (idleFor < _progressStallTimeout)
        {
            return false;
        }

        if (RequiresFileChangeEvidence(task) &&
            task.LastDispatch is { } dispatch &&
            TryInspectGoalWorktree(processRecord.WorkingDirectory, goalId, dispatch.DispatchedAt, out var wt) &&
            (wt.HasCommitAfterDispatch || !wt.IsClean))
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

    private static bool TryReadHeartbeat(string path, out DispatchHeartbeat heartbeat)
    {
        heartbeat = DispatchHeartbeat.Empty;
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
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
                GetNullableString(root, "dirtyStateHash"));
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

    private static bool TryGetDateTimeOffset(JsonElement root, string propertyName, out DateTimeOffset value)
    {
        value = default;
        return root.TryGetProperty(propertyName, out var property) &&
            property.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(property.GetString(), out value);
    }

    private static string GetString(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? "unknown"
            : "unknown";
    }

    private static string? GetNullableString(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out var property) &&
            property.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(property.GetString())
            ? property.GetString()
            : null;
    }

    private static int GetInt32(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out var property) && property.TryGetInt32(out var value)
            ? value
            : 0;
    }

    private static int? GetNullableInt32(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var property) || property.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return property.TryGetInt32(out var value) ? value : null;
    }

    private static long GetInt64(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out var property) && property.TryGetInt64(out var value)
            ? value
            : 0;
    }

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

    private bool UsesCodexExitFileBehavior(TaskDispatchRecord? dispatch) =>
        dispatch is not null && ResolveWorkerProvider(dispatch).Identity.UsesCodexExitFileBehavior;

    private IWorkerProvider ResolveWorkerProvider(TaskDispatchRecord dispatch)
    {
        if (dispatch.WorkerProviderKind != ProviderKind.Unknown)
        {
            var typedProvider = _workerProviders.Resolve(dispatch.WorkerProviderKind);
            if (typedProvider.Identity.Kind != ProviderKind.Unknown)
            {
                return typedProvider;
            }
        }

        var provider = _workerProviders.ResolveProfile(dispatch.WorkerName);
        if (provider.Identity.Kind != ProviderKind.Unknown)
        {
            return provider;
        }

        return provider;
    }

    private static bool ContainsCodexFinalOutput(string value)
    {
        return value.Contains("tokens used", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("token usage", StringComparison.OrdinalIgnoreCase);
    }

    private static string AppendDiagnostic(string standardError, string? diagnostic)
    {
        if (string.IsNullOrWhiteSpace(diagnostic))
        {
            return standardError;
        }

        return string.IsNullOrEmpty(standardError)
            ? diagnostic
            : standardError.TrimEnd() + Environment.NewLine + diagnostic;
    }

    private static string NormalizeDiagnosticText(string value)
    {
        var normalized = string.Join(
            " ",
            value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return string.IsNullOrWhiteSpace(normalized) ? "none" : normalized;
    }

    private static string BuildRecoveryDiagnostic(DispatchRecoveryDecision? decision)
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

    private static DispatchRecoveryDecision WithAction(
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
            if (!File.Exists(path))
            {
                continue;
            }

            var lastWrite = new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero);
            if (lastWrite > newest)
            {
                newest = lastWrite;
            }
        }

        return newest;
    }

    private static string FormatDuration(TimeSpan duration)
    {
        return duration < TimeSpan.Zero
            ? TimeSpan.Zero.ToString("c")
            : duration.ToString("c");
    }

    private static void TryWriteExitCode(
        string path,
        int exitCode,
        string reason = "orchestrator synthesized dispatch outcome")
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

    private bool AnyTrackedProcessStillRunning(TaskProcessRecord processRecord)
    {
        return processRecord.TrackedProcessIds.Any(_isStillRunning);
    }

    private bool AnyObservedProcessStillRunning(TaskProcessRecord processRecord, DispatchHeartbeat? heartbeat)
    {
        return GetObservedProcessIds(processRecord, heartbeat).Any(_isStillRunning);
    }

    private bool AnyOwnedWorkerProcessStillRunning(TaskProcessRecord processRecord, DispatchHeartbeat? heartbeat)
    {
        return GetOwnedWorkerProcessIds(processRecord, heartbeat).Any(_isStillRunning);
    }

    private static IReadOnlyList<int> GetObservedProcessIds(TaskProcessRecord processRecord, DispatchHeartbeat? heartbeat)
    {
        var processIds = new HashSet<int>(processRecord.TrackedProcessIds.Where(pid => pid > 0));
        if (heartbeat is not null)
        {
            if (heartbeat.ChildProcessId is > 0)
            {
                processIds.Add(heartbeat.ChildProcessId.Value);
            }

            if (heartbeat.OwnedProcessIds is { Count: > 0 })
            {
                foreach (var processId in heartbeat.OwnedProcessIds)
                {
                    if (processId > 0)
                    {
                        processIds.Add(processId);
                    }
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
            foreach (var processId in processRecord.OwnedProcessIds)
            {
                if (processId > 0)
                {
                    processIds.Add(processId);
                }
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
                foreach (var processId in heartbeat.OwnedProcessIds)
                {
                    if (processId > 0)
                    {
                        processIds.Add(processId);
                    }
                }
            }
        }

        return processIds.ToArray();
    }

    private void TryKillTrackedProcesses(
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
            if (!waitForExit)
            {
                continue;
            }

            var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
            while (_isStillRunning(processId) && DateTimeOffset.UtcNow < deadline)
            {
                Thread.Sleep(100);
            }
        }
    }

    private static TaskProcessResourceAccounting? ReleaseTrackedProcessJobs(TaskProcessRecord processRecord)
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

    private TaskProcessResourceAccounting? ReapTrackedProcessJobs(TaskProcessRecord processRecord, bool waitForExit)
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

    private static TaskProcessResourceAccounting? SnapshotTrackedProcessAccounting(TaskProcessRecord processRecord)
    {
        if (!TryReadHeartbeat(GetHeartbeatPath(processRecord), out var heartbeat))
        {
            return null;
        }

        var peakMemoryBytes = 0L;
        foreach (var processId in processRecord.TrackedProcessIds.Distinct())
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                if (process.HasExited)
                {
                    continue;
                }

                peakMemoryBytes = Math.Max(peakMemoryBytes, Math.Max(process.WorkingSet64, process.PeakWorkingSet64));
            }
            catch (ArgumentException)
            {
            }
            catch (InvalidOperationException)
            {
            }
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

    private static bool TryKillProcess(int processId)
    {
        return WorkerProcessJobs.TryKillOrFallback(processId);
    }

    internal static string FormatResourceReceipt(
        GoalId goalId,
        TaskId taskId,
        TaskProcessResourceAccounting accounting) =>
        $"RESOURCE goal={goalId.Value[..8]} task={taskId.Value[..8]} cpu_ms={accounting.CpuMilliseconds} peak_mem_bytes={accounting.PeakMemoryBytes} io_bytes={accounting.IoBytes} accounting_source={accounting.AccountingSource}{(accounting.Reaped ? " reaped=true" : string.Empty)}";

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

    private static bool IsDispatchHostReapCompletion(string standardError) =>
        standardError.Contains("[dispatch-host] terminating worker tree:", StringComparison.Ordinal);

    private static long SaturatingAdd(long left, long right)
    {
        if (left < 0 || right < 0)
        {
            return Math.Max(left, right);
        }

        return long.MaxValue - left < right ? long.MaxValue : left + right;
    }

    // The detached dispatch host is the App's __dispatch-run subcommand. The App assembly sits next
    // to this Infrastructure assembly in every run context (the App output dir in production; the
    // test output dir in tests, which reference the App project), so resolve it from the base dir.
    private static string ResolveDispatchHostAssembly()
    {
        return Path.Combine(AppContext.BaseDirectory, "Mcg.AgentOrchestrator.App.dll");
    }

    private static bool IsLocalDispatch(TaskDispatchRecord dispatch)
    {
        return dispatch.WorkerName.Equals("local", StringComparison.OrdinalIgnoreCase);
    }

    private void TryWriteDiagnosticRecord(
        GoalId goalId,
        TaskId taskId,
        TaskSpec task,
        TaskProcessRecord processRecord,
        int exitCode,
        string standardOutput,
        string standardError)
    {
        try
        {
            const string exitSuffix = ".exit.txt";
            var fn = Path.GetFileName(processRecord.ExitCodePath);
            var prefix = fn.EndsWith(exitSuffix, StringComparison.OrdinalIgnoreCase)
                ? fn[..^exitSuffix.Length]
                : fn;

            var outputPath = processRecord.StandardOutputPath;
            var fileExists = File.Exists(outputPath);
            var fileLen = fileExists ? new FileInfo(outputPath).Length : 0L;
            var readLen = (long)standardOutput.Length;
            var stderrPath = processRecord.StandardErrorPath;
            var stderrLen = File.Exists(stderrPath) ? new FileInfo(stderrPath).Length : 0L;

            var classification = ClassifyDispatch(
                exitCode, fileLen, readLen, stderrLen, standardOutput, standardError, out var reason);

            var dispatchState = new DispatchStateSurface(_clock, _isStillRunning).Evaluate(goalId, task);

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
            System.Diagnostics.Debug.WriteLine($"[DispatchDiagnostic] Failed to record dispatch diagnostic: {ex.Message}");
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

        var combined = $"{standardOutput}\n{standardError}";
        if (exitCode != 0 && ContainsRateLimitSentinel(combined, out var sentinelDetail))
        {
            reason = sentinelDetail;
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

    private static bool ContainsRateLimitSentinel(string combined, out string detail)
    {
        if (combined.Contains("usage limit", StringComparison.OrdinalIgnoreCase))
        {
            if (combined.Contains("try again", StringComparison.OrdinalIgnoreCase))
            {
                detail = "session limit sentinel ('usage limit' + 'try again') in stdout or stderr";
                return true;
            }

            if (combined.Contains("purchase more credits", StringComparison.OrdinalIgnoreCase))
            {
                detail = "session limit sentinel ('usage limit' + 'purchase more credits') in stdout or stderr";
                return true;
            }
        }

        detail = string.Empty;
        return false;
    }

    private sealed record DispatchHeartbeat(
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
        string? DirtyStateHash = null)
    {
        public static DispatchHeartbeat Empty { get; } = new(0, null, "unknown", DateTimeOffset.MinValue, DateTimeOffset.MinValue, 0, 0);
    }

    private sealed record GoalWorktreeInspectionResult(
        bool IsAvailable,
        bool IsUnsafe,
        GoalWorktreeDispatchEvidence Evidence,
        string? UnavailableReason,
        string GitReceipt)
    {
        public static GoalWorktreeInspectionResult Available(GoalWorktreeDispatchEvidence evidence) =>
            new(true, false, evidence, null, "git-inspection-succeeded");

        public static GoalWorktreeInspectionResult Unsafe(string reason, string gitReceipt) =>
            new(false, true, GoalWorktreeDispatchEvidence.Unknown, reason, gitReceipt);

        public static GoalWorktreeInspectionResult Unavailable(string reason, string gitReceipt) =>
            new(false, false, GoalWorktreeDispatchEvidence.Unknown, reason, gitReceipt);
    }

    private sealed record GoalWorktreeDispatchEvidence(
        string Branch,
        string Head,
        bool IsClean,
        string WorktreeStatus,
        string StatusShort,
        int CommitsAfterDispatch,
        IReadOnlyList<string> ChangedPaths,
        IReadOnlyList<string> DirtyPaths)
    {
        public bool HasCommitAfterDispatch => CommitsAfterDispatch > 0;
        public bool HasRelevantCommitAfterDispatch => ChangedPaths.Any(path => !GitCli.IsOrchestratorInternalArtifactPath(path));
        public string ChangedPathsSummary => FormatChangedPaths(ChangedPaths);

        public static GoalWorktreeDispatchEvidence Unknown { get; } = new("unknown", "unknown", false, "unknown", "unavailable", 0, [], []);
    }

    private readonly record struct WrapperExitReconciliationEvidence(
        int ObservedRootExitCode,
        int? ChildExitCode,
        bool HasCompleteNonBlockedWorkerResult,
        bool CompletionContractSucceeded,
        bool HasKnownRoleCapability,
        DispatchRoleOutputCapability RoleCapability,
        bool HasRelevantChangeEvidence,
        bool HasFatalOrchestratorFailure);

    private readonly record struct CommitWorktreeEditsResult(bool Succeeded, string Diagnostic)
    {
        public static CommitWorktreeEditsResult Success { get; } = new(true, string.Empty);

        public static CommitWorktreeEditsResult Failed(string diagnostic) => new(false, diagnostic);

        public static CommitWorktreeEditsResult FromGitFailure(
            string operation,
            IReadOnlyList<string> arguments,
            GitCli.GitResult result)
        {
            var detail = string.IsNullOrWhiteSpace(result.Error)
                ? result.Output
                : result.Error;
            return Failed(
                "Orchestrator commit-on-behalf git command failed. " +
                $"operation={operation}; command=git {FormatGitArguments(arguments)}; exit_code={result.ExitCode}; " +
                $"error={NormalizeDiagnosticText(detail)}.");
        }

        private static string FormatGitArguments(IReadOnlyList<string> arguments)
        {
            return string.Join(
                ' ',
                arguments.Select(argument => argument.Contains(' ', StringComparison.Ordinal) ? $"\"{argument}\"" : argument));
        }
    }
}
