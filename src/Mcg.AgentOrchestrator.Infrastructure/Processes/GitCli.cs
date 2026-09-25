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
    private const int DrainTimeoutMilliseconds = PipeDrain.DefaultTimeoutMilliseconds;

    // Applied to every invocation so git fails fast and never wedges:
    //  - core.fsmonitor=false / gc.auto=0 / maintenance.auto=false: never spawn a background daemon
    //    that could inherit the output pipe or keep a lock alive after git returns.
    //  - core.longpaths=true: keep revision/path arguments usable from deeply nested fixture worktrees.
    private static readonly string[] HardeningConfig =
    [
        "-c", "core.fsmonitor=false",
        "-c", "core.longpaths=true",
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

    internal readonly record struct AheadBehindInspection(
        bool Succeeded,
        int? Ahead,
        int? Behind,
        string? Error);

    public static GitResult Run(string workingDirectory, params string[] args) =>
        Run(workingDirectory, DefaultTimeoutMilliseconds, args);

    public static GitResult Run(string workingDirectory, int timeoutMilliseconds, params string[] args)
        => RunExecutable("git", workingDirectory, timeoutMilliseconds, args);

    public static GitResult RunWithStandardInput(
        string workingDirectory, int timeoutMilliseconds, string standardInput, params string[] args) =>
        RunExecutableCore("git", workingDirectory, timeoutMilliseconds, standardInput, args);

    internal static GitResult RunExecutable(
        string executable,
        string workingDirectory,
        int timeoutMilliseconds,
        params string[] args)
        => RunExecutableCore(executable, workingDirectory, timeoutMilliseconds, null, args);

    private static GitResult RunExecutableCore(
        string executable,
        string workingDirectory,
        int timeoutMilliseconds,
        string? standardInput,
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
                RedirectStandardInput = standardInput is not null,
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

            // Drain on dedicated threads, never the thread pool. Under parallel test load the pool can be
            // fully blocked; a pool-dependent async read then never starts inside the drain window and
            // the output already sitting in the pipe is discarded as "" next to a real exit code — the
            // "git exits 0 with no output" flake family (proven by GitCliThreadPoolSaturationTests).
            // Dedicated threads also keep WaitForExit's timeout real: a synchronous ReadToEnd on this
            // thread would block on an inherited pipe after git exits.
            var outputDrain = PipeDrain.Start(process.StandardOutput, "git-stdout-drain");
            var errorDrain = PipeDrain.Start(process.StandardError, "git-stderr-drain");

            // Write while both output pipes drain: git may emit diagnostics before consuming stdin.
            var inputWrite = standardInput is null ? null : Task.Run(() =>
            {
                process.StandardInput.Write(standardInput);
                process.StandardInput.Close();
            });

            bool inputCompleted;
            try { inputCompleted = inputWrite?.Wait(timeoutMilliseconds) ?? true; }
            catch (AggregateException ex)
            {
                TryKillTree(process);
                return new GitResult(1, outputDrain.Text, ex.GetBaseException().Message);
            }
            if (!inputCompleted)
            {
                TryKillTree(process);
                return new GitResult(-1, outputDrain.Text, "git stdin write timed out");
            }

            if (!process.WaitForExit(timeoutMilliseconds))
            {
                TryKillTree(process);
                return new GitResult(-1, outputDrain.Text, $"git {string.Join(' ', args)} timed out after {timeoutMilliseconds}ms");
            }

            // git has exited; bound the drain so a detached grandchild holding the pipe can't keep us
            // here. With the hardening config above this should complete immediately.
            var drainDeadline = Environment.TickCount64 + DrainTimeoutMilliseconds;
            var outputDrained = outputDrain.Join(drainDeadline);
            var errorDrained = errorDrain.Join(drainDeadline);
            if (!outputDrained || !errorDrained)
            {
                TryKillTree(process);
                var drainDiagnostic = PipeDrain.DescribeTimeout(
                    "git",
                    DrainTimeoutMilliseconds,
                    outputDrain,
                    errorDrain);
                var timedOutError = errorDrain.Text;
                return new GitResult(
                    process.ExitCode,
                    outputDrain.Text,
                    string.IsNullOrEmpty(timedOutError) ? drainDiagnostic : timedOutError + Environment.NewLine + drainDiagnostic,
                    DrainTimedOut: true);
            }

            return new GitResult(process.ExitCode, outputDrain.Text, errorDrain.Text);
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

    internal static WorktreeStatusInspection InspectWorktreeStatus(string workingDirectory) =>
        InspectWorktreeStatus(workingDirectory, DefaultTimeoutMilliseconds);

    internal static WorktreeStatusInspection InspectWorktreeStatus(string workingDirectory, int timeoutMilliseconds)
    {
        var result = Run(workingDirectory, timeoutMilliseconds, "status", "--porcelain=v1", "--untracked-files=all");
        return result.ExitCode == 0
            ? new WorktreeStatusInspection(true, ParseCommitWorthyStatusPaths(result.Output), null)
            : new WorktreeStatusInspection(false, [], string.IsNullOrWhiteSpace(result.Error) ? "git status failed" : result.Error.Trim());
    }

    internal static AheadBehindInspection InspectAheadBehind(string workingDirectory)
    {
        var result = Run(workingDirectory, "rev-list", "--left-right", "--count", "main...HEAD");
        return InspectAheadBehind(result);
    }

    internal static AheadBehindInspection InspectAheadBehind(GitResult result)
    {
        if (!result.Succeeded || result.DrainTimedOut)
        {
            return new AheadBehindInspection(
                false,
                null,
                null,
                string.IsNullOrWhiteSpace(result.Error) ? "git rev-list failed" : result.Error.Trim());
        }

        var parts = result.Output.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 ||
            !int.TryParse(parts[0], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var behind) ||
            !int.TryParse(parts[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var ahead))
        {
            return new AheadBehindInspection(false, null, null, "git rev-list returned malformed counts");
        }

        return new AheadBehindInspection(true, ahead, behind, null);
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
