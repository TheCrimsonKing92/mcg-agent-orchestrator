using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Core;
namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed partial class BackgroundDispatchRunner
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
    private readonly Func<string, IReadOnlyList<ProcessInspectionRecord>> _findBuildDaemons;
    private readonly Func<ProcessInspectionRecord, bool> _tryKillBuildDaemon;
    private readonly IDispatchDiagnosticWriter _diagnosticWriter;
    private readonly DispatchRecoveryPolicy _recoveryPolicy;
    private readonly WorkerProviderCatalog _workerProviders;
    private readonly WorkerDispatchCompletionClassifier _completionClassifier;
    private readonly DispatchProcessRecoveryService _recoveryService;
    private readonly DispatchWorktreeCommitter _worktreeCommitter;
    private readonly InterruptedWorkCheckpointAuthorizer _checkpointAuthorizer;
    private readonly ProcessLogReader _processLogReader;
    private readonly Func<ProcessStartInfo, Process?> _startProcess;
    private readonly Func<OrchestratorBuildCheckRequest, OrchestratorBuildCheckResult> _runOrchestratorBuildCheck;
    private readonly Func<ProcessCommandLineSnapshot> _processCommandLineSnapshotFactory;

    public BackgroundDispatchRunner(
        IClock? clock = null,
        TimeSpan? postOutputIdleTimeout = null,
        Func<int, bool>? isStillRunning = null,
        Func<int, bool>? tryKillOwnedProcess = null,
        bool? disableProcessStart = null,
        Func<string, IReadOnlyList<ProcessInspectionRecord>>? findBuildDaemons = null,
        Func<ProcessInspectionRecord, bool>? tryKillBuildDaemon = null,
        TimeSpan? progressStallTimeout = null,
        IDispatchDiagnosticWriter? diagnosticWriter = null,
        TimeSpan? startupHangTimeout = null,
        DispatchRecoveryPolicy? recoveryPolicy = null,
        WorkerProviderCatalog? workerProviders = null,
        Func<string, Stream>? openLogReadStream = null,
        Action? beforeGoalWorktreeInspection = null,
        Func<ProcessStartInfo, Process?>? startProcess = null,
        Func<OrchestratorBuildCheckRequest, OrchestratorBuildCheckResult>? runOrchestratorBuildCheck = null,
        Func<int, (DateTimeOffset StartedAt, string ImagePath)?>? readProcessIdentity = null,
        Func<ProcessCommandLineSnapshot>? processCommandLineSnapshotFactory = null,
        Func<GoalId, string>? resolveWorkerBuildReceiptPath = null)
    {
        _clock = clock ?? new SystemClock();
        _postOutputIdleTimeout = postOutputIdleTimeout ?? DefaultPostOutputIdleTimeout;
        _progressStallTimeout = progressStallTimeout ?? DefaultProgressStallTimeout;
        _startupHangTimeout = startupHangTimeout ?? DefaultStartupHangTimeout;
        _isStillRunning = isStillRunning ?? IsStillRunning;
        _tryKillOwnedProcess = tryKillOwnedProcess ?? TryKillProcess;
        _processStartDisabled = disableProcessStart ?? IsDispatchStartDisabledByEnvironment();
        _findBuildDaemons = findBuildDaemons ?? WorktreeBuildDaemonReaper.Find;
        _tryKillBuildDaemon = tryKillBuildDaemon ?? WorktreeBuildDaemonReaper.TryKill;
        _diagnosticWriter = diagnosticWriter ?? new FileDiagnosticWriter();
        _recoveryPolicy = recoveryPolicy ?? new DispatchRecoveryPolicy(_clock);
        _workerProviders = workerProviders ?? WorkerProviderCatalog.Default();
        _processLogReader = new ProcessLogReader(openLogReadStream);
        _completionClassifier = new WorkerDispatchCompletionClassifier(
            ResolveWorkerProvider,
            _clock,
            File.Exists,
            ProcessLogReader.ReadDecisionBestEffort,
            resolveWorkerBuildReceiptPath);
        _recoveryService = new DispatchProcessRecoveryService(
            _clock,
            _postOutputIdleTimeout,
            _progressStallTimeout,
            _startupHangTimeout,
            _isStillRunning,
            _tryKillOwnedProcess,
            evaluateRecovery: (process, hasLiveProcess, staleRetryBudgetRemaining, worktreeInspection) =>
                _recoveryPolicy.Evaluate(process, hasLiveProcess, staleRetryBudgetRemaining, worktreeInspection),
            diagnosticWriter: _diagnosticWriter,
            readProcessIdentity: DispatchProcessIdentityEvidence.Adapt(readProcessIdentity));
        _worktreeCommitter = new DispatchWorktreeCommitter(beforeWorktreeInspection: beforeGoalWorktreeInspection);
        _checkpointAuthorizer = new InterruptedWorkCheckpointAuthorizer(
            _worktreeCommitter,
            _clock,
            isProcessRunning: _isStillRunning,
            readCurrentIdentity: DispatchProcessIdentityEvidence.Adapt(readProcessIdentity));
        _startProcess = startProcess ?? Process.Start;
        _runOrchestratorBuildCheck = runOrchestratorBuildCheck ?? OrchestratorBuildEvidenceCheck.RunDefault;
        _processCommandLineSnapshotFactory = processCommandLineSnapshotFactory ?? ProcessCommandLines.SnapshotOperation;
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

    public void BeginRefreshCycle() => _worktreeCommitter.BeginRefreshCycle();

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
        Func<GoalId, TaskId, InterruptedDispatchStateRead>? readCurrentState = null,
        WorkerSandboxOptions? sandboxOptions = null,
        Func<bool>? claimWorkerStart = null,
        Func<bool>? confirmWorkerStart = null)
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
        var sandbox = sandboxOptions ?? WorkerSandboxOptions.FromEnvironment();
        var sandboxProvider = ResolveSandboxProvider(dispatch);
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

        // Claude credential source: the conductor's dispatch preflight selected and reported it while
        // preparing this dispatch, and the decision travels from the dispatch record to the detached
        // dispatch host below. Neither this boundary nor the host selects again, which is how preflight's
        // reported login and the login a worker actually receives are the same one. Only the selection
        // travels - the credential bytes are read in the host at seeding time, so a CLI token refresh
        // still reaches the worker.
        var credentialSelection = DispatchProcessHost.TransportedClaudeCredentialSelection(
            dispatch.ClaudeCredentialSourceDirectory,
            dispatch.ClaudeCredentialSourceIsExplicit,
            sandboxProvider,
            useSandbox);

        var runParameters = new DispatchProcessHost.DispatchRunParameters(
            dispatchHostCommand,
            dispatch.WorkingDirectory,
            stdoutPath,
            stderrPath,
            exitCodePath,
            heartbeatPath,
            DisableSharedCompilation: !isLocalDispatch,
            SandboxLowIntegrity: useSandbox,
            Provider: sandboxProvider,
            PromptPath: dispatch.PromptPath,
            SandboxWorktreeWritable: sandboxWorktreeWritable,
            ProviderSessionId: dispatch.ProviderSessionId,
            WorktreeHeadSha: dispatch.WorktreeHeadSha,
            DirtyStateHash: dispatch.DirtyStateHash,
            Kind: DispatchProcessHost.WorkerDispatchKind,
            PrepGoalId: useSandbox ? goalId.Value : null,
            PrepTaskId: useSandbox ? taskId.Value : null,
            PrepRecordPath: prepRecordPath,
            PrepHeartbeatPath: prepHeartbeatPath,
            PrepExitCodePath: prepExitCodePath,
            ChildExitRecordPath: childExitRecordPath,
            HostDiagnosticPath: hostDiagnosticPath,
            MandatoryContextFiles: dispatch.ContextPackageReceipt?.Sections
                .Where(section => section.DeliveryMode == ContextDeliveryMode.MandatoryFile)
                .Select(section => new MandatoryContextFileDescriptor(
                    section.LogicalIdentity,
                    section.MandatoryRelativePath ?? string.Empty,
                    section.ContentHash,
                    section.ContractVersion,
                    task.RequiredRole,
                    section.RoleVisibility))
                .ToArray(),
            ClaudeCredentialSelection: credentialSelection);
        DispatchProcessHost.WriteParameters(parametersPath, runParameters);

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

        if (TryRejectInterruptedDispatchRecovery(kernel, goalId, taskId, readCurrentState))
        {
            return DispatchProcessStartResult.Skipped();
        }

        // Only persist automatic recovery preparation after the live state guard admits it. A
        // checkpoint before this read can merge the stale tick-owned task status over an operator
        // cancellation and make the subsequent read falsely appear non-terminal.
        checkpointBeforeWorkerStart?.Invoke(kernel, goalId, taskId, DispatchRecordCheckpointPhase.BeforeProcessStart);

        DispatchProcessHost.PrepareSharedSandboxState(runParameters);
        ProcessSpawnGuard.ClearInheritableStateDatabaseHandles();
        var process = _startProcess(startInfo)
            ?? throw new InvalidOperationException("Failed to start background dispatch process.");
        if (!WorkerProcessJobs.TryRegister(process, $"{goalId.Value}:{taskId.Value}", out var registrationFailure))
        {
            TerminateUnreleasedDispatchHost(process);
            kernel.ReportTaskProgress(goalId, taskId, WorkTaskStatus.Failed, registrationFailure);
            checkpointBeforeWorkerStart?.Invoke(kernel, goalId, taskId, DispatchRecordCheckpointPhase.ProcessMayHaveStarted);
            return DispatchProcessStartResult.Failed(registrationFailure);
        }

        // A registry-free runner has no durable process-identity reader. Registration still owns
        // the live process through its job, so preserve that supported degraded path; durable
        // registries continue to supply the identity later used by successor recovery.
        _ = WorkerProcessJobs.TryGetRegisteredIdentity(process.Id, out var dispatchHostIdentity);

        var sampleArtifacts = task.RequiredRole == AgentRole.Planner
            ? PlannerSampleDispatcher.CreateArtifacts(stdoutPath, dispatch.PlannerSampleCount)
            : [];
        IReadOnlyList<PlannerSampleLaunch> sampleLaunches;
        try
        {
            sampleLaunches = PlannerSampleDispatcher.StartSamples(
                sampleArtifacts,
                runParameters,
                ResolveDispatchHostAssembly(),
                $"{goalId.Value}:{taskId.Value}",
                _startProcess);
        }
        catch
        {
            // The primary is already registered but cannot be persisted until sample ownership is
            // known. If optional-sample preflight fails loudly, release that unrecorded ownership.
            TerminateUnreleasedDispatchHost(process);
            throw;
        }

        foreach (var ownedProcess in sampleLaunches.Select(launch => launch.Process).Prepend(process))
        {
            if (WorkerProcessJobs.TryHandOffToRuntimeOwnership(ownedProcess.Id, out var handoffFailure))
            {
                continue;
            }

            TerminateUnreleasedDispatchHost(process);
            PlannerSampleDispatcher.TerminateUnreleased(sampleLaunches);
            kernel.ReportTaskProgress(goalId, taskId, WorkTaskStatus.Failed, handoffFailure);
            checkpointBeforeWorkerStart?.Invoke(
                kernel,
                goalId,
                taskId,
                DispatchRecordCheckpointPhase.ProcessMayHaveStarted);
            return DispatchProcessStartResult.Failed(handoffFailure);
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
            OwnedProcessIds: sampleLaunches.Select(launch => launch.Process.Id).Prepend(process.Id).ToArray(),
            ChildExitRecordPath: childExitRecordPath,
            NonBlockingProcessIds: sampleLaunches.Select(launch => launch.Process.Id).ToArray(),
            ProcessIdentityStartedAt: dispatchHostIdentity?.StartedAt);

        try
        {
            if (claimWorkerStart is not null && !claimWorkerStart())
            {
                TerminateUnreleasedDispatchHost(process);
                PlannerSampleDispatcher.TerminateUnreleased(sampleLaunches);
                return DispatchProcessStartResult.Skipped();
            }

            kernel.RecordTaskProcessStarted(goalId, taskId, record);
            checkpointBeforeWorkerStart?.Invoke(kernel, goalId, taskId, DispatchRecordCheckpointPhase.ProcessMayHaveStarted);
        }
        catch
        {
            TerminateUnreleasedDispatchHost(process);
            PlannerSampleDispatcher.TerminateUnreleased(sampleLaunches);
            throw;
        }

        try
        {
            try
            {
                PlannerSampleDispatcher.ReleaseStartGates(sampleLaunches);
            }
            finally
            {
                // The primary is already durable. A deferred optional-sample diagnostic failure must
                // remain loud without stranding the authoritative dispatch behind its start gate.
                ReleaseDispatchHostStartGate(startGatePath);
            }

            if (confirmWorkerStart is not null && !confirmWorkerStart())
            {
                throw new InvalidOperationException(
                    "The paid retry worker start could not be durably confirmed after releasing its start gate.");
            }
        }
        catch
        {
            TerminateUnreleasedDispatchHost(process);
            PlannerSampleDispatcher.TerminateUnreleased(sampleLaunches);
            throw;
        }
        return DispatchProcessStartResult.Started(record);
    }

    private void TerminateUnreleasedDispatchHost(Process process)
    {
        try
        {
            if (!_tryKillOwnedProcess(process.Id) && !process.HasExited)
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
        Directory.CreateDirectory(Path.GetDirectoryName(startGatePath)!);
        File.WriteAllText(startGatePath, "go");
    }

    private DispatchRefreshOutcome? TryBuildPlannerSampleHold(
        TaskSpec task,
        TaskProcessRecord processRecord)
    {
        if (task.RequiredRole != AgentRole.Planner ||
            task.LastDispatch?.PlannerSampleCount is not > 1 ||
            !DispatchExitArtifacts.TryRead(processRecord.ExitCodePath, out var primaryExit) ||
            !PlannerSampleDispatcher.AnySampleUnresolved(
                processRecord.StandardOutputPath,
                task.LastDispatch.PlannerSampleCount))
        {
            return null;
        }

        var wait = PlannerSampleDispatcher.ResolveSampleWait(processRecord.StartedAt, primaryExit);
        var deadline = primaryExit.RecordedAt + wait;
        if (_clock.UtcNow < deadline)
        {
            return new DispatchRefreshOutcome(
                processRecord,
                null,
                RecoveryDecision: new DispatchRecoveryDecision(
                    DispatchRecoveryAction.Hold,
                    DispatchRecoveryPolicy.ToActionName(DispatchRecoveryAction.Hold),
                    processRecord.ExitCodePath,
                    $"Planner samples are unresolved; bounded wait ends at {deadline:O}."));
        }

        foreach (var processId in processRecord.NonBlockingProcessIds ?? [])
        {
            try { _tryKillOwnedProcess(processId); } catch { }
        }
        PlannerSampleDispatcher.RecordTimedOutSamples(
            processRecord.StandardOutputPath,
            task.LastDispatch.PlannerSampleCount,
            wait,
            deadline);
        return null;
    }

    private WorkerSandboxProvider ResolveSandboxProvider(TaskDispatchRecord dispatch)
    {
        if (IsGrokCliProfile(dispatch.WorkerName))
        {
            return WorkerSandboxProvider.Grok;
        }

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

        if (provider.Identity.Kind == ProviderKind.HermesAcp)
        {
            return WorkerSandboxProvider.Hermes;
        }

        return WorkerSandboxProvider.Unknown;
    }

    internal static bool IsGrokCliProfile(string? workerName) =>
        string.Equals(workerName, WorkerProfileDispatcher.XaiSubscriptionProfileName, StringComparison.OrdinalIgnoreCase);

    internal static bool IsSandboxWorktreeWritable(AgentRole role) =>
        role is AgentRole.Developer or AgentRole.Tester;

    internal static bool ShouldUseOsSandbox(
        bool sandboxEnabled,
        bool isLocalDispatch,
        AgentRole role,
        WorkerSandboxProvider provider) =>
        sandboxEnabled &&
        !isLocalDispatch &&
        (IsSandboxWorktreeWritable(role) || provider is WorkerSandboxProvider.Codex or WorkerSandboxProvider.Hermes);

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
        var processInspection = new ProcessInspectionSnapshotScope(_processCommandLineSnapshotFactory);
        foreach (var goal in kernel.Goals.Where(goal => onlyGoalId is null || goal.Id == onlyGoalId))
        {
            foreach (var task in goal.Tasks)
            {
                var process = task.LastProcess;
                if (!DispatchExitSweepEligibility.IsEligibleForExitSweep(task, process))
                {
                    continue;
                }
                if (ShouldDeferProcessReconciliationForOperatorCancelIntent(goal.Id, task))
                    continue;
                var recoveryDecision = _recoveryPolicy.Evaluate(process, _recoveryService.AnyTrackedProcessStillRunning(process));
                if (!_recoveryService.TryCompleteFromExitFile(
                        process,
                        recoveryDecision,
                        heartbeat => RecordProviderSessionFromHeartbeat(
                            kernel,
                            goal.Id,
                            task.Id,
                            kernel.GetTask(goal.Id, task.Id),
                            heartbeat),
                        out var verdict))
                {
                    continue;
                }
                var outcome = verdict.Kind == DispatchProcessVerdictKind.CompletedFromExitFile
                    ? TryBuildPlannerSampleHold(task, process) ?? BuildCompletedProcessOutcome(
                        kernel,
                        goal.Id,
                        task.Id,
                        process,
                        verdict.ExitCode!.Value,
                        verdict.Diagnostic,
                        verdict.RecoveryDecision)
                    : new DispatchRefreshOutcome(process, null, RecoveryDecision: verdict.RecoveryDecision);
                outcome = DispatchExitSweepEligibility.FenceAutoRequeue(task, outcome);
                ApplyRefreshOutcomeAndWriteDiagnostics(kernel, goal.Id, task.Id, outcome, processInspection.Get);
                if (outcome.RecoveryDecision?.Action != DispatchRecoveryAction.Hold)
                {
                    reconciled++;
                }
            }
        }
        return reconciled;
    }

    public TaskProcessRecord RefreshLatestProcess(AgentOrchestratorKernel kernel, GoalId goalId, TaskId taskId) =>
        RefreshLatestProcessWithOutcome(kernel, goalId, taskId).ProcessRecord;

    public DispatchRefreshOutcome RefreshLatestProcessWithOutcome(
        AgentOrchestratorKernel kernel,
        GoalId goalId,
        TaskId taskId)
    {
        var outcome = ReconcileLatestProcess(kernel, goalId, taskId);
        ApplyRefreshOutcomeAndWriteDiagnostics(kernel, goalId, taskId, outcome);
        return outcome;
    }

    public void ApplyRefreshOutcomeAndWriteDiagnostics(
        AgentOrchestratorKernel kernel,
        GoalId goalId,
        TaskId taskId,
        DispatchRefreshOutcome outcome,
        Func<ProcessCommandLineSnapshot>? getProcessSnapshot = null)
    {
        ApplyRefreshOutcome(kernel, goalId, taskId, outcome);
        if (outcome.DiagnosticPayload is not { } diagnostic)
            return;

        var task = kernel.GetTask(goalId, taskId);
        _recoveryService.TryWriteDiagnosticRecord(
            goalId,
            taskId,
            task,
            outcome.ProcessRecord,
            diagnostic.ExitCode,
            diagnostic.StandardOutput,
            diagnostic.StandardError,
            (getProcessSnapshot ?? _processCommandLineSnapshotFactory)());
    }

    public void ApplyRefreshOutcomesAndWriteDiagnostics(
        AgentOrchestratorKernel kernel,
        GoalId goalId,
        IReadOnlyList<(TaskId TaskId, DispatchRefreshOutcome Outcome)> outcomes)
    {
        var processInspection = new ProcessInspectionSnapshotScope(_processCommandLineSnapshotFactory);
        foreach (var (taskId, outcome) in outcomes)
        {
            ApplyRefreshOutcomeAndWriteDiagnostics(
                kernel,
                goalId,
                taskId,
                outcome,
                processInspection.Get);
        }
    }

    public DispatchRefreshOutcome ReconcileLatestProcess(AgentOrchestratorKernel kernel, GoalId goalId, TaskId taskId)
    {
        var task = kernel.GetTask(goalId, taskId);
        var processRecord = task.LastProcess
            ?? throw new InvalidOperationException($"Task '{taskId}' has no background process to refresh.");
        if (DispatchProcessCompletionState.HasAlreadyBeenApplied(task, processRecord))
        {
            return new DispatchRefreshOutcome(processRecord, null);
        }
        if (ShouldDeferProcessReconciliationForOperatorCancelIntent(goalId, task))
            return new DispatchRefreshOutcome(processRecord, null);
        var verdict = _recoveryService.ClassifyRefresh(
            task,
            goalId,
            processRecord,
            DispatchRecoveryPolicy.GetStaleRetryBudgetRemaining(task),
            UsesCodexExitFileBehavior(task.LastDispatch),
            RequiresFileChangeEvidence(task),
            inspectWorktree: () => BuildRecoveryWorktreeInspectionStatus(task, processRecord, goalId),
            hasCodexFinalOutput: () =>
            {
                var standardOutput = _processLogReader.ReadBestEffort(processRecord, processRecord.StandardOutputPath);
                var standardError = _processLogReader.ReadBestEffort(processRecord, processRecord.StandardErrorPath);
                return standardOutput.FinalOutputSeen || standardError.FinalOutputSeen;
            },
            hasWorktreeProgress: () => HasRecoveryWorktreeProgress(task, processRecord, goalId),
            heartbeatObserved: heartbeat =>
                RecordProviderSessionFromHeartbeat(kernel, goalId, taskId, task, heartbeat));

        if (verdict.Kind == DispatchProcessVerdictKind.CompletedFromExitFile)
        {
            return TryBuildPlannerSampleHold(task, processRecord) ?? BuildCompletedProcessOutcome(
                kernel,
                goalId,
                taskId,
                processRecord,
                verdict.ExitCode!.Value,
                verdict.Diagnostic,
                verdict.RecoveryDecision);
        }

        if (verdict.Kind == DispatchProcessVerdictKind.HungWrapper)
        {
            return CompleteHungWrapperDispatch(
                kernel,
                goalId,
                taskId,
                task,
                processRecord,
                verdict.Diagnostic,
                verdict.RecoveryDecision);
        }

        if (verdict.Kind == DispatchProcessVerdictKind.SuspectedHang)
        {
            return CompleteSuspectedHangDispatch(
                kernel,
                goalId,
                taskId,
                task,
                processRecord,
                verdict.Diagnostic,
                verdict.HangRecoveryAction,
                verdict.RecoveryDecision);
        }

        if (verdict.Kind is DispatchProcessVerdictKind.Live or DispatchProcessVerdictKind.Hold)
        {
            return new DispatchRefreshOutcome(processRecord, null, RecoveryDecision: verdict.RecoveryDecision);
        }

        var recoveryDecision = verdict.RecoveryDecision;
        var staleResourceAccounting = verdict.ResourceAccounting;
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

        if (verdict.WorktreeInspectionStatus.HasDirtyEvidence)
        {
            var interruptedDecision = new DispatchRecoveryDecision(
                DispatchRecoveryAction.PreserveInterruptedWork,
                DispatchRecoveryPolicy.ToActionName(DispatchRecoveryAction.PreserveInterruptedWork),
                processRecord.ExitCodePath,
                "process disappeared with dirty worktree evidence",
                "interrupted worker evidence requires operator verification");
            const string interruptedReason = "process missing with dirty worktree evidence";
            _recoveryService.TryWriteExitCode(processRecord.ExitCodePath, 1, interruptedReason);
            var interruptedOutcome = BuildCompletedProcessOutcome(
                kernel,
                goalId,
                taskId,
                processRecord,
                1,
                DispatchProcessRecoveryService.BuildRecoveryDiagnostic(interruptedDecision),
                interruptedDecision,
                staleResourceAccounting);
            return interruptedOutcome.AutoRequeueDisposition is not null
                ? interruptedOutcome
                : interruptedOutcome with
                {
                    AutoRequeueDisposition = new DispatchAutoRequeueDisposition(
                    "InterruptedDispatchWorkPreserved",
                    DispatchProcessRecoveryService.BuildRecoveryDiagnostic(interruptedDecision),
                    ShouldRequeue: false)
                };
        }

        var staleDiagnostic = DispatchProcessRecoveryService.BuildRecoveryDiagnostic(recoveryDecision);
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

    private DispatchWorktreeInspectionStatus BuildRecoveryWorktreeInspectionStatus(
        TaskSpec task,
        TaskProcessRecord processRecord,
        GoalId goalId)
    {
        var lastDispatch = task.LastDispatch
            ?? throw new InvalidOperationException("Recovery worktree inspection requires a dispatch record.");
        var inspection = _worktreeCommitter.InspectGoalWorktree(
            processRecord.WorkingDirectory,
            goalId,
            lastDispatch.DispatchedAt);
        return inspection.IsAvailable
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

    private bool HasRecoveryWorktreeProgress(TaskSpec task, TaskProcessRecord processRecord, GoalId goalId)
    {
        return task.LastDispatch is { } dispatch &&
            _worktreeCommitter.TryInspectGoalWorktree(processRecord.WorkingDirectory, goalId, dispatch.DispatchedAt, out var worktree) &&
            (worktree.HasCommitAfterDispatch || !worktree.IsClean);
    }

    public static void ApplyRefreshOutcome(
        AgentOrchestratorKernel kernel,
        GoalId goalId,
        TaskId taskId,
        DispatchRefreshOutcome outcome)
    {
        var task = GetTaskAfterReceiptlessUsage(kernel, goalId, taskId, outcome);
        if (outcome.Verification is not null && outcome.DispatchAttemptAt is { } dispatchAttemptAt)
        {
            var dispatch = task.DispatchHistory.SingleOrDefault(candidate => candidate.DispatchedAt == dispatchAttemptAt)
                ?? throw new InvalidOperationException(
                    $"Cannot record provider usage for unknown dispatch attempt {dispatchAttemptAt:O}.");
            var receipt = dispatch.ContextPackageReceipt
                ?? throw new InvalidOperationException(
                    $"Dispatch attempt {dispatchAttemptAt:O} has no context package receipt.");
            var measuredReceipt = receipt
                .WithProviderUsage(outcome.ProviderUsage, outcome.ProviderUsageUnavailableReason)
                .WithToolTranscriptCharacters(
                    outcome.Verification.AuthoritativeStandardOutput?.Length ??
                    outcome.Verification.StandardOutput.Length);
            if (outcome.ProviderUsage is not null)
            {
                measuredReceipt = measuredReceipt.WithValidatedContext();
            }
            kernel.RecordDispatchContextPackageReceipt(
                goalId,
                taskId,
                dispatchAttemptAt,
                measuredReceipt);
            task = kernel.GetTask(goalId, taskId);
            if (task.LastDispatch?.DispatchedAt != dispatchAttemptAt)
            {
                // Delayed telemetry belongs to the originating attempt, but its stale process and
                // verification must not be applied to the newer current dispatch.
                return;
            }
        }
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

        if (outcome.FailedRoundReceipt is { } failedReceipt && verification is { Succeeded: false } &&
            task.LastProcess?.WasCancelled != true && !outcome.ProcessRecord.WasCancelled)
            kernel.RecordFailedRoundCheckpointReceipt(goalId, taskId, failedReceipt);
        kernel.RecordTaskProcessRefreshed(goalId, taskId, outcome.ProcessRecord, verification, outcome.ProviderFailureKind);
        if (verification is not null && outcome.ProcessRecord.ResourceAccounting is { } accounting)
        {
            kernel.RecordTaskNote(goalId, taskId, FormatResourceReceipt(goalId, taskId, accounting));
        }
        if (verification is not null && outcome.SkillsReceipt is { } skillsReceipt)
            RecordSkillsReceipt(kernel, goalId, taskId, skillsReceipt);

        if (outcome.AutoRequeueDisposition is { } disposition)
        {
            kernel.RecordTaskNote(goalId, taskId, $"{disposition.EventName}: {disposition.Message}");
            var checkpointRetryBudgetExhausted =
                disposition.Checkpoint is not null &&
                task.Status == WorkTaskStatus.Failed &&
                verification is { Succeeded: false } &&
                outcome.ProviderFailureKind == ProviderFailureKind.Connectivity &&
                DispatchFailureClassifier.IsRecoverableProviderConnectivityFailure(verification);
            var checkpointAlreadyApplied =
                disposition.Checkpoint is { } candidateCheckpoint &&
                task.PendingInterruptedWorkCheckpoint is { } pendingCheckpoint &&
                string.Equals(candidateCheckpoint.IdempotencyKey, pendingCheckpoint.IdempotencyKey, StringComparison.Ordinal) &&
                string.Equals(candidateCheckpoint.CheckpointSha, pendingCheckpoint.CheckpointSha, StringComparison.Ordinal) &&
                string.Equals(candidateCheckpoint.DispatchId, pendingCheckpoint.DispatchId, StringComparison.Ordinal) &&
                string.Equals(candidateCheckpoint.GoalId, pendingCheckpoint.GoalId, StringComparison.Ordinal) &&
                string.Equals(candidateCheckpoint.TaskId, pendingCheckpoint.TaskId, StringComparison.Ordinal);
            if (checkpointRetryBudgetExhausted)
            {
                kernel.RecordTaskNote(
                    goalId,
                    taskId,
                    "InterruptedDispatchCheckpointRetryBudgetExhausted: checkpoint-retry-budget-exhausted; checkpoint retained in git for operator recovery.");
            }
            else if (disposition.ShouldRequeue && !checkpointAlreadyApplied &&
                     !AgentOrchestratorKernel.IsReopenProtectedGoalStatus(kernel.GetGoal(goalId).Status))
            {
                kernel.RequeueInterruptedDispatch(
                    goalId,
                    taskId,
                    disposition.Message,
                    RetryCause.ProviderInterruption,
                    disposition.InterruptedDispatchId,
                    disposition.Checkpoint);
            }
        }

        if (outcome.RecoveryDecision is
            {
                Action: DispatchRecoveryAction.Hold,
                Blocker: { Length: > 0 } blocker
            } apparatusHold && !AgentOrchestratorKernel.IsReopenProtectedGoalStatus(kernel.GetGoal(goalId).Status))
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
        var diagnostic = DispatchProcessRecoveryService.BuildRecoveryDiagnostic(decision);
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
            var capDiagnostic = AppendDiagnostic(DispatchProcessRecoveryService.BuildRecoveryDiagnostic(capDecision), capEvidenceDiagnostic);
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

        var retryDecision = DispatchProcessRecoveryService.WithAction(
            DispatchRecoveryAction.RetryStale,
            recoveryDecision,
            recoveryDecision.Reason);
        var diagnostic = AppendDiagnostic(
            DispatchProcessRecoveryService.BuildRecoveryDiagnostic(retryDecision),
            evidenceDiagnostic);
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
        var stdoutFileBytes = ProcessLogReader.SafeFileLength(processRecord.StandardOutputPath);
        var stderrFileBytes = ProcessLogReader.SafeFileLength(processRecord.StandardErrorPath);
        var stdout = _processLogReader.ReadBestEffort(processRecord, processRecord.StandardOutputPath).DecisionText;
        var stderr = _processLogReader.ReadBestEffort(processRecord, processRecord.StandardErrorPath).DecisionText;
        var workerResultPresent = _completionClassifier.HasWorkerResultArtifact(processRecord.WorkingDirectory, stdout);
        var taskOutputCommitted = HasTaskOutputCommittedForDispatch(kernel.GetGoal(goalId), taskId, task.LastDispatch);
        GoalWorktreeDispatchEvidence? worktreeEvidence = null;
        var worktreeEvidenceAvailable = false;
        if (task.LastDispatch is { } dispatch &&
            _worktreeCommitter.TryInspectGoalWorktree(processRecord.WorkingDirectory, goalId, dispatch.DispatchedAt, out var inspectedWorktree))
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
        var task = kernel.GetTask(goalId, taskId);
        var dispatchAttempt = ResolveDispatchAttempt(task, processRecord);
        DateTimeOffset? contextReceiptAttemptAt = dispatchAttempt?.ContextPackageReceipt is null
            ? null
            : dispatchAttempt.DispatchedAt;
        var providerUsage = ResolveDispatchProviderUsage(dispatchAttempt, processRecord.StandardOutputPath);
        var outputSnapshot = _processLogReader.ReadBestEffort(processRecord, processRecord.StandardOutputPath);
        var errorSnapshot = _processLogReader.ReadBestEffort(processRecord, processRecord.StandardErrorPath);
        var decisionStandardOutput = outputSnapshot.DecisionText;
        var decisionStandardError = errorSnapshot.DecisionText;
        var fullStandardOutput = _processLogReader.ReadComplete(processRecord.StandardOutputPath);
        var authoritativeStandardOutput = fullStandardOutput.Content is { } initialAuthoritativeContent
            ? ProcessLogReader.NormalizeCompleteDecisionText(initialAuthoritativeContent)
            : decisionStandardOutput;
        if (outputSnapshot.DecisionTruncatedChars > 0)
        {
            standardErrorDiagnostic = AppendDiagnostic(
                standardErrorDiagnostic ?? string.Empty,
                $"decision-text-truncated source=stdout dropped_chars={outputSnapshot.DecisionTruncatedChars} " +
                $"dropped_scope=selected-decision-content retained_chars={outputSnapshot.DecisionText.Length} " +
                $"cap={VerificationTextBounds.MaxRetainedChars} " +
                $"complete_artifact='{processRecord.StandardOutputPath}'");
        }
        if (errorSnapshot.DecisionTruncatedChars > 0)
        {
            standardErrorDiagnostic = AppendDiagnostic(
                standardErrorDiagnostic ?? string.Empty,
                $"decision-text-truncated source=stderr dropped_chars={errorSnapshot.DecisionTruncatedChars} " +
                $"dropped_scope=selected-decision-content retained_chars={errorSnapshot.DecisionText.Length} " +
                $"cap={VerificationTextBounds.MaxRetainedChars} " +
                $"complete_artifact='{processRecord.StandardErrorPath}'");
        }
        string? finalPlannerRejectionDiagnostic = null;
        var hasChildExitRecord = TryReadChildExitRecord(processRecord.ChildExitRecordPath, out var childExitRecord);
        var wrapperExitReconciled = false;
        var resourceAccounting = capturedResourceAccounting;
        var goal = kernel.GetGoal(goalId);
        var humanInputDirective = AgentOutputDirectives.ParseHumanInputRequest(authoritativeStandardOutput, task.RequiredRole);
        var hasRoleCapability = DispatchRoleOutputCapabilities.TryGet(task.RequiredRole, out var dispatchRoleCapability);
        var completeNonBlockedWorkerResult = hasRoleCapability && _completionClassifier.HasSuccessfulWorkerResult(
            processRecord.WorkingDirectory,
            authoritativeStandardOutput,
            decisionStandardError,
            allowNoChangedFiles: true,
            requireNoBlockers: true,
            roleCapability: dispatchRoleCapability);
        var successfulChildResultAvailable =
            observedExitCode != 0 &&
            hasChildExitRecord &&
            childExitRecord.ExitCode == 0 &&
            completeNonBlockedWorkerResult;
        var successfulChildWithoutUsableWorkerResult =
            observedExitCode != 0 &&
            hasChildExitRecord &&
            childExitRecord.ExitCode == 0 &&
            !completeNonBlockedWorkerResult;
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

        PlannerCandidateDivergenceReceipt? plannerCandidateDivergence = null;
        var scoutResearchSourcePath = processRecord.StandardOutputPath;
        if (task.RequiredRole == AgentRole.Planner && (exitCode == 0 || successfulChildResultAvailable))
        {
            var acceptanceCriteria = RequiresDurablePlanArtifact(goal, task)
                ? goal.RefinedSpec?.AcceptanceCriteria ?? []
                : null;
            var capturedPlannerOutput = PlannerOutputContract.ReadCapturedOutputTail(processRecord.StandardOutputPath);
            var evidenceRequest = AgentOutputDirectives.ParseHumanInputRequest(authoritativeStandardOutput, AgentRole.Planner);
            PlannerOutputContractResult plannerContract;
            if (evidenceRequest.IsMalformed)
            {
                plannerContract = new PlannerOutputContractResult(false, null, null, evidenceRequest.Diagnostic!);
            }
            else if (task.LastDispatch?.PlannerSampleCount > 1)
            {
                var candidates = PlannerSampleDispatcher.CollectCandidates(
                        processRecord.StandardOutputPath,
                        task.LastDispatch.PlannerSampleCount,
                        dispatchAttempt,
                        processRecord.CompletedAt is { } completedAt
                            ? Math.Max(0, (long)(completedAt - processRecord.StartedAt).TotalMilliseconds)
                            : null);
                var selection = PlannerCandidateSelector.Select(
                    candidates,
                    processRecord.WorkingDirectory,
                    acceptanceCriteria);
                plannerContract = selection.SelectedContract;
                plannerCandidateDivergence = selection.Receipt;
                if (selection.Receipt.SelectedCandidateIndex is int selectedIndex)
                    scoutResearchSourcePath = candidates[selectedIndex].SourcePath
                        ?? throw new InvalidOperationException("Selected Planner candidate has no stdout source path.");
            }
            else
            {
                plannerContract = PlannerOutputContract.Resolve(
                    capturedPlannerOutput,
                    decisionStandardError,
                    processRecord.WorkingDirectory,
                    acceptanceCriteria: acceptanceCriteria);
            }
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
                else if (RequiresDurableScoutResearchArtifact(goal, task) &&
                    !TryPersistScoutResearch(scoutResearchSourcePath, processRecord.StandardOutputPath, out var scoutDiagnostic))
                {
                    exitCode = 1;
                    completionContractSucceeded = false;
                    standardErrorDiagnostic = AppendDiagnostic(
                        standardErrorDiagnostic ?? string.Empty,
                        scoutDiagnostic);
                }
            }
        }

        // Refresh after role-specific contracts may have appended durable receipts, then reuse this same
        // complete artifact for presence and the stored verification record. Normalize only bare carriage
        // returns for parsing; the authoritative stored content remains byte-complete and unchanged.
        fullStandardOutput = _processLogReader.ReadComplete(processRecord.StandardOutputPath);
        authoritativeStandardOutput = fullStandardOutput.Content is { } authoritativeContent
            ? ProcessLogReader.NormalizeCompleteDecisionText(authoritativeContent)
            : authoritativeStandardOutput;
        var providerFailureKind = _completionClassifier.ParseProviderFailureKind(task.LastDispatch, observedExitCode, decisionStandardOutput, decisionStandardError);
        var workerResultPresent = _completionClassifier.HasWorkerResultArtifact(
            processRecord.WorkingDirectory,
            authoritativeStandardOutput);
        var hasCommittedChanges = false;
        var orchestratorCommitted = false;
        InterruptedWorkCheckpointDisposition? checkpointDisposition = null;
        FailedRoundCheckpointReceipt? failedRoundReceipt = null;
        var completedWorktreeInspection = RequiresFileChangeEvidence(task)
            ? _worktreeCommitter.InspectGoalWorktree(
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
            ? _completionClassifier.ClassifyReconciliationOriginRule(
                task,
                processRecord,
                observedExitCode,
                decisionStandardOutput,
                decisionStandardError,
                standardErrorDiagnostic,
                workerResultPresent,
                hasCommittedChanges,
                providerFailureKind,
                childExitRecord,
                recoveryDecision)
            : null;
        var reconcileWrapperExit = ShouldReconcileWrapperExit(new WrapperExitReconciliationEvidence(
            observedExitCode,
            hasChildExitRecord ? childExitRecord.ExitCode : null,
            completeNonBlockedWorkerResult,
            completionContractSucceeded,
            hasRoleCapability,
            dispatchRoleCapability,
            relevantChangeEvidenceAvailable,
            humanInputDirective.Directive is not null || humanInputDirective.IsMalformed,
            !string.IsNullOrWhiteSpace(orchestratorFailureReason)));
        if (completedWorktreeInspection is { IsAvailable: true, Evidence: var worktreeEvidence })
        {
            // Default path: a Developer/Tester that edited the worktree and showed verification
            // evidence does not need to self-commit. The orchestrator stages and commits the dirty
            // diff after guards pass. Dirty-but-unverified edits are left dirty and fail.
            var originalExitCode = exitCode;
            var commitAttempted = false;
            var commitAttempt = default(CommitWorktreeEditsResult);
            var sandboxCommitBlocked = _completionClassifier.HasSandboxCommitBlockedEvidence(
                task.RequiredRole,
                decisionStandardOutput,
                decisionStandardError);
            var sandboxCommitOnBehalfEvidence =
                sandboxCommitBlocked || providerFailureKind == ProviderFailureKind.Sandbox1312;
            var lowIntegrityConfinementEvidence = _completionClassifier.HasLowIntegrityConfinementEvidence(
                task.LastDispatch,
                processRecord,
                decisionStandardError,
                sandboxCommitOnBehalfEvidence);
            // Blockers remain advisory on the ordinary exit-0 commit path. The stricter
            // completeNonBlockedWorkerResult is reserved for overriding a failed wrapper exit.
            var successfulWorkerResult = _completionClassifier.HasSuccessfulWorkerResult(
                processRecord.WorkingDirectory,
                decisionStandardOutput,
                decisionStandardError);
            var failedWorkerBuildCheck = _completionClassifier.TryFindFailedWorkerBuildCheck(
                    processRecord.WorkingDirectory,
                    decisionStandardOutput,
                    decisionStandardError,
                    out var failedBuildCheckDiagnostic);
            if (failedWorkerBuildCheck)
            {
                exitCode = 1;
                standardErrorDiagnostic = AppendDiagnostic(
                    AppendDiagnostic(
                        standardErrorDiagnostic ?? string.Empty,
                        failedBuildCheckDiagnostic),
                    DispatchFailureDiagnosticMarker.Format(DispatchFailureDiagnosticMarker.WorkerBuildCheckFailed));
            }

            checkpointDisposition = _checkpointAuthorizer.AuthorizeAndCheckpoint(
                new InterruptedWorkCheckpointRequest(
                    goalId,
                    task,
                    processRecord,
                    recoveryDecision,
                    providerFailureKind,
                    completedWorktreeInspection,
                    BuildDispatchId(goalId, taskId, task.LastDispatch!),
                    failedWorkerBuildCheck));
            if (checkpointDisposition.Kind is InterruptedWorkCheckpointDispositionKind.Hold or
                InterruptedWorkCheckpointDispositionKind.CommitFailed)
            {
                standardErrorDiagnostic = AppendDiagnostic(
                    standardErrorDiagnostic ?? string.Empty,
                    checkpointDisposition.Message);
            }

            var buildEvidence = OrchestratorBuildEvidenceCheck.Resolve(
                processRecord.WorkingDirectory,
                task.RequiredRole,
                worktreeEvidence.ChangedPaths,
                worktreeEvidence.DirtyPaths,
                failedWorkerBuildCheck,
                () => _completionClassifier.EvaluateWorkerBuildReceipt(processRecord.WorkingDirectory, goal.Id),
                _runOrchestratorBuildCheck);
            var missingWorkerBuildEvidence = buildEvidence.MissingEvidence;
            standardErrorDiagnostic = buildEvidence.AppendDiagnostic(standardErrorDiagnostic);
            if (buildEvidence.FailsRound)
            {
                exitCode = 1;
            }
            var provider = ResolveWorkerProvider(task.LastDispatch);
            var normalIntegrityCommitEvidence =
                task.LastDispatch.SandboxLowIntegrity != true &&
                (successfulWorkerResult || worktreeEvidence.HasRelevantCommitAfterDispatch);
            var shouldCommitDirtyWorktree =
                !missingWorkerBuildEvidence &&
                checkpointDisposition?.IsCheckpoint != true &&
                (recoveryDecision?.Action != DispatchRecoveryAction.PreserveInterruptedWork || reconcileWrapperExit) &&
                ((exitCode == 0 && (normalIntegrityCommitEvidence || lowIntegrityConfinementEvidence)) ||
                 reconcileWrapperExit ||
                 (task.LastDispatch.SandboxLowIntegrity && sandboxCommitOnBehalfEvidence) ||
                 (originalExitCode != 0 && successfulWorkerResult && !provider.Capabilities.CanSelfCommit && lowIntegrityConfinementEvidence));

            if (!worktreeEvidence.IsClean &&
                shouldCommitDirtyWorktree)
            {
                commitAttempt = _worktreeCommitter.TryCommitWorktreeEdits(
                    processRecord.WorkingDirectory,
                    DispatchWorktreeCommitter.BuildOrchestratorCommitMessage(goal, task, BuildDispatchId(goalId, taskId, task.LastDispatch!), decisionStandardOutput, decisionStandardError, worktreeEvidence.DirtyPaths),
                    worktreeEvidence.DirtyPaths);
                commitAttempted = true;
                if (commitAttempt.Succeeded &&
                    _worktreeCommitter.TryInspectGoalWorktree(
                        processRecord.WorkingDirectory,
                        goalId,
                        task.LastDispatch!.DispatchedAt,
                        out worktreeEvidence,
                        forceRefresh: true) &&
                    worktreeEvidence.IsClean && worktreeEvidence.HasRelevantCommitAfterDispatch)
                {
                    orchestratorCommitted = true;
                    hasCommittedChanges = true;
                    if (!reconcileWrapperExit && !missingWorkerBuildEvidence)
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
                        WithDeferredNoChangeDecline(kernel.GetGoal(goalId), task, worktreeEvidence, fullStandardOutput.Content is null ? null : authoritativeStandardOutput, decisionStandardError, standardErrorDiagnostic),
                        DispatchWorktreeCommitter.BuildCommitOnBehalfFailureDiagnostic(commitAttempt.Diagnostic, worktreeEvidence));
                }
                else
                {
                    standardErrorDiagnostic = AppendDiagnostic(
                        WithDeferredNoChangeDecline(kernel.GetGoal(goalId), task, worktreeEvidence, fullStandardOutput.Content is null ? null : authoritativeStandardOutput, decisionStandardError, standardErrorDiagnostic),
                        "Developer/Tester dispatch exited 0 but left the worktree dirty. " +
                        $"branch={worktreeEvidence.Branch}; head={worktreeEvidence.Head}; worktree={worktreeEvidence.WorktreeStatus}; " +
                        $"commits_after_dispatch={worktreeEvidence.CommitsAfterDispatch}; status_short={worktreeEvidence.StatusShort}.");
                }
            }
            else if (!orchestratorCommitted && worktreeEvidence.IsClean)
            {
                var hasCompletedVerification = _completionClassifier.HasCompletedVerification(
                    decisionStandardOutput,
                    decisionStandardError);
                var hasVerificationOnlyTesterCompletion = _completionClassifier.IsVerificationOnlyTesterCompletion(
                    task,
                    decisionStandardOutput,
                    hasCompletedVerification);
                var verificationRecognized =
                    hasCompletedVerification ||
                    hasVerificationOnlyTesterCompletion ||
                    _completionClassifier.HasReportedFailingVerification(decisionStandardOutput, decisionStandardError);
                var roleStillRequiresChangeEvidence =
                    dispatchRoleCapability == DispatchRoleOutputCapability.RequiresChangeEvidence;
                var allowsNoChangeCompletion = _completionClassifier.AllowsNoChangeCompletion(
                    task, decisionStandardOutput, decisionStandardError) ||
                    (fullStandardOutput.Content is not null && TryAcceptDeferredNoChange(
                        kernel.GetGoal(goalId), task, worktreeEvidence, authoritativeStandardOutput,
                        decisionStandardError, ref standardErrorDiagnostic));
                var requiresCommitEvidence =
                    !successfulChildWithoutUsableWorkerResult &&
                    _completionClassifier.RequiresPostDispatchCommitEvidence(task, hasVerificationOnlyTesterCompletion) &&
                    ((roleStillRequiresChangeEvidence && !allowsNoChangeCompletion) ||
                      (!verificationRecognized &&
                       !allowsNoChangeCompletion)) &&
                    !worktreeEvidence.HasRelevantCommitAfterDispatch;

                if (requiresCommitEvidence)
                {
                    // The role had to land a relevant change and didn't — fail regardless of exit code
                    // (a Developer that produced nothing is a real failure, not exit-code noise).
                    exitCode = 1;
                    standardErrorDiagnostic = AppendDiagnostic(
                        AppendDiagnostic(
                            AppendDiagnostic(
                                standardErrorDiagnostic ?? string.Empty,
                                DispatchRejectionDiagnosticMarker.Format(
                                    verificationRecognized,
                                    worktreeEvidence.CommitsAfterDispatch,
                                    worktreeEvidence.ChangedPathsSummary)),
                            "Developer/Tester dispatch did not produce required relevant file-change evidence. " +
                            $"branch={worktreeEvidence.Branch}; head={worktreeEvidence.Head}; worktree={worktreeEvidence.WorktreeStatus}; " +
                            $"commits_after_dispatch={worktreeEvidence.CommitsAfterDispatch}; changed_paths={worktreeEvidence.ChangedPathsSummary}."),
                        DispatchFailureDiagnosticMarker.Format(DispatchFailureDiagnosticMarker.RequiredFileChangeEvidenceMissing));
                }
            }

            failedRoundReceipt = TryBuildFailedRoundReceipt(task, BuildDispatchId(goalId, taskId, task.LastDispatch),
                worktreeEvidence, orchestratorCommitted || (commitAttempted && commitAttempt.Succeeded),
                checkpointDisposition?.IsCheckpoint == true,
                task.LastDispatch.SandboxLowIntegrity && !lowIntegrityConfinementEvidence, processRecord.WasCancelled);

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

        resourceAccounting ??= _recoveryService.ReleaseTrackedProcessJobs(processRecord);
        if (resourceAccounting is not null &&
            !resourceAccounting.Reaped &&
            DispatchProcessRecoveryService.IsDispatchHostReapCompletion(decisionStandardError))
        {
            resourceAccounting = resourceAccounting with { Reaped = true };
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
        else if (successfulChildWithoutUsableWorkerResult)
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

        var humanInputQuestion = humanInputDirective.Directive?.Question;
        // Keep orchestrator-ingested plan text in the captured stdout artifact, whose path is
        // recorded below, but out of the worker decision stream and bounded verification
        // snapshot. Kernel classification reparses the snapshot for directives and blockers.
        var fullStandardError = _processLogReader.ReadComplete(processRecord.StandardErrorPath);
        var standardOutput = outputSnapshot.BoundedText;
        var standardError = AppendDiagnostic(
            AppendDiagnostic(errorSnapshot.BoundedText, standardErrorDiagnostic),
            finalPlannerRejectionDiagnostic);
        if (!exitArtifactAlreadyExisted)
        {
            _recoveryService.TryWriteExitCode(
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
        var resultCommit = checkpointDisposition?.IsCheckpoint == true
            ? null
            : TryGetWorktreeHead(processRecord.WorkingDirectory);
        var resultCommitProvenance = hasCommittedChanges
            ? orchestratorCommitted ? "orchestrator" : "worker"
            : null;

        // Worker-self-reported stdout bytes from the heartbeat — a flush-race-proof signal of real output.
        var heartbeatStdoutBytes = _recoveryService.TryReadHeartbeat(GetHeartbeatPath(processRecord), out var completionHeartbeat)
            ? completionHeartbeat.StandardOutputBytes
            : (long?)null;
        // The bounded snapshot is diagnostic evidence only. A scope declaration can steer a
        // Developer into a bounded revision, so it must come from the authoritative artifact.
        bool assignedScopeComplete = false;
        var hasAssignedScopeComplete = fullStandardOutput.Content is { } assignedScopeOutput &&
            WorkerResultBlockers.TryGetAssignedScopeComplete(
                assignedScopeOutput,
                out assignedScopeComplete,
                out _);

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
            HumanInputKind: humanInputDirective.Directive?.Kind,
            HumanInputEvidenceOwner: humanInputDirective.Directive?.EvidenceOwner, HumanInputStoreReference: humanInputDirective.Directive?.StoreReference,
            ObservedRootExitCode: observedExitCode,
            ReconciledToSuccess: wrapperExitReconciled,
            ReconciliationOriginRule: wrapperExitReconciled ? reconciliationOriginRule : null,
            FullStandardOutput: fullStandardOutput.Content,
            FullStandardError: fullStandardError.Content,
            FullStandardOutputUnavailableReason: fullStandardOutput.UnavailableReason,
            FullStandardErrorUnavailableReason: fullStandardError.UnavailableReason,
            PlannerCandidateDivergence: plannerCandidateDivergence,
            AssignedScopeComplete: hasAssignedScopeComplete ? assignedScopeComplete : null);

        var outcome = new DispatchRefreshOutcome(
            completed,
            verification,
            resultCommit,
            resultCommitProvenance,
            recoveryDecision,
            providerFailureKind,
            new DispatchDiagnosticPayload(exitCode, standardOutput, standardError),
            AutoRequeueDisposition: DispatchAutoRequeueDisposition.FromInterruptedWorkCheckpoint(
                checkpointDisposition,
                recoveryDecision),
            ProviderUsage: providerUsage.Usage,
            ProviderUsageUnavailableReason: providerUsage.UnavailableReason,
            DispatchAttemptAt: contextReceiptAttemptAt, ReceiptlessUsageAttemptAt: ReceiptlessUsageAttemptAt(dispatchAttempt), FailedRoundReceipt: failedRoundReceipt,
            SkillsReceipt: ResolveSkillsReceipt(goalId, taskId, dispatchAttempt, processRecord.StandardOutputPath, verification));
        _processLogReader.Evict(processRecord);
        return outcome;
    }

    private static TaskDispatchRecord? ResolveDispatchAttempt(TaskSpec task, TaskProcessRecord processRecord) =>
        task.DispatchHistory
            .Where(dispatch => dispatch.DispatchedAt <= processRecord.StartedAt)
            .OrderByDescending(dispatch => dispatch.DispatchedAt)
            .FirstOrDefault();

    internal static bool ShouldReconcileWrapperExit(WrapperExitReconciliationEvidence evidence) =>
        WorkerDispatchCompletionClassifier.ShouldReconcileWrapperExit(evidence);

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
        var resourceAccounting = _recoveryService.ReapTrackedProcessJobs(processRecord, waitForExit: true);
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
            DispatchProcessRecoveryService.WithAction(
                DispatchRecoveryAction.Reap,
                recoveryDecision,
                completionDiagnostic),
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
        var resourceAccounting = _recoveryService.ReapTrackedProcessJobs(processRecord, waitForExit: true);
        var exitRead = _recoveryService.ReadExitCodeWithRetry(processRecord.ExitCodePath);
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
            DispatchProcessRecoveryService.WithAction(recoveryAction, recoveryDecision, completionDiagnostic),
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

        var standardOutput = _processLogReader.ReadBestEffort(processRecord, processRecord.StandardOutputPath).DecisionText;
        var standardError = _processLogReader.ReadBestEffort(processRecord, processRecord.StandardErrorPath).DecisionText;
        var hasPopulatedStandardOutput =
            ProcessLogReader.SafeFileLength(processRecord.StandardOutputPath) > 0L ||
            (_recoveryService.TryReadHeartbeat(GetHeartbeatPath(processRecord), out var heartbeat) &&
             heartbeat.StandardOutputBytes > 0L);
        var hasSuccessfulWorkerResult = _completionClassifier.HasSuccessfulWorkerResult(
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
            _worktreeCommitter.InspectGoalWorktree(processRecord.WorkingDirectory, goalId, dispatch.DispatchedAt) is
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

    private static string TryResolveIndexLockPath(string workingDirectory)
    {
        try
        {
            return GoalWorktreeGitMetadata.Inspect(workingDirectory).IndexLockPath;
        }
        catch
        {
            return Path.Combine(workingDirectory, ".git", "index.lock");
        }
    }

    public TaskProcessRecord CancelLatestProcess(AgentOrchestratorKernel kernel, GoalId goalId, TaskId taskId) =>
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

        var resourceAccounting = ReapForCancellation(processRecord, bypassTrackedJobRegistry);
        var cancelledAt = _clock.UtcNow;
        var cancelled = processRecord with
        {
            CompletedAt = cancelledAt,
            WasCancelled = true,
            ResourceAccounting = resourceAccounting,
            WasCancelledByConductor = cancelledByConductor
        };

        var candidateEvidence = CancellationCandidateEvidenceClassifier.Classify(
            task, processRecord, goalId, _worktreeCommitter);
        kernel.RecordTaskProcessCancelled(goalId, taskId, cancelled, candidateEvidence);
        try
        {
            if (resourceAccounting is not null)
            {
                kernel.RecordTaskNote(goalId, taskId, FormatResourceReceipt(goalId, taskId, resourceAccounting));
            }

            if (task.RequiredRole == AgentRole.Planner &&
                task.LastDispatch?.PlannerSampleCount is > 1)
            {
                try
                {
                    PlannerSampleDispatcher.RecordCancelledSamples(
                        processRecord.StandardOutputPath,
                        task.LastDispatch.PlannerSampleCount,
                        cancelledAt);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    kernel.RecordTaskNote(
                        goalId,
                        taskId,
                        $"Planner sample cancellation evidence could not be persisted: {ex.Message}");
                }
            }
        }
        finally
        {
            _processLogReader.Evict(processRecord);
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

            CancelLatestProcess(kernel, goalId, task.Id, cancelledByConductor: true);
            cancelled++;
        }

        return cancelled;
    }

    public int DetachRunningProcessesForGoal(AgentOrchestratorKernel kernel, GoalId goalId) =>
        GracefulDispatchDetacher.DetachRunningProcessesForGoal(
            kernel,
            goalId,
            _processLogReader.Evict,
            taskId => CancelLatestProcess(
                kernel,
                goalId,
                taskId,
                cancelledByConductor: true,
                bypassTrackedJobRegistry: true), _isStillRunning, _clock.UtcNow);

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
                        _processLogReader.Evict(cancelledProcess);
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
                     WorkerProcessJobs.WasDetachedByConductor(
                         $"{goal.Id.Value}:{task.Id.Value}",
                         detachedFailure.ProcessId,
                         detachedFailure.StartedAt)) &&
                    task.LastVerification?.WorkerResultPresent != true &&
                    !_recoveryService.AnyTrackedProcessStillRunning(detachedFailure))
                {
                    _processLogReader.Evict(detachedFailure);
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
                    _recoveryService.AnyTrackedProcessStillRunning(process))
                {
                    continue;
                }

                _processLogReader.Evict(process);
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

        if (IsRequeueRefusedByOperatorCancelIntent(goalId, currentTask, dispatchId))
            return false;

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

        kernel.RequeueInterruptedDispatch(
            goalId,
            taskId,
            message,
            RetryCause.ProviderInterruption,
            dispatchId);
        return true;
    }

    internal static bool TryRejectInterruptedDispatchRecovery(
        AgentOrchestratorKernel kernel,
        GoalId goalId,
        TaskId taskId,
        Func<GoalId, TaskId, InterruptedDispatchStateRead>? readCurrentState)
    {
        if (kernel.GetTask(goalId, taskId).InterruptedDispatchRecoveryId is not { } dispatchId ||
            !TryReadAutoRequeueBlocker(kernel, goalId, taskId, readCurrentState, out var blocker))
            return false;

        kernel.RecordTaskRequeueSkipped(goalId, taskId, dispatchId, blocker.BlockingEntity,
            blocker.TerminalState, blocker.Reason, blocker.Detail);
        if (blocker.Reason == "terminal-state")
            kernel.ConcludeInterruptedDispatchRecovery(goalId, taskId, blocker.GoalStatus, blocker.TaskStatus);
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

    internal static string BuildDispatchId(GoalId goalId, TaskId taskId, TaskDispatchRecord dispatch)
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

    private string? ReapWorktreeBuildDaemons(string workingDirectory) =>
        WorktreeBuildDaemonReaper.Reap(workingDirectory, _findBuildDaemons, _tryKillBuildDaemon);

    private static bool IsStillRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (Exception exception) when (ProcessProbeFailure.IsNotLive(exception))
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

    internal static CodexJsonlParseResult? NormalizeStructuredCodexOutput(
        TaskDispatchRecord? dispatch,
        string standardOutputPath,
        Func<string, string>? readAllText = null)
        => StructuredCodexOutputNormalizer.Normalize(dispatch, standardOutputPath, readAllText).Parsed;

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

    internal static string GetHeartbeatPath(TaskProcessRecord processRecord) =>
        DispatchProcessRecoveryService.GetHeartbeatPath(processRecord);

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

    private static bool TryKillProcess(int processId) =>
        WorkerProcessJobs.TryKillOrFallback(processId);

    internal static string FormatResourceReceipt(
        GoalId goalId,
        TaskId taskId,
        TaskProcessResourceAccounting accounting) =>
        $"RESOURCE goal={goalId.Value[..8]} task={taskId.Value[..8]} cpu_ms={accounting.CpuMilliseconds} peak_mem_bytes={accounting.PeakMemoryBytes} io_bytes={accounting.IoBytes} accounting_source={accounting.AccountingSource}{(accounting.Reaped ? " reaped=true" : string.Empty)}";

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

    internal readonly record struct WrapperExitReconciliationEvidence(
        int ObservedRootExitCode,
        int? ChildExitCode,
        bool HasCompleteNonBlockedWorkerResult,
        bool CompletionContractSucceeded,
        bool HasKnownRoleCapability,
        DispatchRoleOutputCapability RoleCapability,
        bool HasRelevantChangeEvidence,
        bool HasTerminalHumanInputDirective,
        bool HasFatalOrchestratorFailure);

}
