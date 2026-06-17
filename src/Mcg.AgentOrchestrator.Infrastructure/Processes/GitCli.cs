using System.Diagnostics;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class GitCli
{
    // 60 000 ms covers the longest observed per-call timeout in use across the codebase.
    internal const int DefaultTimeoutMilliseconds = 60_000;

    public readonly record struct GitResult(int ExitCode, string Output, string Error)
    {
        public bool Succeeded => ExitCode == 0;
    }

    public static GitResult Run(string workingDirectory, params string[] args) =>
        Run(workingDirectory, DefaultTimeoutMilliseconds, args);

    public static GitResult Run(string workingDirectory, int timeoutMilliseconds, params string[] args)
    {
        try
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
            foreach (var arg in args)
                startInfo.ArgumentList.Add(arg);

            using var process = Process.Start(startInfo);
            if (process is null)
                return new GitResult(1, string.Empty, "failed to start git process");

            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            if (!process.WaitForExit(timeoutMilliseconds))
            {
                process.Kill(entireProcessTree: true);
                return new GitResult(-1, string.Empty, $"git {string.Join(' ', args)} timed out after {timeoutMilliseconds}ms");
            }

            return new GitResult(process.ExitCode, output, error);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            return new GitResult(1, string.Empty, ex.Message);
        }
    }

    // Returns true when the worktree has uncommitted changes, or when the status check
    // fails (treating errors as dirty to prevent accidental acceptance of unverified state).
    public static bool IsWorktreeDirty(string workingDirectory)
    {
        var result = Run(workingDirectory, "status", "--porcelain");
        return result.ExitCode != 0 || !string.IsNullOrWhiteSpace(result.Output);
    }
}
