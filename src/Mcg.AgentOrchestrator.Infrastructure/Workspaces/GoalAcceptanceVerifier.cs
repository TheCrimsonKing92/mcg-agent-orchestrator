using System.Diagnostics;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed record AcceptanceVerificationResult(
    bool Passed,
    bool Skipped,
    int? ExitCode,
    string? OutputTail,
    bool Retried = false);

public sealed class GoalAcceptanceVerifier
{
    internal sealed record CommandResult(int ExitCode, string Output);

    private static readonly TimeSpan CommandTimeout = TimeSpan.FromMinutes(10);

    private readonly Func<string[], string, CancellationToken, Task<CommandResult>> _runner;

    public GoalAcceptanceVerifier() : this(RunProcessAsync) { }

    internal GoalAcceptanceVerifier(Func<string[], string, CancellationToken, Task<CommandResult>> runner)
    {
        _runner = runner;
    }

    public async Task<AcceptanceVerificationResult> RunAsync(
        string worktreePath,
        CancellationToken cancellationToken = default)
    {
        // Shut down build servers to release file locks before running tests.
        await _runner(["dotnet", "build-server", "shutdown"], worktreePath, cancellationToken).ConfigureAwait(false);

        var result = await _runner(["dotnet", "test"], worktreePath, cancellationToken).ConfigureAwait(false);

        var retried = false;
        if (result.ExitCode != 0 && result.Output.Contains("CS2012", StringComparison.Ordinal))
        {
            // CS2012 is a transient file-lock on obj dlls; a second build-server shutdown
            // clears residual compiler processes before the single allowed retry.
            await _runner(["dotnet", "build-server", "shutdown"], worktreePath, cancellationToken).ConfigureAwait(false);
            result = await _runner(["dotnet", "test"], worktreePath, cancellationToken).ConfigureAwait(false);
            retried = true;
        }

        var outputTail = result.ExitCode != 0
            ? TailOutput(result.Output)
            : null;

        return new AcceptanceVerificationResult(
            Passed: result.ExitCode == 0,
            Skipped: false,
            ExitCode: result.ExitCode,
            OutputTail: outputTail,
            Retried: retried);
    }

    private static string? TailOutput(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        const int maxChars = 2000;
        const int maxLines = 40;

        var trimmed = output.Trim();
        var lines = trimmed.Split('\n');
        var tail = lines.Length <= maxLines
            ? trimmed
            : string.Join('\n', lines[^maxLines..]);

        return tail.Length <= maxChars ? tail : tail[^maxChars..];
    }

    private static async Task<CommandResult> RunProcessAsync(
        string[] arguments,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = arguments[0],
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory
        };

        // Match the dispatch wrapper: without these, MSBuild worker nodes and
        // VBCSCompiler outlive the root test process holding the redirected
        // pipes and worktree obj files, so the output reads below hang until
        // the command timeout cancels them.
        startInfo.EnvironmentVariables["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";
        startInfo.EnvironmentVariables["MSBUILDDISABLENODEREUSE"] = "1";
        startInfo.EnvironmentVariables["UseSharedCompilation"] = "false";

        for (var i = 1; i < arguments.Length; i++)
        {
            startInfo.ArgumentList.Add(arguments[i]);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start process: {arguments[0]}");

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(CommandTimeout);

        var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);

        await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        return new CommandResult(process.ExitCode, (stdout + stderr).Trim());
    }
}
