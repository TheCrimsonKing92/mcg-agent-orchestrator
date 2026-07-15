using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed record DispatchRefreshOutcome(
    TaskProcessRecord ProcessRecord,
    TaskVerificationRecord? Verification,
    string? ResultCommit = null,
    string? ResultCommitProvenance = null,
    DispatchRecoveryDecision? RecoveryDecision = null,
    ProviderFailureKind ProviderFailureKind = ProviderFailureKind.Unknown,
    DispatchDiagnosticPayload? DiagnosticPayload = null);

public sealed record DispatchDiagnosticPayload(
    int ExitCode,
    string StandardOutput,
    string StandardError);

public sealed record DispatchProcessStartResult(
    TaskProcessRecord? ProcessRecord,
    WorkerSandboxPrepRecoverableAction? RecoveryAction)
{
    public static DispatchProcessStartResult Started(TaskProcessRecord processRecord) => new(processRecord, null);

    public static DispatchProcessStartResult RequiresRecovery(WorkerSandboxPrepRecoverableAction action) => new(null, action);
}

public sealed class BackgroundDispatchRunner
{
    public const string DisableDispatchStartVariable = "MCG_ORCHESTRATOR_DISABLE_DISPATCH_START";

    private static readonly TimeSpan DefaultPostOutputIdleTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan DefaultProgressStallTimeout = TimeSpan.FromMinutes(20);
    // A startup-hang is a worker whose tool process never launched. The childPid/CPU-burst "invoked"
    // check in TryDetectStartupHang makes this window safe to keep short: a worker that DID launch and
    // is merely idling on the provider API (low local CPU, buffered output) is never flagged, so this
    // only bounds how long a genuinely never-started tool may sit before it is reaped.
    private static readonly TimeSpan DefaultStartupHangTimeout = TimeSpan.FromSeconds(120);

    // ownedCpuMs above this means the tool consumed real CPU since start (it launched and ran), beyond
    // the bare pwsh wrapper baseline - one of the signals that the tool was invoked.
    private const long CpuStartupBurstMs = 1000L;
    private static readonly string[] BuildServerCandidates = ["VBCSCompiler", "MSBuild"];
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
        WorkerProviderCatalog? workerProviders = null)
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
    }

    private static bool IsDispatchStartDisabledByEnvironment()
    {
        var value = Environment.GetEnvironmentVariable(DisableDispatchStartVariable);
        return value is "1" || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }

    public TaskProcessRecord StartLatestDispatch(
        AgentOrchestratorKernel kernel,
        GoalId goalId,
        TaskId taskId,
        string logRoot,
        Action<AgentOrchestratorKernel, GoalId, TaskId>? checkpointBeforeWorkerStart = null)
    {
        var result = TryStartLatestDispatch(kernel, goalId, taskId, logRoot, checkpointBeforeWorkerStart);
        if (result.RecoveryAction is { } action)
        {
            throw new InvalidOperationException(action.Reason);
        }

        return result.ProcessRecord
            ?? throw new InvalidOperationException("Dispatch start did not produce a process record.");
    }

    public DispatchProcessStartResult TryStartLatestDispatch(
        AgentOrchestratorKernel kernel,
        GoalId goalId,
        TaskId taskId,
        string logRoot,
        Action<AgentOrchestratorKernel, GoalId, TaskId>? checkpointBeforeWorkerStart = null)
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
        var heartbeatPath = Path.Combine(logRoot, $"{prefix}.heartbeat.json");
        var startGatePath = Path.Combine(logRoot, $"{prefix}.start-gate");

        var isLocalDispatch = IsLocalDispatch(dispatch);
        var parametersPath = Path.Combine(logRoot, $"{prefix}.dispatch.json");

        // OS worker sandbox: implementation roles receive a Low-labeled writable worktree. Read-only
        // Codex roles also run Low so Codex can skip its expensive nested Windows sandbox setup, but
        // their worktree stays Medium and MIC therefore denies writes.
        var sandbox = WorkerSandboxOptions.FromEnvironment();
        var sandboxProvider = ResolveSandboxProvider(dispatch);
        var sandboxWorktreeWritable = IsSandboxWorktreeWritable(task.RequiredRole);
        var useSandbox = ShouldUseOsSandbox(
            sandbox.Enabled,
            isLocalDispatch,
            task.RequiredRole,
            sandboxProvider);
        kernel.RecordDispatchSandboxLowIntegrity(goalId, taskId, useSandbox);

        DispatchProcessHost.WriteParameters(parametersPath, new DispatchProcessHost.DispatchRunParameters(
            dispatch.Command,
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
            SandboxWorktreeWritable: sandboxWorktreeWritable));

        if (useSandbox && OperatingSystem.IsWindows())
        {
            var sandboxRoot = Path.Combine(dispatch.WorkingDirectory, ".mcg-sandbox");
            var preparer = WorkerSandboxPreparer.CreateDefault();
            var preparation = sandboxWorktreeWritable
                ? preparer.Prepare(dispatch.WorkingDirectory, sandboxRoot)
                : preparer.PrepareSandboxRootOnly(dispatch.WorkingDirectory, sandboxRoot);
            if (preparation.RecoveryAction is { } action)
            {
                return DispatchProcessStartResult.RequiresRecovery(action);
            }
        }

        // Launch the native dispatch host detached: it outlives this CLI process, runs the worker
        // command through the resolved PowerShell host, and writes logs/heartbeat/exit natively.
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
        var baseCommit = TryGetWorktreeHead(dispatch.WorkingDirectory);
        if (baseCommit is not null)
            kernel.RecordDispatchBaseCommit(goalId, taskId, baseCommit);

        ProcessSpawnGuard.ClearInheritableStateDatabaseHandles();
        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start background dispatch process.");
        WorkerProcessJobs.TryRegister(process, $"{goalId.Value}:{taskId.Value}");

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
            OwnedProcessIds: [process.Id]);

        kernel.RecordTaskProcessStarted(goalId, taskId, record);
        try
        {
            checkpointBeforeWorkerStart?.Invoke(kernel, goalId, taskId);
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
                if (process is null ||
                    process.WasCancelled ||
                    task.LastVerification is not null ||
                    HasRecordedCompletionForProcess(task, process))
                {
                    continue;
                }

                var recoveryDecision = _recoveryPolicy.Evaluate(process, AnyTrackedProcessStillRunning(process));
                if (!TryCompleteFromExitFile(kernel, goal.Id, task.Id, process, recoveryDecision, out var outcome))
                    continue;

                ApplyRefreshOutcomeAndWriteDiagnostics(kernel, goal.Id, task.Id, outcome);
                reconciled++;
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

        var hasLiveProcess = AnyTrackedProcessStillRunning(processRecord);
        var hasDirtyWorktreeEvidence =
            !hasLiveProcess &&
            !File.Exists(processRecord.ExitCodePath) &&
            task.LastDispatch is not null &&
            RequiresFileChangeEvidence(task) &&
            TryInspectGoalWorktree(processRecord.WorkingDirectory, goalId, task.LastDispatch.DispatchedAt, out var staleWorktreeEvidence) &&
            !staleWorktreeEvidence.IsClean;
        var recoveryDecision = _recoveryPolicy.Evaluate(
            processRecord,
            hasLiveProcess,
            DispatchRecoveryPolicy.GetStaleRetryBudgetRemaining(task),
            hasDirtyWorktreeEvidence);
        var exitFileExists = File.Exists(processRecord.ExitCodePath);
        if (TryCompleteFromExitFile(kernel, goalId, taskId, processRecord, recoveryDecision, out var completion))
            return completion;

        if (hasLiveProcess)
        {
            if (IsAuthoritativeHold(recoveryDecision))
            {
                return new DispatchRefreshOutcome(processRecord, null, RecoveryDecision: recoveryDecision);
            }

            if (TryDetectHungCodexWrapper(task, processRecord, out var diagnostic))
            {
                var resourceAccounting = ReapTrackedProcessJobs(processRecord, waitForExit: true);
                if (RequiresFileChangeEvidence(task) &&
                    TryInspectGoalWorktree(processRecord.WorkingDirectory, goalId, task.LastDispatch!.DispatchedAt, out var wt) &&
                    wt.IsClean && wt.HasRelevantCommitAfterDispatch)
                {
                    var reapNote =
                        "Background dispatch wrapper appears hung after codex final output; no exit file was written. " +
                        $"Wrapper process reaped; task completed based on relevant file-change evidence " +
                        $"(branch={wt.Branch}; head={wt.Head}; commits_after_dispatch={wt.CommitsAfterDispatch}).";
                    TryWriteExitCode(processRecord.ExitCodePath, 0);
                    return BuildCompletedProcessOutcome(kernel, goalId, taskId, processRecord, 0, reapNote, WithAction(DispatchRecoveryAction.Reap, recoveryDecision, reapNote), resourceAccounting);
                }

                TryWriteExitCode(processRecord.ExitCodePath, 1);
                return BuildCompletedProcessOutcome(kernel, goalId, taskId, processRecord, 1, diagnostic, WithAction(DispatchRecoveryAction.Reap, recoveryDecision, diagnostic), resourceAccounting);
            }

            if (TryDetectHungSubscriptionWrapper(task, processRecord, out var wrapperDiagnostic))
            {
                var resourceAccounting = ReapTrackedProcessJobs(processRecord, waitForExit: true);
                if (RequiresFileChangeEvidence(task) &&
                    TryInspectGoalWorktree(processRecord.WorkingDirectory, goalId, task.LastDispatch!.DispatchedAt, out var wt) &&
                    wt.IsClean && wt.HasRelevantCommitAfterDispatch)
                {
                    var reapNote =
                        "Background dispatch wrapper appears hung with stalled heartbeat; no exit file was written. " +
                        $"Wrapper process reaped; task completed based on relevant file-change evidence " +
                        $"(branch={wt.Branch}; head={wt.Head}; commits_after_dispatch={wt.CommitsAfterDispatch}).";
                    TryWriteExitCode(processRecord.ExitCodePath, 0);
                    return BuildCompletedProcessOutcome(kernel, goalId, taskId, processRecord, 0, reapNote, WithAction(DispatchRecoveryAction.Reap, recoveryDecision, reapNote), resourceAccounting);
                }

                TryWriteExitCode(processRecord.ExitCodePath, 1);
                return BuildCompletedProcessOutcome(kernel, goalId, taskId, processRecord, 1, wrapperDiagnostic, WithAction(DispatchRecoveryAction.Reap, recoveryDecision, wrapperDiagnostic), resourceAccounting);
            }

            if (TryDetectStartupHang(processRecord, out var startupHangDiagnostic))
            {
                var resourceAccounting = ReapTrackedProcessJobs(processRecord, waitForExit: true);
                TryWriteExitCode(processRecord.ExitCodePath, 1);
                return BuildCompletedProcessOutcome(kernel, goalId, taskId, processRecord, 1, startupHangDiagnostic, WithAction(DispatchRecoveryAction.Reap, recoveryDecision, startupHangDiagnostic), resourceAccounting);
            }

            if (TryDetectProbableProgressStall(task, goalId, processRecord, out var stallDiagnostic))
            {
                var resourceAccounting = ReapTrackedProcessJobs(processRecord, waitForExit: true);
                TryWriteExitCode(processRecord.ExitCodePath, 1);
                return BuildCompletedProcessOutcome(kernel, goalId, taskId, processRecord, 1, stallDiagnostic, WithAction(DispatchRecoveryAction.ClassifyBlocker, recoveryDecision, stallDiagnostic), resourceAccounting);
            }

            return new DispatchRefreshOutcome(processRecord, null, RecoveryDecision: recoveryDecision);
        }

        if (exitFileExists)
        {
            return new DispatchRefreshOutcome(processRecord, null, RecoveryDecision: recoveryDecision);
        }

        var staleResourceAccounting = ReapTrackedProcessJobs(processRecord, waitForExit: false);
        var staleDiagnostic = BuildRecoveryDiagnostic(recoveryDecision);
        return BuildCompletedProcessOutcome(kernel, goalId, taskId, processRecord, 1, staleDiagnostic, recoveryDecision, staleResourceAccounting);
    }

    private bool TryCompleteFromExitFile(
        AgentOrchestratorKernel kernel,
        GoalId goalId,
        TaskId taskId,
        TaskProcessRecord processRecord,
        DispatchRecoveryDecision recoveryDecision,
        out DispatchRefreshOutcome outcome)
    {
        IReadOnlyList<int>? ownedProcessIds = null;
        if (TryReadHeartbeat(GetHeartbeatPath(processRecord), out var heartbeat))
        {
            ownedProcessIds = heartbeat.OwnedProcessIds;
        }

        if (ownedProcessIds?.Any(_isStillRunning) == true)
        {
            outcome = new DispatchRefreshOutcome(processRecord, null, RecoveryDecision: recoveryDecision);
            return false;
        }

        if (!TryReadExitCode(processRecord.ExitCodePath, out var exitCode))
        {
            if (!File.Exists(processRecord.ExitCodePath) || AnyTrackedProcessStillRunning(processRecord))
            {
                outcome = new DispatchRefreshOutcome(processRecord, null, RecoveryDecision: recoveryDecision);
                return false;
            }

            if (!TryReadExitCodeWithRetry(processRecord.ExitCodePath, out exitCode))
            {
                var diagnostic =
                    "Dispatch exit file exists and no tracked process is alive, but the exit code could not be read; " +
                    $"treating dispatch as failed. exit_path={processRecord.ExitCodePath}";
                outcome = BuildCompletedProcessOutcome(
                    kernel,
                    goalId,
                    taskId,
                    processRecord,
                    1,
                    AppendDiagnostic(BuildRecoveryDiagnostic(recoveryDecision), diagnostic),
                    recoveryDecision);
                return true;
            }
        }

        if (AnyTrackedProcessStillRunning(processRecord))
        {
            var resourceAccounting = ReapTrackedProcessJobs(processRecord, waitForExit: true);
            outcome = BuildCompletedProcessOutcome(
                kernel,
                goalId,
                taskId,
                processRecord,
                exitCode,
                BuildRecoveryDiagnostic(recoveryDecision),
                recoveryDecision,
                resourceAccounting);
            return true;
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

    private static bool TryReadExitCodeWithRetry(string path, out int exitCode)
    {
        const int attempts = 3;
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            if (TryReadExitCode(path, out exitCode))
            {
                return true;
            }

            if (attempt < attempts - 1)
            {
                Thread.Sleep(TimeSpan.FromMilliseconds(50));
            }
        }

        exitCode = 1;
        return false;
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

        if (previousProcess?.CompletedAt is not null &&
            outcome.ProcessRecord.CompletedAt is not null &&
            previousProcess.ProcessId == outcome.ProcessRecord.ProcessId &&
            task.LastVerification is not null)
        {
            verification = null;
        }

        kernel.RecordTaskProcessRefreshed(goalId, taskId, outcome.ProcessRecord, verification, outcome.ProviderFailureKind);
        if (verification is not null && outcome.ProcessRecord.ResourceAccounting is { } accounting)
        {
            kernel.RecordTaskNote(goalId, taskId, FormatResourceReceipt(goalId, taskId, accounting));
        }
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
        TaskProcessResourceAccounting? capturedResourceAccounting = null)
    {
        var exitArtifactAlreadyExisted = File.Exists(processRecord.ExitCodePath);
        var standardOutput = ReadBestEffort(processRecord.StandardOutputPath);
        var standardError = ReadBestEffort(processRecord.StandardErrorPath);
        var resourceAccounting = capturedResourceAccounting ?? ReleaseTrackedProcessJobs(processRecord);
        if (resourceAccounting is not null &&
            !resourceAccounting.Reaped &&
            IsDispatchHostReapCompletion(standardError))
        {
            resourceAccounting = resourceAccounting with { Reaped = true };
        }
        var task = kernel.GetTask(goalId, taskId);
        var providerFailureKind = ParseProviderFailureKind(task.LastDispatch, exitCode, standardOutput, standardError);
        var workerResultPresent = HasWorkerResultArtifact(processRecord.WorkingDirectory, standardOutput, standardError);
        var hasCommittedChanges = false;
        var orchestratorCommitted = false;
        if (RequiresFileChangeEvidence(task) &&
            TryInspectGoalWorktree(processRecord.WorkingDirectory, goalId, task.LastDispatch!.DispatchedAt, out var worktreeEvidence))
        {
            hasCommittedChanges = worktreeEvidence.HasRelevantCommitAfterDispatch;
            // Default path: a Developer/Tester that edited the worktree and showed verification
            // evidence does not need to self-commit. The orchestrator stages and commits the dirty
            // diff after guards pass. Dirty-but-unverified edits are left dirty and fail.
            var originalExitCode = exitCode;
            var commitAttempted = false;
            var commitAttempt = default(CommitWorktreeEditsResult);
            var sandboxCommitBlocked = HasSandboxCommitBlockedEvidence(
                task,
                processRecord,
                standardOutput,
                standardError,
                providerFailureKind);
            var lowIntegrityConfinementEvidence = HasLowIntegrityConfinementEvidence(
                task.LastDispatch,
                processRecord,
                standardError,
                sandboxCommitBlocked);
            var successfulWorkerResult = HasSuccessfulWorkerResult(
                processRecord.WorkingDirectory,
                standardOutput,
                standardError);
            if (TryFindFailedWorkerBuildCheck(
                    processRecord.WorkingDirectory,
                    standardOutput,
                    standardError,
                    out var failedBuildCheckDiagnostic))
            {
                exitCode = 1;
                standardErrorDiagnostic = AppendDiagnostic(
                    standardErrorDiagnostic ?? string.Empty,
                    failedBuildCheckDiagnostic);
            }

            var provider = ResolveWorkerProvider(task.LastDispatch);
            var normalIntegrityCommitEvidence =
                task.LastDispatch.SandboxLowIntegrity != true &&
                (successfulWorkerResult || worktreeEvidence.HasRelevantCommitAfterDispatch);
            var shouldCommitDirtyWorktree =
                (exitCode == 0 && (normalIntegrityCommitEvidence || lowIntegrityConfinementEvidence)) ||
                (task.LastDispatch.SandboxLowIntegrity && sandboxCommitBlocked) ||
                (originalExitCode != 0 && successfulWorkerResult && !provider.Capabilities.CanSelfCommit && lowIntegrityConfinementEvidence);

            if (!worktreeEvidence.IsClean &&
                shouldCommitDirtyWorktree)
            {
                commitAttempt = TryCommitWorktreeEdits(
                    processRecord.WorkingDirectory,
                    BuildOrchestratorCommitSubject(task, standardOutput, standardError),
                    worktreeEvidence.DirtyPaths);
                commitAttempted = true;
                if (commitAttempt.Succeeded &&
                    TryInspectGoalWorktree(processRecord.WorkingDirectory, goalId, task.LastDispatch!.DispatchedAt, out worktreeEvidence) &&
                    worktreeEvidence.IsClean && worktreeEvidence.HasRelevantCommitAfterDispatch)
                {
                    orchestratorCommitted = true;
                    hasCommittedChanges = true;
                    exitCode = 0;
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

            if (!orchestratorCommitted && !worktreeEvidence.IsClean && exitCode == 0)
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
                var requiresCommitEvidence =
                    RequiresPostDispatchCommitEvidence(task, standardOutput, standardError, workerResultPresent) &&
                    !HasCompletedVerification(task, standardOutput, standardError) &&
                    !AllowsNoChangeCompletion(task, standardOutput, standardError) &&
                    !worktreeEvidence.HasRelevantCommitAfterDispatch;

                if (requiresCommitEvidence)
                {
                    // The role had to land a relevant change and didn't — fail regardless of exit code
                    // (a Developer that produced nothing is a real failure, not exit-code noise).
                    exitCode = 1;
                    standardErrorDiagnostic = AppendDiagnostic(
                        standardErrorDiagnostic ?? string.Empty,
                        "Developer/Tester dispatch did not produce required relevant file-change evidence. " +
                        $"branch={worktreeEvidence.Branch}; head={worktreeEvidence.Head}; worktree={worktreeEvidence.WorktreeStatus}; " +
                        $"commits_after_dispatch={worktreeEvidence.CommitsAfterDispatch}; changed_paths={worktreeEvidence.ChangedPathsSummary}.");
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

        standardError = AppendDiagnostic(standardError, standardErrorDiagnostic);
        if (!exitArtifactAlreadyExisted)
        {
            TryWriteExitCode(processRecord.ExitCodePath, exitCode);
        }
        var completed = processRecord with
        {
            CompletedAt = _clock.UtcNow,
            ExitCode = exitCode,
            ResourceAccounting = resourceAccounting
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
            ProviderFailureKind: providerFailureKind);

        return new DispatchRefreshOutcome(
            completed,
            verification,
            resultCommit,
            resultCommitProvenance,
            recoveryDecision,
            providerFailureKind,
            new DispatchDiagnosticPayload(exitCode, standardOutput, standardError));
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

    private static bool RequiresFileChangeEvidence(TaskSpec task)
    {
        return task.LastDispatch is { } dispatch &&
            !IsLocalDispatch(dispatch) &&
            task.RequiredRole is AgentRole.Developer or AgentRole.Tester;
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
            HasCompletedVerification(task, standardOutput, standardError);
    }

    // A verification-role worker proves it did its job with recognised verification evidence.
    // WORKER_RESULT shape alone is not enough: evidence-less clean dispatches must fail so the
    // orchestrator does not convert a well-formed self-report into proof that checks actually passed.
    private static bool HasClassifiedVerificationEvidence(TaskSpec task, string standardOutput, string standardError)
    {
        var tempVerification = new TaskVerificationRecord(
            string.Empty, string.Empty, 1, standardOutput, standardError, DateTimeOffset.UtcNow);
        return DispatchFailureClassifier.Classify(task, tempVerification).EvidenceSummary.Length > 0;
    }

    private static bool HasCompletedVerification(TaskSpec task, string standardOutput, string standardError)
    {
        return HasClassifiedVerificationEvidence(task, standardOutput, standardError) &&
            !TryFindFailingTestsInWorkerResult($"{standardOutput}\n{standardError}", out _);
    }

    private static bool HasSandboxCommitBlockedEvidence(
        TaskSpec task,
        TaskProcessRecord processRecord,
        string standardOutput,
        string standardError,
        ProviderFailureKind providerFailureKind)
    {
        var verification = new TaskVerificationRecord(
            processRecord.Command,
            processRecord.WorkingDirectory,
            1,
            standardOutput,
            standardError,
            DateTimeOffset.UtcNow);
        return DispatchFailureClassifier.Classify(task, verification, providerFailureKind).Kind == DispatchOutcomeKind.SandboxCommitBlocked;
    }

    private static bool HasLowIntegrityConfinementEvidence(
        TaskDispatchRecord? dispatch,
        TaskProcessRecord processRecord,
        string standardError,
        bool sandboxCommitBlocked)
    {
        if (dispatch?.SandboxLowIntegrity != true)
        {
            return false;
        }

        return sandboxCommitBlocked ||
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

    private static bool HasWorkerResultArtifact(string workingDirectory, string standardOutput, string standardError)
    {
        if (WorkerResultParser.TryParseFields(
                $"{standardOutput}\n{standardError}",
                out _,
                out _))
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
                if (WorkerResultParser.TryParseFields(File.ReadAllText(path), out _, out _))
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

    private static bool HasSuccessfulWorkerResult(string workingDirectory, string standardOutput, string standardError)
    {
        if (WorkerResultParser.TryParseSuccessfulResult(
                $"{standardOutput}\n{standardError}",
                out _,
                out _))
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
                if (WorkerResultParser.TryParseSuccessfulResult(File.ReadAllText(path), out _, out _))
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
                if (TryFindFailedWorkerBuildCheckInText(File.ReadAllText(path), out diagnostic))
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

    private static bool TryInspectGoalWorktree(
        string workingDirectory,
        GoalId goalId,
        DateTimeOffset dispatchedAt,
        out GoalWorktreeDispatchEvidence evidence)
    {
        evidence = GoalWorktreeDispatchEvidence.Unknown;
        if (!Directory.Exists(workingDirectory) || !File.Exists(Path.Combine(workingDirectory, ".git")))
        {
            return false;
        }

        var branch = GitCli.Run(workingDirectory, "branch", "--show-current");
        var expectedBranch = GoalWorktrees.BranchName(goalId);
        if (branch.ExitCode != 0 || !string.Equals(branch.Output.Trim(), expectedBranch, StringComparison.Ordinal))
        {
            return false;
        }

        var head = GitCli.Run(workingDirectory, "rev-parse", "--short", "HEAD");
        var status = GitCli.Run(workingDirectory, "status", "--short", "--untracked-files=all");
        var dispatch = GitCli.Run(workingDirectory, "log", "--format=%H", $"--since={dispatchedAt:O}");
        var changedPaths = GitCli.Run(workingDirectory, "log", "--name-only", "--format=", $"--since={dispatchedAt:O}");
        var commitsAfterDispatch = 0;
        if (dispatch.ExitCode == 0)
        {
            commitsAfterDispatch = dispatch.Output
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Length;
        }

        var pathsChangedAfterDispatch = changedPaths.ExitCode == 0
            ? changedPaths.Output
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray()
            : [];

        var filteredStatusOutput = GitCli.FilterCommitWorthyStatus(status.Output);
        evidence = new GoalWorktreeDispatchEvidence(
            branch.Output.Trim(),
            head.ExitCode == 0 ? head.Output.Trim() : "unknown",
            status.ExitCode == 0 && string.IsNullOrWhiteSpace(filteredStatusOutput),
            status.ExitCode == 0 && string.IsNullOrWhiteSpace(filteredStatusOutput) ? "clean" : "dirty",
            FormatStatusShort(new GitCli.GitResult(status.ExitCode, filteredStatusOutput, string.Empty)),
            commitsAfterDispatch,
            pathsChangedAfterDispatch,
            GitCli.ParseCommitWorthyStatusPaths(filteredStatusOutput));
        return true;
    }

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

    public TaskProcessRecord CancelLatestProcess(AgentOrchestratorKernel kernel, GoalId goalId, TaskId taskId)
    {
        var task = kernel.GetTask(goalId, taskId);
        var processRecord = task.LastProcess
            ?? throw new InvalidOperationException($"Task '{taskId}' has no background process to cancel.");

        TaskProcessResourceAccounting? resourceAccounting = null;
        if (processRecord.IsRunning)
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

        resourceAccounting ??= ReleaseTrackedProcessJobs(processRecord);
        var cancelled = processRecord with
        {
            CompletedAt = _clock.UtcNow,
            WasCancelled = true,
            ResourceAccounting = resourceAccounting
        };

        kernel.RecordTaskProcessCancelled(goalId, taskId, cancelled);
        if (resourceAccounting is not null)
        {
            kernel.RecordTaskNote(goalId, taskId, FormatResourceReceipt(goalId, taskId, resourceAccounting));
        }

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

            CancelLatestProcess(kernel, goalId, task.Id);
            cancelled++;
        }

        return cancelled;
    }

    public int DetachRunningProcessesForGoal(AgentOrchestratorKernel kernel, GoalId goalId)
    {
        var goal = kernel.GetGoal(goalId);
        return goal.Tasks.Count(task => task.LastProcess is { IsRunning: true });
    }

    public int RequeueInterruptedDispatches(AgentOrchestratorKernel kernel)
    {
        var recovered = 0;
        foreach (var goal in kernel.Goals.Where(goal => goal.Status == GoalStatus.Active).ToArray())
        {
            foreach (var task in goal.Tasks.ToArray())
            {
                if (task.Status == WorkTaskStatus.Cancelled)
                {
                    kernel.RequeueInterruptedDispatch(
                        goal.Id,
                        task.Id,
                        "Auto-requeued interrupted dispatch after conductor loop stop.");
                    recovered++;
                    continue;
                }

                if (task.Status != WorkTaskStatus.Running ||
                    task.LastProcess is not { IsRunning: true } process ||
                    TryReadExitCode(process.ExitCodePath, out _) ||
                    AnyTrackedProcessStillRunning(process))
                {
                    continue;
                }

                kernel.RequeueInterruptedDispatch(
                    goal.Id,
                    task.Id,
                    "Auto-requeued orphaned running dispatch; no tracked process is alive.");
                recovered++;
            }
        }

        return recovered;
    }

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

    private static bool TryReadExitCode(string path, out int exitCode)
    {
        if (!File.Exists(path))
        {
            exitCode = 1;
            return false;
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            if (int.TryParse(reader.ReadToEnd().Trim(), out exitCode))
            {
                return true;
            }
        }
        catch (IOException)
        {
            exitCode = 1;
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            exitCode = 1;
            return false;
        }

        exitCode = 1;
        return true;
    }

    private static string ReadBestEffort(string path)
    {
        if (!File.Exists(path))
        {
            return string.Empty;
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
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

    private bool TryDetectHungCodexWrapper(TaskSpec task, TaskProcessRecord processRecord, out string diagnostic)
    {
        diagnostic = string.Empty;
        if (!UsesCodexExitFileBehavior(task.LastDispatch) || File.Exists(processRecord.ExitCodePath))
        {
            return false;
        }

        var standardOutput = ReadBestEffort(processRecord.StandardOutputPath);
        var standardError = ReadBestEffort(processRecord.StandardErrorPath);
        if (!ContainsCodexFinalOutput(standardOutput) && !ContainsCodexFinalOutput(standardError))
        {
            return false;
        }

        var lastOutputAt = GetLastOutputWriteTime(processRecord);
        var idleFor = _clock.UtcNow - lastOutputAt;
        if (idleFor < _postOutputIdleTimeout)
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

        // Only Developer/Tester subscription dispatches carry file-change evidence;
        // other roles use the broader progress-stall timeout instead.
        if (!RequiresFileChangeEvidence(task))
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
            "Marking dispatch based on worktree evidence.";
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

        if (RequiresFileChangeEvidence(task) &&
            task.LastDispatch is { } dispatch &&
            TryInspectGoalWorktree(processRecord.WorkingDirectory, goalId, dispatch.DispatchedAt, out var wt) &&
            (wt.HasCommitAfterDispatch || !wt.IsClean))
        {
            return false;
        }

        var idleFor = _clock.UtcNow - heartbeat.LastProgressAt;
        if (idleFor < _progressStallTimeout)
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
                GetInt32Array(root, "ownedPids"));
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

    private static void TryWriteExitCode(string path, int exitCode)
    {
        try
        {
            File.WriteAllText(path, exitCode.ToString());
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

    private void TryKillTrackedProcesses(TaskProcessRecord processRecord, bool waitForExit)
    {
        foreach (var processId in processRecord.TrackedProcessIds.Distinct())
        {
            _tryKillOwnedProcess(processId);
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
            : null;
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
            var stderrLen = (long)standardError.Length;

            var classification = ClassifyDispatch(
                exitCode, fileLen, readLen, standardOutput, standardError, out var reason);

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

        if (exitCode != 0 && fileLen == 0 && readLen == 0)
        {
            reason = "exit non-zero with empty output file and empty captured stdout";
            return "genuine-failure";
        }

        reason = exitCode == 0
            ? $"exit 0; fileLen={fileLen}; readLen={readLen}"
            : $"exit {exitCode}; fileLen={fileLen}; readLen={readLen}; stderrLen={standardError.Length}";
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
        IReadOnlyList<int>? OwnedProcessIds = null)
    {
        public static DispatchHeartbeat Empty { get; } = new(0, null, "unknown", DateTimeOffset.MinValue, DateTimeOffset.MinValue, 0, 0);
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
