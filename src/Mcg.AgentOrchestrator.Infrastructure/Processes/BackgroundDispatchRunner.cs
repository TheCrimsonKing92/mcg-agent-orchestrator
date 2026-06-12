using System.Diagnostics;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed class BackgroundDispatchRunner
{
    private static readonly TimeSpan DefaultPostOutputIdleTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(30);
    private readonly IClock _clock;
    private readonly TimeSpan _postOutputIdleTimeout;
    private readonly Func<int, bool> _isStillRunning;

    public BackgroundDispatchRunner(
        IClock? clock = null,
        TimeSpan? postOutputIdleTimeout = null,
        Func<int, bool>? isStillRunning = null)
    {
        _clock = clock ?? new SystemClock();
        _postOutputIdleTimeout = postOutputIdleTimeout ?? DefaultPostOutputIdleTimeout;
        _isStillRunning = isStillRunning ?? IsStillRunning;
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

        Directory.CreateDirectory(logRoot);
        var prefix = $"{goalId.Value[..8]}-{taskId.Value[..8]}-{_clock.UtcNow:yyyyMMddHHmmss}";
        var stdoutPath = Path.Combine(logRoot, $"{prefix}.out.log");
        var stderrPath = Path.Combine(logRoot, $"{prefix}.err.log");
        var exitCodePath = Path.Combine(logRoot, $"{prefix}.exit.txt");

        var isLocalDispatch = IsLocalDispatch(dispatch);
        var wrapper = BuildWrapper(
            dispatch.Command,
            stdoutPath,
            stderrPath,
            exitCodePath,
            shutdownBuildServerOnExit: !isLocalDispatch,
            disableSharedCompilation: !isLocalDispatch);
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = dispatch.WorkingDirectory
        };

        if (OperatingSystem.IsWindows())
        {
            startInfo.CreateNewProcessGroup = true;
        }

        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(wrapper);

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

    public TaskProcessRecord RefreshLatestProcess(AgentOrchestratorKernel kernel, GoalId goalId, TaskId taskId)
    {
        var task = kernel.GetTask(goalId, taskId);
        var processRecord = task.LastProcess
            ?? throw new InvalidOperationException($"Task '{taskId}' has no background process to refresh.");

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
                    wt.IsClean && wt.HasCommitAfterDispatch)
                {
                    var reapNote =
                        "Background dispatch wrapper appears hung after codex final output; no exit file was written. " +
                        $"Wrapper process reaped; task completed based on file-change evidence " +
                        $"(branch={wt.Branch}; head={wt.Head}; commits_after_dispatch={wt.CommitsAfterDispatch}).";
                    TryWriteExitCode(processRecord.ExitCodePath, 0);
                    return RecordCompletedProcess(kernel, goalId, taskId, processRecord, 0, reapNote);
                }

                TryWriteExitCode(processRecord.ExitCodePath, 1);
                return RecordCompletedProcess(kernel, goalId, taskId, processRecord, 1, diagnostic);
            }

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
            else if (!HasExplicitNoChangeRationale(standardOutput, standardError) && !worktreeEvidence.HasCommitAfterDispatch)
            {
                exitCode = 1;
                standardErrorDiagnostic = AppendDiagnostic(
                    standardErrorDiagnostic ?? string.Empty,
                    "Developer/Tester dispatch exited 0 but did not produce required file-change evidence. " +
                    $"branch={worktreeEvidence.Branch}; head={worktreeEvidence.Head}; worktree={worktreeEvidence.WorktreeStatus}; " +
                    $"commits_after_dispatch={worktreeEvidence.CommitsAfterDispatch}.");
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

    private static bool HasExplicitNoChangeRationale(string standardOutput, string standardError)
    {
        var output = $"{standardOutput}\n{standardError}";
        return output.Contains("NO_CHANGE:", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("No-change rationale:", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("No changes needed:", StringComparison.OrdinalIgnoreCase);
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
        var commitsAfterDispatch = 0;
        if (dispatch.ExitCode == 0)
        {
            commitsAfterDispatch = dispatch.Output
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Length;
        }

        evidence = new GoalWorktreeDispatchEvidence(
            branch.Output.Trim(),
            head.ExitCode == 0 ? head.Output.Trim() : "unknown",
            status.ExitCode == 0 && string.IsNullOrWhiteSpace(status.Output),
            status.ExitCode == 0 && string.IsNullOrWhiteSpace(status.Output) ? "clean" : "dirty",
            FormatStatusShort(status),
            commitsAfterDispatch);
        return true;
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

        if (int.TryParse(File.ReadAllText(path).Trim(), out exitCode))
        {
            return true;
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

    internal static string BuildWrapper(
        string command,
        string stdoutPath,
        string stderrPath,
        string exitCodePath,
        bool shutdownBuildServerOnExit = true,
        bool disableSharedCompilation = true)
    {
        var cleanup = shutdownBuildServerOnExit
            ? "try { & dotnet build-server shutdown *> $null } catch { }; "
            : string.Empty;

        var envSetup = disableSharedCompilation
            ? "$env:DOTNET_CLI_USE_MSBUILD_SERVER = '0'; $env:MSBUILDDISABLENODEREUSE = '1'; $env:UseSharedCompilation = 'false'; "
            : string.Empty;

        return
            "$code = 1; " +
            "try { " +
            envSetup +
            $"& {{ {command} }} 1> {Quote(stdoutPath)} 2> {Quote(stderrPath)}; " +
            "$code = if ($global:LASTEXITCODE -ne $null) { $global:LASTEXITCODE } elseif ($?) { 0 } else { 1 }; " +
            "} finally { " +
            cleanup +
            $"[IO.File]::WriteAllText({Quote(exitCodePath)}, [string]$code) " +
            "}; exit $code";
    }

    private static bool IsLocalDispatch(TaskDispatchRecord dispatch)
    {
        return dispatch.WorkerName.Equals("local", StringComparison.OrdinalIgnoreCase);
    }

    private static string Quote(string value) => "'" + value.Replace("'", "''") + "'";

    private sealed record GitResult(int ExitCode, string Output);

    private sealed record GoalWorktreeDispatchEvidence(
        string Branch,
        string Head,
        bool IsClean,
        string WorktreeStatus,
        string StatusShort,
        int CommitsAfterDispatch)
    {
        public bool HasCommitAfterDispatch => CommitsAfterDispatch > 0;

        public static GoalWorktreeDispatchEvidence Unknown { get; } = new("unknown", "unknown", false, "unknown", "unavailable", 0);
    }
}
