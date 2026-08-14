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

    internal sealed record CommandResult(
        int ExitCode,
        string Stdout,
        string Stderr = "",
        bool TimedOut = false,
        TimeSpan? Timeout = null,
        TimeSpan? Elapsed = null);

    private readonly Func<string, IReadOnlyList<string>, string, TimeSpan, CancellationToken, Task<CommandResult>> _runner;

    public LocalProcessVerifier() : this(RunCommandAsync) { }

    internal LocalProcessVerifier(Func<string, IReadOnlyList<string>, string, CancellationToken, Task<CommandResult>> runner)
        : this((fileName, args, workingDirectory, _, cancellationToken) =>
            runner(fileName, args, workingDirectory, cancellationToken))
    {
    }

    internal LocalProcessVerifier(Func<string, IReadOnlyList<string>, string, TimeSpan, CancellationToken, Task<CommandResult>> runner)
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
        await _runner(
            "dotnet",
            ["build-server", "shutdown"],
            workingDirectory,
            AcceptanceCheckTimeouts.DefaultTimeout,
            cancellationToken).ConfigureAwait(false);

        var completedAt = DateTimeOffset.UtcNow;
        var preparedCommand = PrepareCommand(command, goalId, taskId);
        var elapsed = Stopwatch.StartNew();
        var commandTimeout = AcceptanceCheckTimeouts.DefaultTimeout;

        using var leaseLock = preparedCommand.BuildEnvironment is null
            ? null
            : DotnetBuildEnvironmentManager.AcquireLeaseExecutionLock(preparedCommand.BuildEnvironment, cancellationToken);

        var result = await RunPreparedCommandAsync(
            preparedCommand,
            workingDirectory,
            commandTimeout,
            cancellationToken).ConfigureAwait(false);

        if (result.ExitCode != 0 && (result.Stdout + result.Stderr).Contains("CS2012", StringComparison.Ordinal))
        {
            // CS2012 is a transient file-lock on obj dlls; a second build-server shutdown
            // clears residual compiler processes before the single allowed retry.
            await _runner(
                "dotnet",
                ["build-server", "shutdown"],
                workingDirectory,
                AcceptanceCheckTimeouts.DefaultTimeout,
                cancellationToken).ConfigureAwait(false);
            result = await RunPreparedCommandAsync(
                preparedCommand,
                workingDirectory,
                commandTimeout,
                cancellationToken).ConfigureAwait(false);
        }

        elapsed.Stop();
        completedAt = DateTimeOffset.UtcNow;

        var standardOutput = BuildBrokerEvidence(preparedCommand, elapsed.Elapsed, result.ExitCode, result.Stdout, result.Stderr) +
            (result.TimedOut ? BuildTimeoutEvidence(result) : string.Empty) +
            result.Stdout;
        return new TaskVerificationRecord(
            preparedCommand.Command,
            workingDirectory,
            result.ExitCode,
            standardOutput,
            result.Stderr,
            completedAt,
            FullStandardOutput: standardOutput,
            FullStandardError: result.Stderr);
    }

    private async Task<CommandResult> RunPreparedCommandAsync(
        PreparedCommand preparedCommand,
        string workingDirectory,
        TimeSpan commandTimeout,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _runner(
                preparedCommand.FileName,
                preparedCommand.Arguments,
                workingDirectory,
                commandTimeout,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new CommandResult(
                -1,
                string.Empty,
                string.Empty,
                TimedOut: true,
                Timeout: commandTimeout,
                Elapsed: commandTimeout);
        }
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

    private static string BuildTimeoutEvidence(CommandResult result) =>
        $"acceptance-check-timeout: local-process-verification elapsed={FormatTimeout(result.Elapsed ?? result.Timeout ?? AcceptanceCheckTimeouts.DefaultTimeout)} budget={FormatTimeout(result.Timeout ?? AcceptanceCheckTimeouts.DefaultTimeout)}{Environment.NewLine}";

    private static string FormatTimeout(TimeSpan timeout) =>
        timeout.TotalSeconds >= 60
            ? $"{timeout.TotalMinutes:0.#}m"
            : $"{timeout.TotalSeconds:0.#}s";

    private static async Task<CommandResult> RunCommandAsync(
        string fileName,
        IReadOnlyList<string> args,
        string workingDirectory,
        TimeSpan commandTimeout,
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

        // This path also produces a verdict (a TaskVerificationRecord), so it needs the same hermeticity
        // as the acceptance gate: a verification result must describe the code, not the launch context.
        GoalAcceptanceVerifier.ConfigureHermeticVerificationEnvironment(startInfo.Environment, workingDirectory);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start process: {fileName}");
        WorkerProcessJobs.RegisterOrThrow(process, $"local-verification:{workingDirectory}");

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(commandTimeout);
        var elapsed = Stopwatch.StartNew();

        try
        {
            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);

            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);

            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            elapsed.Stop();
            return new CommandResult(process.ExitCode, stdout, stderr, Elapsed: elapsed.Elapsed);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try { WorkerProcessJobs.TryKillOrFallback(process.Id); } catch { /* best effort */ }
            try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
            elapsed.Stop();
            return new CommandResult(
                -1,
                string.Empty,
                string.Empty,
                TimedOut: true,
                Timeout: commandTimeout,
                Elapsed: elapsed.Elapsed);
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
