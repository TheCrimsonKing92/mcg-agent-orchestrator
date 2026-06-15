using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed class BackgroundDispatchRunner
{
    public const string DisableDispatchStartVariable = "MCG_ORCHESTRATOR_DISABLE_DISPATCH_START";

    private static readonly TimeSpan DefaultPostOutputIdleTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan DefaultProgressStallTimeout = TimeSpan.FromMinutes(20);
    private static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(30);
    private static readonly string[] BuildServerCandidates = ["VBCSCompiler", "MSBuild"];
    private readonly IClock _clock;
    private readonly TimeSpan _postOutputIdleTimeout;
    private readonly TimeSpan _progressStallTimeout;
    private readonly Func<int, bool> _isStillRunning;
    private readonly bool _processStartDisabled;
    private readonly Func<string, IReadOnlyList<(int ProcessId, string ProcessName, string? CommandLine)>> _findBuildDaemons;
    private readonly Func<int, bool> _tryKillBuildDaemon;

    public BackgroundDispatchRunner(
        IClock? clock = null,
        TimeSpan? postOutputIdleTimeout = null,
        Func<int, bool>? isStillRunning = null,
        bool? disableProcessStart = null,
        Func<string, IReadOnlyList<(int ProcessId, string ProcessName, string? CommandLine)>>? findBuildDaemons = null,
        Func<int, bool>? tryKillBuildDaemon = null,
        TimeSpan? progressStallTimeout = null)
    {
        _clock = clock ?? new SystemClock();
        _postOutputIdleTimeout = postOutputIdleTimeout ?? DefaultPostOutputIdleTimeout;
        _progressStallTimeout = progressStallTimeout ?? DefaultProgressStallTimeout;
        _isStillRunning = isStillRunning ?? IsStillRunning;
        _processStartDisabled = disableProcessStart ?? IsDispatchStartDisabledByEnvironment();
        _findBuildDaemons = findBuildDaemons ?? FindBuildDaemons;
        _tryKillBuildDaemon = tryKillBuildDaemon ?? TryKillBuildDaemonProcess;
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

        var isLocalDispatch = IsLocalDispatch(dispatch);
        var parametersPath = Path.Combine(logRoot, $"{prefix}.dispatch.json");
        DispatchProcessHost.WriteParameters(parametersPath, new DispatchProcessHost.DispatchRunParameters(
            dispatch.Command,
            dispatch.WorkingDirectory,
            stdoutPath,
            stderrPath,
            exitCodePath,
            heartbeatPath,
            ShutdownBuildServerOnExit: !isLocalDispatch,
            DisableSharedCompilation: !isLocalDispatch));

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

        startInfo.ArgumentList.Add("exec");
        startInfo.ArgumentList.Add(ResolveDispatchHostAssembly());
        startInfo.ArgumentList.Add(DispatchProcessHost.SubcommandName);
        startInfo.ArgumentList.Add(parametersPath);

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start background dispatch process.");

        var record = new TaskProcessRecord(
            process.Id,
            dispatch.Command,
            dispatch.WorkingDirectory,
            stdoutPath,
            stderrPath,
            exitCodePath,
            _clock.UtcNow,
            null,
            null);

        kernel.RecordTaskProcessStarted(goalId, taskId, record);
        return record;
    }

    /// <summary>
    /// Scans all Running tasks across all goals for an exit file and auto-reconciles any
    /// whose dispatched process has written its exit code. Idempotent: a task whose
    /// process already completed (IsRunning == false) is skipped on repeat calls.
    /// Returns the number of tasks reconciled.
    /// </summary>
    public int SweepExitedProcesses(AgentOrchestratorKernel kernel)
    {
        var reconciled = 0;
        foreach (var goal in kernel.Goals)
        {
            foreach (var task in goal.Tasks)
            {
                var process = task.LastProcess;
                if (process is not { IsRunning: true })
                    continue;

                if (!TryReadExitCode(process.ExitCodePath, out _))
                    continue;

                RefreshLatestProcess(kernel, goal.Id, task.Id);
                reconciled++;
            }
        }

        return reconciled;
    }

    public TaskProcessRecord RefreshLatestProcess(AgentOrchestratorKernel kernel, GoalId goalId, TaskId taskId)
    {
        var task = kernel.GetTask(goalId, taskId);
        var processRecord = task.LastProcess
            ?? throw new InvalidOperationException($"Task '{taskId}' has no background process to refresh.");

        var exitFileExists = File.Exists(processRecord.ExitCodePath);
        if (TryReadExitCode(processRecord.ExitCodePath, out var exitCode))
        {
            if (_isStillRunning(processRecord.ProcessId))
            {
                TryKillProcess(processRecord.ProcessId);
            }

            return RecordCompletedProcess(kernel, goalId, taskId, processRecord, exitCode);
        }

        if (_isStillRunning(processRecord.ProcessId))
        {
            if (TryDetectHungCodexWrapper(task, processRecord, out var diagnostic))
            {
                TryKillProcess(processRecord.ProcessId);
                if (RequiresFileChangeEvidence(task) &&
                    TryInspectGoalWorktree(processRecord.WorkingDirectory, goalId, task.LastDispatch!.DispatchedAt, out var wt) &&
                    wt.IsClean && wt.HasRelevantCommitAfterDispatch)
                {
                    var reapNote =
                        "Background dispatch wrapper appears hung after codex final output; no exit file was written. " +
                        $"Wrapper process reaped; task completed based on relevant file-change evidence " +
                        $"(branch={wt.Branch}; head={wt.Head}; commits_after_dispatch={wt.CommitsAfterDispatch}).";
                    TryWriteExitCode(processRecord.ExitCodePath, 0);
                    return RecordCompletedProcess(kernel, goalId, taskId, processRecord, 0, reapNote);
                }

                TryWriteExitCode(processRecord.ExitCodePath, 1);
                return RecordCompletedProcess(kernel, goalId, taskId, processRecord, 1, diagnostic);
            }

            if (TryDetectProbableProgressStall(task, goalId, processRecord, out var stallDiagnostic))
            {
                TryKillProcess(processRecord.ProcessId);
                TryWriteExitCode(processRecord.ExitCodePath, 1);
                return RecordCompletedProcess(kernel, goalId, taskId, processRecord, 1, stallDiagnostic);
            }

            kernel.RecordTaskProcessRefreshed(goalId, taskId, processRecord, null);
            return processRecord;
        }

        if (exitFileExists)
        {
            kernel.RecordTaskProcessRefreshed(goalId, taskId, processRecord, null);
            return processRecord;
        }

        return RecordCompletedProcess(kernel, goalId, taskId, processRecord, 1);
    }

    private TaskProcessRecord RecordCompletedProcess(
        AgentOrchestratorKernel kernel,
        GoalId goalId,
        TaskId taskId,
        TaskProcessRecord processRecord,
        int exitCode,
        string? standardErrorDiagnostic = null)
    {
        var standardOutput = ReadBestEffort(processRecord.StandardOutputPath);
        var standardError = ReadBestEffort(processRecord.StandardErrorPath);
        var task = kernel.GetTask(goalId, taskId);
        if (RequiresFileChangeEvidence(task) &&
            TryInspectGoalWorktree(processRecord.WorkingDirectory, goalId, task.LastDispatch!.DispatchedAt, out var worktreeEvidence) &&
            exitCode == 0)
        {
            if (!worktreeEvidence.IsClean)
            {
                exitCode = 1;
                standardErrorDiagnostic = AppendDiagnostic(
                    standardErrorDiagnostic ?? string.Empty,
                    "Developer/Tester dispatch exited 0 but left the worktree dirty. " +
                    $"branch={worktreeEvidence.Branch}; head={worktreeEvidence.Head}; worktree={worktreeEvidence.WorktreeStatus}; " +
                    $"commits_after_dispatch={worktreeEvidence.CommitsAfterDispatch}; status_short={worktreeEvidence.StatusShort}.");
            }
            else if (RequiresPostDispatchCommitEvidence(task, standardOutput, standardError) &&
                !AllowsNoChangeCompletion(task, standardOutput, standardError) &&
                !worktreeEvidence.HasRelevantCommitAfterDispatch)
            {
                exitCode = 1;
                standardErrorDiagnostic = AppendDiagnostic(
                    standardErrorDiagnostic ?? string.Empty,
                    "Developer/Tester dispatch exited 0 but did not produce required relevant file-change evidence. " +
                    $"branch={worktreeEvidence.Branch}; head={worktreeEvidence.Head}; worktree={worktreeEvidence.WorktreeStatus}; " +
                    $"commits_after_dispatch={worktreeEvidence.CommitsAfterDispatch}; changed_paths={worktreeEvidence.ChangedPathsSummary}.");
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

        var verification = new TaskVerificationRecord(
            processRecord.Command,
            processRecord.WorkingDirectory,
            exitCode,
            standardOutput,
            standardError,
            completed.CompletedAt.Value,
            StandardOutputPath: processRecord.StandardOutputPath,
            StandardErrorPath: processRecord.StandardErrorPath);

        kernel.RecordTaskProcessRefreshed(goalId, taskId, completed, verification);
        return completed;
    }

    private static bool RequiresFileChangeEvidence(TaskSpec task)
    {
        return task.LastDispatch is { } dispatch &&
            !IsLocalDispatch(dispatch) &&
            task.RequiredRole is AgentRole.Developer or AgentRole.Tester;
    }

    private static bool RequiresPostDispatchCommitEvidence(TaskSpec task, string standardOutput, string standardError)
    {
        return task.RequiredRole switch
        {
            AgentRole.Developer => true,
            AgentRole.Tester => !IsVerificationOnlyTesterCompletion(task, standardOutput, standardError),
            _ => false
        };
    }

    private static bool IsVerificationOnlyTesterCompletion(TaskSpec task, string standardOutput, string standardError)
    {
        return task.RequiredRole == AgentRole.Tester &&
            !TesterTaskRequestsFileChanges(task) &&
            DispatchFailureClassifier.HasVerificationEvidence(standardOutput, standardError);
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

        var branch = RunGit(workingDirectory, "branch", "--show-current");
        var expectedBranch = GoalWorktrees.BranchName(goalId);
        if (branch.ExitCode != 0 || !string.Equals(branch.Output.Trim(), expectedBranch, StringComparison.Ordinal))
        {
            return false;
        }

        var head = RunGit(workingDirectory, "rev-parse", "--short", "HEAD");
        var status = RunGit(workingDirectory, "status", "--short");
        var dispatch = RunGit(workingDirectory, "log", "--format=%H", $"--since={dispatchedAt:O}");
        var changedPaths = RunGit(workingDirectory, "log", "--name-only", "--format=", $"--since={dispatchedAt:O}");
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
            FormatStatusShort(new GitResult(status.ExitCode, filteredStatusOutput)),
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

    private static string FormatStatusShort(GitResult status)
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
                var process = Process.GetProcessById(processRecord.ProcessId);
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
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

        kernel.RecordTaskProcessCancelled(goalId, taskId, cancelled);
        return cancelled;
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
        var normalizedPath = Path.GetFullPath(workingDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
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
            var referencesPath = cmdLine is not null &&
                (cmdLine.Contains(normalizedPath, StringComparison.OrdinalIgnoreCase) ||
                 cmdLine.Contains(workingDirectory, StringComparison.OrdinalIgnoreCase));

            if (referencesPath || cmdLine is null)
            {
                result.Add((pid, name, cmdLine));
            }
        }

        return result;
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
        if (!IsCodexDispatch(task.LastDispatch) || File.Exists(processRecord.ExitCodePath))
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
                GetInt64(root, "stderrBytes"));
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

    private static bool IsCodexDispatch(TaskDispatchRecord? dispatch)
    {
        if (dispatch is null)
        {
            return false;
        }

        return dispatch.WorkerName.Contains("codex", StringComparison.OrdinalIgnoreCase) ||
            dispatch.Command.TrimStart().StartsWith("codex ", StringComparison.OrdinalIgnoreCase) ||
            dispatch.Command.TrimStart().StartsWith("& codex ", StringComparison.OrdinalIgnoreCase);
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

    private static void TryKillProcess(int processId)
    {
        try
        {
            var process = Process.GetProcessById(processId);
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
        }
        catch (ArgumentException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static GitResult RunGit(string workingDirectory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            return new GitResult(1, string.Empty);
        }

        var output = process.StandardOutput.ReadToEnd();
        if (!process.WaitForExit((int)GitTimeout.TotalMilliseconds))
        {
            process.Kill(entireProcessTree: true);
            return new GitResult(1, string.Empty);
        }

        return new GitResult(process.ExitCode, output);
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


    private sealed record GitResult(int ExitCode, string Output);

    private sealed record DispatchHeartbeat(
        int ProcessId,
        int? ChildProcessId,
        string State,
        DateTimeOffset LastObservedAt,
        DateTimeOffset LastProgressAt,
        long StandardOutputBytes,
        long StandardErrorBytes)
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
