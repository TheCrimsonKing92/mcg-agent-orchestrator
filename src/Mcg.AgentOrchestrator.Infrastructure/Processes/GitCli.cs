using System.Diagnostics;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class GitCli
{
    // 60 000 ms covers the longest observed per-call timeout in use across the codebase.
    internal const int DefaultTimeoutMilliseconds = 60_000;

    // After git itself exits, bound how long we wait for stdout/stderr to drain. A git background
    // helper (fsmonitor/maintenance/gc) can inherit the output pipe and hold it open after git
    // exits; a synchronous ReadToEnd would then block forever and DEFEAT the timeout below (observed:
    // an inherited-pipe git wedged the conductor for 34 minutes while holding index.lock).
    private const int DrainTimeoutMilliseconds = 5_000;

    // Applied to every invocation so git fails fast and never wedges:
    //  - core.fsmonitor=false / gc.auto=0 / maintenance.auto=false: never spawn a background daemon
    //    that could inherit the output pipe or keep a lock alive after git returns.
    private static readonly string[] HardeningConfig =
    [
        "-c", "core.fsmonitor=false",
        "-c", "gc.auto=0",
        "-c", "maintenance.auto=false",
    ];

    public readonly record struct GitResult(
        int ExitCode,
        string Output,
        string Error,
        bool DrainTimedOut = false,
        bool ProcessStarted = true)
    {
        public bool Succeeded => ExitCode == 0;
    }

    internal readonly record struct WorktreeStatusInspection(
        bool Succeeded,
        IReadOnlyList<string> CommitWorthyPaths,
        string? Error)
    {
        public bool IsDirty => CommitWorthyPaths.Count > 0;
    }

    public static GitResult Run(string workingDirectory, params string[] args) =>
        Run(workingDirectory, DefaultTimeoutMilliseconds, args);

    public static GitResult Run(string workingDirectory, int timeoutMilliseconds, params string[] args)
        => RunExecutable("git", workingDirectory, timeoutMilliseconds, args);

    internal static GitResult RunExecutable(
        string executable,
        string workingDirectory,
        int timeoutMilliseconds,
        params string[] args)
    {
        var processStarted = false;
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = workingDirectory
            };
            foreach (var config in HardeningConfig)
                startInfo.ArgumentList.Add(config);
            foreach (var arg in args)
                startInfo.ArgumentList.Add(arg);

            // Read-only commands (status/diff/log) skip the index.lock, so recovery inspection can
            // never take or block on it; write commands take their required locks regardless.
            startInfo.Environment["GIT_OPTIONAL_LOCKS"] = "0";

            using var process = Process.Start(startInfo);
            if (process is null)
                return new GitResult(1, string.Empty, "failed to start git process", ProcessStarted: false);
            processStarted = true;

            // Drain asynchronously so WaitForExit's timeout is real: a synchronous ReadToEnd would
            // block on an inherited pipe even after git exits, and the timeout would never fire.
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(timeoutMilliseconds))
            {
                TryKillTree(process);
                return new GitResult(-1, string.Empty, $"git {string.Join(' ', args)} timed out after {timeoutMilliseconds}ms");
            }

            // git has exited; bound the drain so a detached grandchild holding the pipe can't keep us
            // here. With the hardening config above this should complete immediately.
            if (!Task.WaitAll([outputTask, errorTask], DrainTimeoutMilliseconds))
            {
                TryKillTree(process);
                var timedOutOutput = outputTask.Status == TaskStatus.RanToCompletion ? outputTask.Result : string.Empty;
                var timedOutError = errorTask.Status == TaskStatus.RanToCompletion ? errorTask.Result : string.Empty;
                return new GitResult(process.ExitCode, timedOutOutput, timedOutError, DrainTimedOut: true);
            }

            var output = outputTask.Status == TaskStatus.RanToCompletion ? outputTask.Result : string.Empty;
            var error = errorTask.Status == TaskStatus.RanToCompletion ? errorTask.Result : string.Empty;
            return new GitResult(process.ExitCode, output, error);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            return new GitResult(1, string.Empty, ex.Message, ProcessStarted: processStarted);
        }
    }

    private static void TryKillTree(Process process)
    {
        try { process.Kill(entireProcessTree: true); }
        catch { /* best-effort: process may have already exited */ }
    }

    // Returns true when the worktree has commit-worthy uncommitted changes, or when the
    // status check fails (treating errors as dirty to prevent accidental acceptance of
    // unverified state).
    public static bool IsWorktreeDirty(string workingDirectory)
    {
        var inspection = InspectWorktreeStatus(workingDirectory);
        return !inspection.Succeeded || inspection.IsDirty;
    }

    internal static WorktreeStatusInspection InspectWorktreeStatus(string workingDirectory)
    {
        var result = Run(workingDirectory, "status", "--porcelain=v1", "--untracked-files=all");
        return result.ExitCode == 0
            ? new WorktreeStatusInspection(true, ParseCommitWorthyStatusPaths(result.Output), null)
            : new WorktreeStatusInspection(false, [], string.IsNullOrWhiteSpace(result.Error) ? "git status failed" : result.Error.Trim());
    }

    internal static string FilterCommitWorthyStatus(string statusOutput)
    {
        if (string.IsNullOrWhiteSpace(statusOutput))
        {
            return statusOutput;
        }

        var lines = statusOutput
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Where(IsCommitWorthyStatusLine);
        return string.Join("\n", lines);
    }

    internal static string[] ParseCommitWorthyStatusPaths(string statusOutput)
    {
        if (string.IsNullOrWhiteSpace(statusOutput))
        {
            return [];
        }

        return statusOutput
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .SelectMany(ParseStatusLinePaths)
            .Where(path => !IsOrchestratorInternalArtifactPath(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    internal static bool IsCommitWorthyStatusLine(string line)
    {
        var paths = ParseStatusLinePaths(line);
        return paths.Length > 0 && paths.Any(path => !IsOrchestratorInternalArtifactPath(path));
    }

    internal static string[] ParseStatusLinePaths(string line)
    {
        if (line.Length < 4)
        {
            return [];
        }

        var path = line[3..].Trim();
        if (string.IsNullOrWhiteSpace(path))
        {
            return [];
        }

        var renameSeparator = path.IndexOf(" -> ", StringComparison.Ordinal);
        if (renameSeparator >= 0)
        {
            var source = path[..renameSeparator].Trim();
            var destination = path[(renameSeparator + 4)..].Trim();
            return [source, destination];
        }

        return [path];
    }

    internal static bool IsOrchestratorInternalArtifactPath(string path)
    {
        var normalized = path.Replace('\\', '/').TrimStart('/');
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        return normalized.Equals(".qwen/settings.json", StringComparison.OrdinalIgnoreCase) ||
            IsPowerShellModuleAnalysisCachePath(normalized) ||
            normalized.Equals(".orchestrator-handoff.md", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals(WorkerSandboxPreparer.MarkerFileName, StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals(WorkerSandboxPreparer.ReceiptFileName, StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("WORKER_RESULT.md", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("WORKER_RESULT.txt", StringComparison.OrdinalIgnoreCase) ||
            IsIsolationLeaseArtifactPath(normalized) ||
            normalized.StartsWith(".orchestrator-context/", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("bin/", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("/bin/", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("obj/", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("/obj/", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith(".scratch/", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith(".orchestrator-prototype/", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("TestResults/", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("/TestResults/", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("playwright-report/", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("/playwright-report/", StringComparison.OrdinalIgnoreCase) ||
            normalized.EndsWith(".log", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPowerShellModuleAnalysisCachePath(string normalizedPath)
    {
        if (normalizedPath.Equals(
            "Microsoft/Windows/PowerShell/ModuleAnalysisCache",
            StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        const string powerShell7Prefix = "Microsoft/PowerShell/ModuleAnalysisCache-";
        if (!normalizedPath.StartsWith(powerShell7Prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var cacheKey = normalizedPath[powerShell7Prefix.Length..];
        return cacheKey.Length == 8 && cacheKey.All(Uri.IsHexDigit);
    }

    private static bool IsIsolationLeaseArtifactPath(string normalizedPath)
    {
        return
            (normalizedPath.StartsWith("i/goals/", StringComparison.OrdinalIgnoreCase) &&
             (normalizedPath.EndsWith("/lease/lease.json", StringComparison.OrdinalIgnoreCase) ||
              normalizedPath.EndsWith("/lease/lease.lock", StringComparison.OrdinalIgnoreCase))) ||
            (normalizedPath.StartsWith("i/slots/", StringComparison.OrdinalIgnoreCase) &&
             normalizedPath.EndsWith("/lease.execution.lock", StringComparison.OrdinalIgnoreCase));
    }
}
