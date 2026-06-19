using System.Diagnostics;
using System.Text.Json;

namespace Mcg.AgentOrchestrator.Infrastructure;

/// <summary>
/// Native, cross-platform replacement for the old generated-PowerShell dispatch wrapper. Runs
/// detached as a hidden <c>__dispatch-run</c> subcommand of the App: it sets the build env, launches
/// the worker command through the resolved PowerShell host (<see cref="WorkerShell"/>), streams
/// stdout/stderr to log files, writes a periodic heartbeat, and always records the exit code so the
/// orchestrator can reconcile the dispatch. The detached process outlives the orchestrator CLI, so
/// this logic must be self-contained and never throw without writing the exit file.
/// </summary>
public static class DispatchProcessHost
{
    public const string SubcommandName = "__dispatch-run";
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(15);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public sealed record DispatchRunParameters(
        string Command,
        string WorkingDirectory,
        string StdoutPath,
        string StderrPath,
        string ExitCodePath,
        string? HeartbeatPath,
        bool ShutdownBuildServerOnExit,
        bool DisableSharedCompilation,
        // OS worker sandbox: when SandboxAccount is set, the worker command is launched AS that
        // low-priv account (password read from SandboxCredentialTarget) with Modify granted on the
        // worktree + SandboxGitCommonDir, so the OS confines its writes. Null = run as the operator.
        string? SandboxAccount = null,
        string? SandboxCredentialTarget = null,
        string? SandboxGitCommonDir = null);

    public static string WriteParameters(string path, DispatchRunParameters parameters)
    {
        File.WriteAllText(path, JsonSerializer.Serialize(parameters, JsonOptions));
        return path;
    }

    // When the OS worker sandbox is configured for this dispatch, grant the low-priv account Modify
    // on the per-run writable set and launch the worker AS that account. The confinement boundary is
    // set once at the root; children inherit it via ordinary CreateProcess, so the worker's writes are
    // OS-confined and codex (run danger-full-access) never touches its fragile CreateProcessAsUserW path.
    private static void ApplyWorkerSandbox(ProcessStartInfo startInfo, DispatchRunParameters parameters)
    {
        if (string.IsNullOrWhiteSpace(parameters.SandboxAccount) || !OperatingSystem.IsWindows())
        {
            return;
        }

        var account = parameters.SandboxAccount;
        WorkerSandboxAcl.GrantModify(parameters.WorkingDirectory, account);
        if (!string.IsNullOrWhiteSpace(parameters.SandboxGitCommonDir))
        {
            WorkerSandboxAcl.GrantModify(parameters.SandboxGitCommonDir, account);
        }

        var password = string.IsNullOrWhiteSpace(parameters.SandboxCredentialTarget)
            ? null
            : WindowsWorkerCredential.TryReadPassword(parameters.SandboxCredentialTarget);
        if (string.IsNullOrEmpty(password))
        {
            throw new InvalidOperationException(
                $"OS worker sandbox is enabled (account '{account}') but its credential could not be read from " +
                "Credential Manager. Run scripts/Setup-WorkerSandbox.ps1 and ensure the credential is readable non-elevated.");
        }

        // Launch the worker AS the low-priv account (CreateProcessWithLogonW). WorkingDirectory is the
        // worktree, which the account can reach because GrantModify above granted it. Redirected stdio
        // uses handles this host opened as the operator, so worker logging is unaffected by its ACLs.
        startInfo.UserName = account;
        startInfo.Domain = ".";
        startInfo.PasswordInClearText = password;
    }

    /// <summary>Entry point for the detached <c>__dispatch-run &lt;paramsPath&gt;</c> subcommand.</summary>
    public static int Run(string parametersPath)
    {
        DispatchRunParameters parameters;
        try
        {
            parameters = JsonSerializer.Deserialize<DispatchRunParameters>(File.ReadAllText(parametersPath), JsonOptions)
                ?? throw new InvalidOperationException("Dispatch parameters were empty.");
        }
        catch
        {
            return 1;
        }

        return RunCore(parameters);
    }

    private static int RunCore(DispatchRunParameters parameters)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var lastProgressAt = startedAt;
        long lastStdoutBytes = -1;
        long lastStderrBytes = -1;
        var exitCode = 1;
        Process? worker = null;

        void WriteHeartbeat(string state)
        {
            if (string.IsNullOrWhiteSpace(parameters.HeartbeatPath))
            {
                return;
            }

            var stdoutBytes = FileLength(parameters.StdoutPath);
            var stderrBytes = FileLength(parameters.StderrPath);
            if (stdoutBytes != lastStdoutBytes || stderrBytes != lastStderrBytes)
            {
                lastProgressAt = DateTimeOffset.UtcNow;
                lastStdoutBytes = stdoutBytes;
                lastStderrBytes = stderrBytes;
            }

            var payload = new
            {
                pid = Environment.ProcessId,
                childPid = worker is { HasExited: false } ? worker.Id : (int?)null,
                startedAt = startedAt.ToString("o"),
                lastObservedAt = DateTimeOffset.UtcNow.ToString("o"),
                lastProgressAt = lastProgressAt.ToString("o"),
                state,
                stdoutBytes,
                stderrBytes,
                exitFileExists = File.Exists(parameters.ExitCodePath)
            };

            try
            {
                var tmp = parameters.HeartbeatPath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(payload, JsonOptions));
                File.Move(tmp, parameters.HeartbeatPath, overwrite: true);
            }
            catch
            {
                // Heartbeat is best-effort; never let it fail the dispatch.
            }
        }

        using var heartbeatTimer = new Timer(_ => WriteHeartbeat("running"), null, Timeout.Infinite, Timeout.Infinite);

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = WorkerShell.Executable,
                WorkingDirectory = parameters.WorkingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                // Redirect stdin so we can close it immediately: CLI workers (e.g. claude-cli, a
                // node shim) otherwise inherit the orchestrator's stdin. Under a background/detached
                // launch that handle is an open pipe that never reaches EOF, so the worker blocks
                // indefinitely waiting for stdin (the CLI's 3s "no stdin" skip only applies to a
                // TTY, not an inherited pipe). Closing stdin gives an immediate EOF and prevents
                // the startup hang.
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var argument in WorkerShell.BaseArguments())
            {
                startInfo.ArgumentList.Add(argument);
            }

            startInfo.ArgumentList.Add(parameters.Command);

            if (parameters.DisableSharedCompilation)
            {
                startInfo.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";
                startInfo.Environment["MSBUILDDISABLENODEREUSE"] = "1";
                startInfo.Environment["UseSharedCompilation"] = "false";
            }

            ApplyWorkerSandbox(startInfo, parameters);

            WriteHeartbeat("starting");
            worker = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Failed to start worker process.");

            // Close the worker's stdin immediately so it reads EOF instead of blocking on an
            // inherited/open pipe (see RedirectStandardInput note above).
            try { worker.StandardInput.Close(); } catch { /* worker may have already exited */ }

            // Stream raw bytes to the log files so the heartbeat's byte-growth progress detection works.
            using var stdout = new FileStream(parameters.StdoutPath, FileMode.Create, FileAccess.Write, FileShare.Read);
            using var stderr = new FileStream(parameters.StderrPath, FileMode.Create, FileAccess.Write, FileShare.Read);
            using var drainCts = new CancellationTokenSource();
            var copyOut = worker.StandardOutput.BaseStream.CopyToAsync(stdout, drainCts.Token);
            var copyErr = worker.StandardError.BaseStream.CopyToAsync(stderr, drainCts.Token);

            heartbeatTimer.Change(HeartbeatInterval, HeartbeatInterval);

            worker.WaitForExit();

            // Drain with a bounded timeout. A grandchild that inherits the pipe handle
            // (e.g. claude-cli's node child) keeps CopyToAsync alive indefinitely after
            // the worker exits. Cap the wait and cancel so the finally block always
            // writes the exit-code file.
            const int DrainTimeoutMs = 12_000;
            var drainTasks = new Task[] { copyOut, copyErr };
            if (!Task.WaitAll(drainTasks, DrainTimeoutMs))
            {
                drainCts.Cancel();
                TryKillWorkerTree(worker);
                try { Task.WaitAll(drainTasks, 2000); } catch { }
            }

            try { stdout.Flush(); } catch { }
            try { stderr.Flush(); } catch { }
            exitCode = worker.ExitCode;
        }
        catch
        {
            exitCode = 1;
        }
        finally
        {
            heartbeatTimer.Change(Timeout.Infinite, Timeout.Infinite);
            if (parameters.ShutdownBuildServerOnExit)
            {
                TryShutdownBuildServer(parameters.WorkingDirectory);
            }

            WriteHeartbeat("exiting");
            TryWriteExitCode(parameters.ExitCodePath, exitCode);
        }

        return exitCode;
    }

    private static long FileLength(string path)
    {
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : 0L;
        }
        catch
        {
            return 0L;
        }
    }

    private static void TryKillWorkerTree(Process worker)
    {
        try
        {
            worker.Kill(entireProcessTree: true);
        }
        catch
        {
            // Best-effort: worker may have already exited.
        }
    }

    private static void TryWriteExitCode(string path, int exitCode)
    {
        try
        {
            File.WriteAllText(path, exitCode.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        catch
        {
            // Best-effort; the orchestrator treats a missing exit file as still-running.
        }
    }

    private static void TryShutdownBuildServer(string workingDirectory)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "dotnet",
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            psi.ArgumentList.Add("build-server");
            psi.ArgumentList.Add("shutdown");
            using var process = Process.Start(psi);
            process?.WaitForExit(10000);
        }
        catch
        {
            // Best-effort build-server cleanup.
        }
    }
}
