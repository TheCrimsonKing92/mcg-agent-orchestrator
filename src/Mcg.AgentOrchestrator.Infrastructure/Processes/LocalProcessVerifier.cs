using System.Diagnostics;
using System.Text;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed class LocalProcessVerifier
{
    internal sealed record PreparedCommand(
        string Command,
        string ArtifactPathEvidence,
        string FileName,
        IReadOnlyList<string> Arguments,
        DotnetBuildEnvironment? BuildEnvironment = null);

    internal sealed record CommandResult(int ExitCode, string Stdout, string Stderr = "");

    private static readonly TimeSpan CommandTimeout = TimeSpan.FromMinutes(10);

    private readonly Func<string, IReadOnlyList<string>, string, CancellationToken, Task<CommandResult>> _runner;

    public LocalProcessVerifier() : this(RunCommandAsync) { }

    internal LocalProcessVerifier(Func<string, IReadOnlyList<string>, string, CancellationToken, Task<CommandResult>> runner)
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
        await _runner("dotnet", ["build-server", "shutdown"], workingDirectory, cancellationToken).ConfigureAwait(false);

        var completedAt = DateTimeOffset.UtcNow;
        var preparedCommand = PrepareCommand(command, goalId, taskId);
        var elapsed = Stopwatch.StartNew();

        using var leaseLock = preparedCommand.BuildEnvironment is null
            ? null
            : DotnetBuildEnvironmentManager.AcquireLeaseExecutionLock(preparedCommand.BuildEnvironment, cancellationToken);

        var result = await _runner(preparedCommand.FileName, preparedCommand.Arguments, workingDirectory, cancellationToken).ConfigureAwait(false);

        if (result.ExitCode != 0 && (result.Stdout + result.Stderr).Contains("CS2012", StringComparison.Ordinal))
        {
            // CS2012 is a transient file-lock on obj dlls; a second build-server shutdown
            // clears residual compiler processes before the single allowed retry.
            await _runner("dotnet", ["build-server", "shutdown"], workingDirectory, cancellationToken).ConfigureAwait(false);
            result = await _runner(preparedCommand.FileName, preparedCommand.Arguments, workingDirectory, cancellationToken).ConfigureAwait(false);
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
        var executionArguments = TokenizeSimpleCommand(commandToRun);
        var fileName = executionArguments.FirstOrDefault() ?? string.Empty;
        var arguments = executionArguments.Skip(1).ToArray();
        if (goalId is null || !IsSimpleDotnetCommand(commandToRun))
        {
            return new PreparedCommand(commandToRun, string.Empty, fileName, arguments);
        }

        var attemptName = taskId is null ? "verify" : $"verify-{Prefix(taskId.Value)}";
        var environment = DotnetBuildEnvironmentManager.CreateAttempt(goalId, attemptName);
        arguments = [.. arguments, .. environment.Arguments];
        return new PreparedCommand(
            JoinDisplayCommand([fileName, .. arguments]),
            $"Build environment lease: {environment.LeaseId}{Environment.NewLine}" +
            $"Verification artifacts: {environment.ArtifactsPath}{Environment.NewLine}",
            fileName,
            arguments,
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

    private static string[] TokenizeSimpleCommand(string command)
    {
        var tokens = new List<string>();
        var token = new StringBuilder();
        char? quote = null;

        foreach (var ch in command)
        {
            if (quote is { } quoteChar)
            {
                if (ch == quoteChar)
                {
                    quote = null;
                    continue;
                }

                token.Append(ch);
                continue;
            }

            if (ch is '\'' or '"')
            {
                quote = ch;
                continue;
            }

            if (char.IsWhiteSpace(ch))
            {
                if (token.Length > 0)
                {
                    tokens.Add(token.ToString());
                    token.Clear();
                }

                continue;
            }

            token.Append(ch);
        }

        if (token.Length > 0)
        {
            tokens.Add(token.ToString());
        }

        return tokens.ToArray();
    }

    private static string JoinDisplayCommand(IReadOnlyList<string> arguments)
    {
        return string.Join(' ', arguments);
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
        string fileName,
        IReadOnlyList<string> args,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory
        };

        for (var i = 0; i < args.Count; i++)
        {
            startInfo.ArgumentList.Add(args[i]);
        }

        startInfo.EnvironmentVariables["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";
        startInfo.EnvironmentVariables["MSBUILDDISABLENODEREUSE"] = "1";
        startInfo.EnvironmentVariables["UseSharedCompilation"] = "false";
        startInfo.EnvironmentVariables["MCG_ORCHESTRATOR_REPOSITORY_ROOT"] = workingDirectory;

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start process: {fileName}");
        WorkerProcessJobs.TryRegister(process, $"local-verification:{workingDirectory}");

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(CommandTimeout);

        try
        {
            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);

            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);

            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            return new CommandResult(process.ExitCode, stdout, stderr);
        }
        catch (OperationCanceledException)
        {
            try { WorkerProcessJobs.TryKillOrFallback(process.Id); } catch { /* best effort */ }
            try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
            throw;
        }
        finally
        {
            WorkerProcessJobs.Release(process.Id);
        }
    }
}
