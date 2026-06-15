using System.Diagnostics;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed class LocalProcessVerifier
{
    internal sealed record PreparedCommand(
        string Command,
        string ArtifactPathEvidence,
        DotnetBuildEnvironment? BuildEnvironment = null);

    internal sealed record CommandResult(int ExitCode, string Stdout, string Stderr = "");

    private static readonly TimeSpan CommandTimeout = TimeSpan.FromMinutes(10);

    private readonly Func<string[], string, CancellationToken, Task<CommandResult>> _runner;

    public LocalProcessVerifier() : this(RunCommandAsync) { }

    internal LocalProcessVerifier(Func<string[], string, CancellationToken, Task<CommandResult>> runner)
    {
        _runner = runner;
    }

    public async Task<TaskVerificationRecord> RunAsync(
        string command,
        string workingDirectory,
        GoalId? goalId = null,
        TaskId? taskId = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            throw new ArgumentException("Value cannot be empty.", nameof(command));
        }

        if (string.IsNullOrWhiteSpace(workingDirectory))
        {
            throw new ArgumentException("Value cannot be empty.", nameof(workingDirectory));
        }

        // Shut down build servers to release file locks before running verification.
        await _runner(["dotnet", "build-server", "shutdown"], workingDirectory, cancellationToken).ConfigureAwait(false);

        var completedAt = DateTimeOffset.UtcNow;
        var preparedCommand = PrepareCommand(command, goalId, taskId);
        var elapsed = Stopwatch.StartNew();

        string[] psArgs = ["powershell.exe", "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", preparedCommand.Command];

        using var leaseLock = preparedCommand.BuildEnvironment is null
            ? null
            : DotnetBuildEnvironmentManager.AcquireLeaseExecutionLock(preparedCommand.BuildEnvironment, cancellationToken);

        var result = await _runner(psArgs, workingDirectory, cancellationToken).ConfigureAwait(false);

        if (result.ExitCode != 0 && (result.Stdout + result.Stderr).Contains("CS2012", StringComparison.Ordinal))
        {
            // CS2012 is a transient file-lock on obj dlls; a second build-server shutdown
            // clears residual compiler processes before the single allowed retry.
            await _runner(["dotnet", "build-server", "shutdown"], workingDirectory, cancellationToken).ConfigureAwait(false);
            result = await _runner(psArgs, workingDirectory, cancellationToken).ConfigureAwait(false);
        }

        elapsed.Stop();
        completedAt = DateTimeOffset.UtcNow;

        return new TaskVerificationRecord(
            preparedCommand.Command,
            workingDirectory,
            result.ExitCode,
            BuildBrokerEvidence(preparedCommand, elapsed.Elapsed, result.ExitCode, result.Stdout, result.Stderr) + result.Stdout,
            result.Stderr,
            completedAt);
    }

    internal static PreparedCommand PrepareCommand(string command, GoalId? goalId = null, TaskId? taskId = null)
    {
        var commandToRun = command.Trim();
        if (goalId is null || !IsSimpleDotnetCommand(commandToRun))
        {
            return new PreparedCommand(commandToRun, string.Empty);
        }

        var attemptName = taskId is null ? "verify" : $"verify-{Prefix(taskId.Value)}";
        var environment = DotnetBuildEnvironmentManager.CreateAttempt(goalId, attemptName);
        commandToRun = $"{commandToRun} {string.Join(' ', environment.Arguments.Select(QuotePowerShellArgument))}";
        return new PreparedCommand(
            commandToRun,
            $"Build environment lease: {environment.LeaseId}{Environment.NewLine}" +
            $"Verification artifacts: {environment.ArtifactsPath}{Environment.NewLine}",
            environment);
    }

    private static bool IsSimpleDotnetCommand(string command)
    {
        if (!command.Equals("dotnet", StringComparison.OrdinalIgnoreCase) &&
            !command.StartsWith("dotnet ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !command.Contains(';') &&
            !command.Contains("&&", StringComparison.Ordinal) &&
            !command.Contains("||", StringComparison.Ordinal) &&
            !command.Contains('\n') &&
            !command.Contains('\r');
    }

    private static string QuotePowerShellArgument(string argument)
    {
        return $"'{argument.Replace("'", "''", StringComparison.Ordinal)}'";
    }

    private static string Prefix(string value)
    {
        return value.Length <= 8 ? value : value[..8];
    }

    private static string BuildBrokerEvidence(
        PreparedCommand preparedCommand,
        TimeSpan duration,
        int exitCode,
        string stdout,
        string stderr)
    {
        if (preparedCommand.BuildEnvironment is null)
        {
            return preparedCommand.ArtifactPathEvidence;
        }

        var resultSummary = ExtractResultSummary(stdout, stderr);
        return preparedCommand.ArtifactPathEvidence +
            "Verification broker: local-process-verifier" + Environment.NewLine +
            $"Broker duration ms: {(long)duration.TotalMilliseconds}" + Environment.NewLine +
            $"Broker exit code: {exitCode}" + Environment.NewLine +
            $"Broker execution lock: {preparedCommand.BuildEnvironment.ExecutionLockPath}" + Environment.NewLine +
            (string.IsNullOrWhiteSpace(resultSummary)
                ? string.Empty
                : $"Broker result summary: {resultSummary}{Environment.NewLine}");
    }

    private static string? ExtractResultSummary(string stdout, string stderr)
    {
        var lines = $"{stdout}{Environment.NewLine}{stderr}"
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.LastOrDefault(line =>
            line.Contains("Failed:", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Passed:", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Total:", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<CommandResult> RunCommandAsync(
        string[] args,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = args[0],
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory
        };

        for (var i = 1; i < args.Length; i++)
        {
            startInfo.ArgumentList.Add(args[i]);
        }

        startInfo.EnvironmentVariables["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";
        startInfo.EnvironmentVariables["MSBUILDDISABLENODEREUSE"] = "1";
        startInfo.EnvironmentVariables["UseSharedCompilation"] = "false";
        startInfo.EnvironmentVariables["MCG_ORCHESTRATOR_REPOSITORY_ROOT"] = workingDirectory;

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start process: {args[0]}");

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(CommandTimeout);

        var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);

        await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        return new CommandResult(process.ExitCode, stdout, stderr);
    }
}
