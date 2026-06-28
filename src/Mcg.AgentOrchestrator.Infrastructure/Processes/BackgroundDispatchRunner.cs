using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed record DispatchRefreshOutcome(TaskProcessRecord ProcessRecord, TaskVerificationRecord? Verification, string? ResultCommit = null);

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
        TimeSpan? startupHangTimeout = null)
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
    }

    private static bool IsDispatchStartDisabledByEnvironment()
    {
        var value = Environment.GetEnvironmentVariable(DisableDispatchStartVariable);
        return value is "1" || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }

    public TaskProcessRecord StartLatestDispatch(AgentOrchestratorKernel kernel, GoalId goalId, TaskId taskId, string logRoot)
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

        // OS worker sandbox: for write-capable subscription dispatches (Developer/Tester), run the
        // worker AS the dedicated low-priv account, confined by ACL to the worktree + git common dir.
        var sandbox = WorkerSandboxOptions.FromEnvironment();
        var useSandbox = sandbox.Enabled && !isLocalDispatch &&
            task.RequiredRole is AgentRole.Developer or AgentRole.Tester;
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
            Provider: ResolveSandboxProvider(dispatch)));

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
            startInfo.Environment[DispatchProcessHost.StartGatePathVariable] = startGatePath;
        }

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
        ReleaseDispatchHostStartGate(startGatePath);

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
        return record;
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

    private static WorkerSandboxProvider ResolveSandboxProvider(TaskDispatchRecord dispatch)
    {
        if (dispatch.ProviderName?.Equals("Anthropic", StringComparison.OrdinalIgnoreCase) == true ||
            dispatch.WorkerName.Contains("claude", StringComparison.OrdinalIgnoreCase) ||
            dispatch.Command.Contains("claude", StringComparison.OrdinalIgnoreCase))
        {
            return WorkerSandboxProvider.Claude;
        }

        if (dispatch.ProviderName?.Equals("OpenAI", StringComparison.OrdinalIgnoreCase) == true ||
            dispatch.WorkerName.Contains("codex", StringComparison.OrdinalIgnoreCase) ||
            dispatch.Command.Contains("codex", StringComparison.OrdinalIgnoreCase))
        {
            return WorkerSandboxProvider.Codex;
        }

        if (dispatch.ProviderName?.Equals("Ollama", StringComparison.OrdinalIgnoreCase) == true ||
            dispatch.WorkerName.Contains("qwen", StringComparison.OrdinalIgnoreCase) ||
            dispatch.Command.Contains("qwen", StringComparison.OrdinalIgnoreCase))
        {
            return WorkerSandboxProvider.Ollama;
        }

        return WorkerSandboxProvider.Unknown;
    }

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
                if (process is null || task.LastVerification is not null)
                    continue;

                if (!TryCompleteFromExitFile(kernel, goal.Id, task.Id, process, out var outcome))
                    continue;

                ApplyRefreshOutcome(kernel, goal.Id, task.Id, outcome);
                reconciled++;
            }
        }

        return reconciled;
    }

    public TaskProcessRecord RefreshLatestProcess(AgentOrchestratorKernel kernel, GoalId goalId, TaskId taskId)
    {
        var outcome = ReconcileLatestProcess(kernel, goalId, taskId);
        ApplyRefreshOutcome(kernel, goalId, taskId, outcome);
        return outcome.ProcessRecord;
    }

    public DispatchRefreshOutcome ReconcileLatestProcess(AgentOrchestratorKernel kernel, GoalId goalId, TaskId taskId)
    {
        var task = kernel.GetTask(goalId, taskId);
        var processRecord = task.LastProcess
            ?? throw new InvalidOperationException($"Task '{taskId}' has no background process to refresh.");

        var exitFileExists = File.Exists(processRecord.ExitCodePath);
        if (TryCompleteFromExitFile(kernel, goalId, taskId, processRecord, out var completion))
            return completion;

        if (_isStillRunning(processRecord.ProcessId))
        {
            if (TryDetectHungCodexWrapper(task, processRecord, out var diagnostic))
            {
                TryKillTrackedProcesses(processRecord, waitForExit: true);
                if (RequiresFileChangeEvidence(task) &&
                    TryInspectGoalWorktree(processRecord.WorkingDirectory, goalId, task.LastDispatch!.DispatchedAt, out var wt) &&
                    wt.IsClean && wt.HasRelevantCommitAfterDispatch)
                {
                    var reapNote =
                        "Background dispatch wrapper appears hung after codex final output; no exit file was written. " +
                        $"Wrapper process reaped; task completed based on relevant file-change evidence " +
                        $"(branch={wt.Branch}; head={wt.Head}; commits_after_dispatch={wt.CommitsAfterDispatch}).";
                    TryWriteExitCode(processRecord.ExitCodePath, 0);
                    return BuildCompletedProcessOutcome(kernel, goalId, taskId, processRecord, 0, reapNote);
                }

                TryWriteExitCode(processRecord.ExitCodePath, 1);
                return BuildCompletedProcessOutcome(kernel, goalId, taskId, processRecord, 1, diagnostic);
            }

            if (TryDetectHungSubscriptionWrapper(task, processRecord, out var wrapperDiagnostic))
            {
                TryKillTrackedProcesses(processRecord, waitForExit: true);
                if (RequiresFileChangeEvidence(task) &&
                    TryInspectGoalWorktree(processRecord.WorkingDirectory, goalId, task.LastDispatch!.DispatchedAt, out var wt) &&
                    wt.IsClean && wt.HasRelevantCommitAfterDispatch)
                {
                    var reapNote =
                        "Background dispatch wrapper appears hung with stalled heartbeat; no exit file was written. " +
                        $"Wrapper process reaped; task completed based on relevant file-change evidence " +
                        $"(branch={wt.Branch}; head={wt.Head}; commits_after_dispatch={wt.CommitsAfterDispatch}).";
                    TryWriteExitCode(processRecord.ExitCodePath, 0);
                    return BuildCompletedProcessOutcome(kernel, goalId, taskId, processRecord, 0, reapNote);
                }

                TryWriteExitCode(processRecord.ExitCodePath, 1);
                return BuildCompletedProcessOutcome(kernel, goalId, taskId, processRecord, 1, wrapperDiagnostic);
            }

            if (TryDetectStartupHang(processRecord, out var startupHangDiagnostic))
            {
                TryKillTrackedProcesses(processRecord, waitForExit: true);
                TryWriteExitCode(processRecord.ExitCodePath, 1);
                return BuildCompletedProcessOutcome(kernel, goalId, taskId, processRecord, 1, startupHangDiagnostic);
            }

            if (TryDetectProbableProgressStall(task, goalId, processRecord, out var stallDiagnostic))
            {
                TryKillTrackedProcesses(processRecord, waitForExit: true);
                TryWriteExitCode(processRecord.ExitCodePath, 1);
                return BuildCompletedProcessOutcome(kernel, goalId, taskId, processRecord, 1, stallDiagnostic);
            }

            return new DispatchRefreshOutcome(processRecord, null);
        }

        if (exitFileExists)
        {
            return new DispatchRefreshOutcome(processRecord, null);
        }

        TryKillTrackedProcesses(processRecord, waitForExit: false);
        return BuildCompletedProcessOutcome(kernel, goalId, taskId, processRecord, 1);
    }

    private bool TryCompleteFromExitFile(
        AgentOrchestratorKernel kernel,
        GoalId goalId,
        TaskId taskId,
        TaskProcessRecord processRecord,
        out DispatchRefreshOutcome outcome)
    {
        IReadOnlyList<int>? ownedProcessIds = null;
        if (TryReadHeartbeat(GetHeartbeatPath(processRecord), out var heartbeat))
        {
            if (heartbeat.ChildProcessId is not null)
            {
                outcome = new DispatchRefreshOutcome(processRecord, null);
                return false;
            }

            ownedProcessIds = heartbeat.OwnedProcessIds;
        }

        if (ownedProcessIds?.Any(_isStillRunning) == true)
        {
            outcome = new DispatchRefreshOutcome(processRecord, null);
            return false;
        }

        if (!TryReadExitCode(processRecord.ExitCodePath, out var exitCode))
        {
            outcome = new DispatchRefreshOutcome(processRecord, null);
            return false;
        }

        if (AnyTrackedProcessStillRunning(processRecord))
        {
            TryKillTrackedProcesses(processRecord, waitForExit: true);
        }

        outcome = BuildCompletedProcessOutcome(kernel, goalId, taskId, processRecord, exitCode);
        return true;
    }

    public static void ApplyRefreshOutcome(
        AgentOrchestratorKernel kernel,
        GoalId goalId,
        TaskId taskId,
        DispatchRefreshOutcome outcome)
    {
        kernel.RecordTaskProcessRefreshed(goalId, taskId, outcome.ProcessRecord, outcome.Verification);
        if (outcome.ResultCommit is not null)
            kernel.RecordDispatchResultCommit(goalId, taskId, outcome.ResultCommit);
    }

    private DispatchRefreshOutcome BuildCompletedProcessOutcome(
        AgentOrchestratorKernel kernel,
        GoalId goalId,
        TaskId taskId,
        TaskProcessRecord processRecord,
        int exitCode,
        string? standardErrorDiagnostic = null)
    {
        var standardOutput = ReadBestEffort(processRecord.StandardOutputPath);
        var standardError = ReadBestEffort(processRecord.StandardErrorPath);
        ReleaseTrackedProcessJobs(processRecord);
        var task = kernel.GetTask(goalId, taskId);
        var workerResultPresent = HasWorkerResultArtifact(processRecord.WorkingDirectory, standardOutput, standardError);
        var hasCommittedChanges = false;
        if (RequiresFileChangeEvidence(task) &&
            TryInspectGoalWorktree(processRecord.WorkingDirectory, goalId, task.LastDispatch!.DispatchedAt, out var worktreeEvidence))
        {
            hasCommittedChanges = worktreeEvidence.HasRelevantCommitAfterDispatch;
            // Default path: a Developer/Tester that edited the worktree and showed verification
            // evidence does not need to self-commit. The orchestrator stages and commits the dirty
            // diff after guards pass. Dirty-but-unverified edits are left dirty and fail.
            var orchestratorCommitted = false;
            var sandboxCommitBlocked = HasSandboxCommitBlockedEvidence(processRecord, standardOutput, standardError);
            if (!worktreeEvidence.IsClean &&
                task.LastDispatch.SandboxLowIntegrity &&
                (HasClassifiedVerificationEvidence(task, standardOutput, standardError) ||
                 sandboxCommitBlocked ||
                 worktreeEvidence.HasRelevantCommitAfterDispatch) &&
                TryCommitWorktreeEdits(
                    processRecord.WorkingDirectory,
                    BuildOrchestratorCommitSubject(task, standardOutput, standardError)) &&
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
            }

            if (!orchestratorCommitted && !worktreeEvidence.IsClean && exitCode == 0)
            {
                // Exited 0 but left uncommitted edits the orchestrator could not land (no verification
                // evidence, or the commit failed) — not acceptable.
                exitCode = 1;
                standardErrorDiagnostic = AppendDiagnostic(
                    standardErrorDiagnostic ?? string.Empty,
                    "Developer/Tester dispatch exited 0 but left the worktree dirty. " +
                    $"branch={worktreeEvidence.Branch}; head={worktreeEvidence.Head}; worktree={worktreeEvidence.WorktreeStatus}; " +
                    $"commits_after_dispatch={worktreeEvidence.CommitsAfterDispatch}; status_short={worktreeEvidence.StatusShort}.");
            }
            else if (!orchestratorCommitted && worktreeEvidence.IsClean)
            {
                var requiresCommitEvidence =
                    RequiresPostDispatchCommitEvidence(task, standardOutput, standardError, workerResultPresent) &&
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
                else if (exitCode != 0 &&
                    HasCompletedVerification(task, standardOutput, standardError))
                {
                    // Clean worktree, no commit required (e.g. a Tester verifying already-committed work),
                    // and the worker produced verification evidence — but it exited non-zero. Under the
                    // Low-IL sandbox the worker's exit code is unreliable (a benign access-denied during
                    // shutdown yields a non-zero exit even on success). The deliverable is present and the
                    // acceptance gate re-verifies, so accept rather than fail on the exit code.
                    exitCode = 0;
                    standardErrorDiagnostic = AppendDiagnostic(
                        standardErrorDiagnostic ?? string.Empty,
                        "Accepted on verification evidence despite a non-zero worker exit (clean worktree; " +
                        "worker exit codes are unreliable under the low-integrity sandbox). " +
                        $"branch={worktreeEvidence.Branch}; head={worktreeEvidence.Head}.");
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

        standardError = AppendDiagnostic(standardError, standardErrorDiagnostic);
        TryWriteExitCode(processRecord.ExitCodePath, exitCode);
        var completed = processRecord with
        {
            CompletedAt = _clock.UtcNow,
            ExitCode = exitCode
        };

        // Capture resultCommit after all orchestrator commits — the right boundary for file attribution.
        var resultCommit = TryGetWorktreeHead(processRecord.WorkingDirectory);

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
            HeartbeatStandardOutputBytes: heartbeatStdoutBytes);

        TryWriteDiagnosticRecord(goalId, taskId, processRecord, exitCode, standardOutput, standardError);
        return new DispatchRefreshOutcome(completed, verification, resultCommit);
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
            (HasCompletedVerification(task, standardOutput, standardError) || workerResultPresent);
    }

    // A verification-role worker proves it did its job either with a recognised test-runner result OR
    // by emitting its WORKER_RESULT contract block (confidence/blockers) — the way Tester/Reviewer
    // workers actually report. The pass-count patterns in HasVerificationEvidence never match that
    // block, so without this a Tester that verified already-committed work is false-failed for "no
    // relevant file-change evidence". Scoped to the clean-worktree path; the acceptance suite re-runs
    // the real tests, and a worker that errored before producing a WORKER_RESULT still fails here.
    private static bool HasClassifiedVerificationEvidence(TaskSpec task, string standardOutput, string standardError)
    {
        var tempVerification = new TaskVerificationRecord(
            string.Empty, string.Empty, 1, standardOutput, standardError, DateTimeOffset.UtcNow);
        return DispatchFailureClassifier.Classify(task, tempVerification).EvidenceSummary.Length > 0;
    }

    private static bool HasCompletedVerification(TaskSpec task, string standardOutput, string standardError)
    {
        return HasClassifiedVerificationEvidence(task, standardOutput, standardError) ||
            standardOutput.Contains("WORKER_RESULT", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasSandboxCommitBlockedEvidence(
        TaskProcessRecord processRecord,
        string standardOutput,
        string standardError)
    {
        var verification = new TaskVerificationRecord(
            processRecord.Command,
            processRecord.WorkingDirectory,
            1,
            standardOutput,
            standardError,
            DateTimeOffset.UtcNow);
        return DispatchFailureClassifier.IsSandboxCommitBlockedFailure(verification);
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
    // pass. Workers edit the worktree; this path deterministically stages and commits the diff. The
    // sandbox scratch dir (.mcg-sandbox) is kept out of the commit via the worktree's local git
    // exclude (ExcludeSandboxFromGit), so a plain `add -A` honours that exclusion. We must NOT pass an
    // explicit ":(exclude).mcg-sandbox" pathspec here: combined with the ignore entry, git treats the
    // ignored path as explicitly requested and exits non-zero ("paths are ignored ... Use -f") AFTER
    // partially staging the real files — which previously left edits staged-but-uncommitted.
    private static bool TryCommitWorktreeEdits(string workingDirectory, string subject)
    {
        try
        {
            var add = GitCli.Run(workingDirectory, "add", "-A");
            if (!add.Succeeded)
            {
                return false;
            }

            var staged = GitCli.Run(workingDirectory, "diff", "--cached", "--name-only");
            if (staged.ExitCode != 0 || string.IsNullOrWhiteSpace(staged.Output))
            {
                // Nothing to commit (e.g. only the excluded sandbox scratch was dirty) — leave the
                // dispatch to fail/report rather than create an empty commit.
                return false;
            }

            var commit = GitCli.Run(workingDirectory, "commit", "-m", subject);
            return commit.Succeeded;
        }
        catch
        {
            return false;
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
        var status = GitCli.Run(workingDirectory, "status", "--short");
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

        var filteredStatusOutput = FilterWorkerResultArtifacts(status.Output);
        evidence = new GoalWorktreeDispatchEvidence(
            branch.Output.Trim(),
            head.ExitCode == 0 ? head.Output.Trim() : "unknown",
            status.ExitCode == 0 && string.IsNullOrWhiteSpace(filteredStatusOutput),
            status.ExitCode == 0 && string.IsNullOrWhiteSpace(filteredStatusOutput) ? "clean" : "dirty",
            FormatStatusShort(new GitCli.GitResult(status.ExitCode, filteredStatusOutput, string.Empty)),
            commitsAfterDispatch,
            pathsChangedAfterDispatch);
        return true;
    }

    private static bool IsRelevantSourcePath(string path)
    {
        var normalized = path.Replace('\\', '/').TrimStart('/');
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        return !normalized.Equals(".qwen/settings.json", StringComparison.OrdinalIgnoreCase) &&
            !normalized.Equals("WORKER_RESULT.md", StringComparison.OrdinalIgnoreCase) &&
            !normalized.Equals("WORKER_RESULT.txt", StringComparison.OrdinalIgnoreCase) &&
            !normalized.StartsWith("bin/", StringComparison.OrdinalIgnoreCase) &&
            !normalized.Contains("/bin/", StringComparison.OrdinalIgnoreCase) &&
            !normalized.StartsWith("obj/", StringComparison.OrdinalIgnoreCase) &&
            !normalized.Contains("/obj/", StringComparison.OrdinalIgnoreCase) &&
            !normalized.StartsWith(".scratch/", StringComparison.OrdinalIgnoreCase) &&
            !normalized.StartsWith(".orchestrator-prototype/", StringComparison.OrdinalIgnoreCase) &&
            !normalized.StartsWith("TestResults/", StringComparison.OrdinalIgnoreCase) &&
            !normalized.Contains("/TestResults/", StringComparison.OrdinalIgnoreCase) &&
            !normalized.StartsWith("playwright-report/", StringComparison.OrdinalIgnoreCase) &&
            !normalized.Contains("/playwright-report/", StringComparison.OrdinalIgnoreCase) &&
            !normalized.EndsWith(".log", StringComparison.OrdinalIgnoreCase);
    }

    private static string FilterWorkerResultArtifacts(string statusOutput)
    {
        if (string.IsNullOrWhiteSpace(statusOutput))
        {
            return statusOutput;
        }

        var lines = statusOutput
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Where(line => !IsWorkerResultArtifactStatusLine(line));
        return string.Join("\n", lines);
    }

    private static bool IsWorkerResultArtifactStatusLine(string line)
    {
        // Untracked WORKER_RESULT.md/.txt show as "?? WORKER_RESULT.md" in git status --short.
        // We ignore these as result artifacts, not real work artifacts.
        var trimmed = line.TrimStart();
        if (!trimmed.StartsWith("??", StringComparison.Ordinal))
        {
            return false;
        }

        var filename = trimmed[2..].Trim();
        return string.Equals(filename, "WORKER_RESULT.md", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(filename, "WORKER_RESULT.txt", StringComparison.OrdinalIgnoreCase);
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

        if (processRecord.IsRunning)
        {
            try
            {
                TryKillTrackedProcesses(processRecord, waitForExit: true);
            }
            catch (ArgumentException)
            {
                // Process already exited; still record the user-requested cancellation.
            }
        }

        var cancelled = processRecord with
        {
            CompletedAt = _clock.UtcNow,
            WasCancelled = true
        };
        ReleaseTrackedProcessJobs(processRecord);

        kernel.RecordTaskProcessCancelled(goalId, taskId, cancelled);
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

    private static bool UsesCodexExitFileBehavior(TaskDispatchRecord? dispatch) =>
        WorkerProviderResolver.Resolve(dispatch?.WorkerName).UsesCodexExitFileBehavior;

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

    private static void ReleaseTrackedProcessJobs(TaskProcessRecord processRecord)
    {
        foreach (var processId in processRecord.TrackedProcessIds.Distinct())
        {
            WorkerProcessJobs.Release(processId);
        }
    }

    private static bool TryKillProcess(int processId)
    {
        return WorkerProcessJobs.TryKillOrFallback(processId);
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
                _clock.UtcNow.ToString("O"));

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
        IReadOnlyList<string> ChangedPaths)
    {
        public bool HasCommitAfterDispatch => CommitsAfterDispatch > 0;
        public bool HasRelevantCommitAfterDispatch => ChangedPaths.Any(IsRelevantSourcePath);
        public string ChangedPathsSummary => FormatChangedPaths(ChangedPaths);

        public static GoalWorktreeDispatchEvidence Unknown { get; } = new("unknown", "unknown", false, "unknown", "unavailable", 0, []);
    }
}
