using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal interface IConductorSupervisorProcessHost
{
    Task<ConductorSupervisorProcessResult> RunAsync(
        ConductorSupervisorProcessRequest request,
        CancellationToken cancellationToken);
}

internal sealed record ConductorSupervisorProcessRequest(
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    string ExitArtifactPath,
    string StdoutPath,
    string StderrPath);

internal sealed record ConductorSupervisorProcessResult(
    int ExitCode,
    int ProcessId,
    string StdoutPath = "",
    string StderrPath = "");

internal sealed record ConductorContinuityExitArtifact(
    string StopReason,
    int Ticks,
    int Done,
    bool RestartRequested)
{
    public static void Write(string path, ConductorContinuityExitArtifact artifact)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        var temporaryPath = path + $".{Guid.NewGuid():N}.tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(artifact));
        File.Move(temporaryPath, path, overwrite: true);
    }

    public static ConductorContinuityExitArtifact? TryRead(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<ConductorContinuityExitArtifact>(File.ReadAllText(path))
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}

internal sealed class ConductorContinuitySupervisor(
    IConductorSupervisorProcessHost processHost,
    IRunEventStore eventStore,
    TimeProvider? timeProvider = null,
    Func<TimeSpan, CancellationToken, Task>? delay = null,
    int maxUnexpectedRestarts = 3,
    TimeSpan? restartWindow = null,
    int maxRenewalsWithoutProgress = ConductorLoopHandoff.DefaultMaxRenewalsWithoutLanding)
{
    public const string ChildFlag = "--continuity-child";
    public const string ExitArtifactFlag = "--continuity-exit-artifact";
    internal const string StdoutLogPathEnvironmentVariable = "MCG_ORCHESTRATOR_STDOUT_LOG_PATH";
    internal const string StderrLogPathEnvironmentVariable = "MCG_ORCHESTRATOR_STDERR_LOG_PATH";
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;
    private readonly TimeSpan _restartWindow = restartWindow ?? TimeSpan.FromMinutes(10);

    public static bool ShouldSupervise(
        IReadOnlyList<string> args,
        bool authorityTransferRequested) =>
        CliPersistentStateRunner.IsConductLoop(args) &&
        args.Any(arg => arg.Equals("--loop", StringComparison.OrdinalIgnoreCase)) &&
        !args.Any(arg => arg.Equals(ChildFlag, StringComparison.OrdinalIgnoreCase)) &&
        !authorityTransferRequested;

    public async Task<int> RunAsync(
        IReadOnlyList<string> args,
        string workingDirectory,
        string artifactDirectory,
        string projectName,
        string tenantName,
        string? logDirectory = null,
        CancellationToken cancellationToken = default)
    {
        var unexpectedStarts = new Queue<DateTimeOffset>();
        var renewalsWithoutProgress = 0;
        var attempt = 0;

        while (true)
        {
            attempt++;
            var artifactPath = Path.Combine(artifactDirectory, $"conduct-{Guid.NewGuid():N}.json");
            var outputDirectory = Path.GetFullPath(logDirectory ?? Path.Combine(artifactDirectory, "logs"));
            Directory.CreateDirectory(outputDirectory);
            var stamp = _timeProvider.GetUtcNow().ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture);
            var outputPrefix = $"conduct-supervisor-{stamp}-{attempt:D3}-{Guid.NewGuid():N}";
            var stdoutPath = Path.Combine(outputDirectory, $"{outputPrefix}.out.log");
            var stderrPath = Path.Combine(outputDirectory, $"{outputPrefix}.err.log");
            PrepareOutputFile(stdoutPath);
            PrepareOutputFile(stderrPath);
            var childArgs = args
                .Where(arg => !arg.Equals(ChildFlag, StringComparison.OrdinalIgnoreCase))
                .Concat([
                    $"--project={projectName}",
                    $"--tenant={tenantName}",
                    ChildFlag,
                    ExitArtifactFlag,
                    artifactPath
                ])
                .ToArray();
            ConductorSupervisorProcessResult result;
            string? launchFailure = null;
            try
            {
                result = await processHost.RunAsync(
                    new ConductorSupervisorProcessRequest(
                        childArgs,
                        workingDirectory,
                        artifactPath,
                        stdoutPath,
                        stderrPath),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                launchFailure = $"launch={ex.GetType().Name}:{ex.Message}";
                result = new ConductorSupervisorProcessResult(-1, 0, stdoutPath, stderrPath);
            }
            var artifact = ConductorContinuityExitArtifact.TryRead(artifactPath);
            TryDeleteArtifact(artifactPath);

            if (result.ExitCode == 0 && artifact is { RestartRequested: false })
            {
                Record("stopped", "completed", attempt, artifact.StopReason, result.ProcessId, stdoutPath, stderrPath);
                return 0;
            }

            if (result.ExitCode == 0 && artifact is { RestartRequested: true })
            {
                renewalsWithoutProgress = artifact.Done > 0 ? 0 : renewalsWithoutProgress + 1;
                if (renewalsWithoutProgress > maxRenewalsWithoutProgress)
                {
                    var reason = $"renewal-cap count={renewalsWithoutProgress} max={maxRenewalsWithoutProgress}";
                    Record("restart", "escalated", attempt, reason, result.ProcessId, stdoutPath, stderrPath);
                    Console.Error.WriteLine($"[conduct supervisor] Escalated: {reason}.");
                    return 1;
                }

                Record("restart", "planned", attempt, artifact.StopReason, result.ProcessId, stdoutPath, stderrPath);
                continue;
            }

            var now = _timeProvider.GetUtcNow();
            while (unexpectedStarts.Count > 0 && now - unexpectedStarts.Peek() > _restartWindow)
            {
                unexpectedStarts.Dequeue();
            }
            unexpectedStarts.Enqueue(now);
            var failure = launchFailure ?? (artifact is null
                ? $"exit={result.ExitCode} exit-artifact=missing-or-invalid"
                : $"exit={result.ExitCode} stop={artifact.StopReason}");
            if (unexpectedStarts.Count > maxUnexpectedRestarts)
            {
                var reason = $"restart-cap count={unexpectedStarts.Count} max={maxUnexpectedRestarts} windowSeconds={(int)_restartWindow.TotalSeconds} {failure}";
                Record("restart", "escalated", attempt, reason, result.ProcessId, stdoutPath, stderrPath);
                Console.Error.WriteLine($"[conduct supervisor] Escalated: {reason}.");
                return 1;
            }

            var backoff = TimeSpan.FromSeconds(Math.Min(30, 1 << (unexpectedStarts.Count - 1)));
            Record(
                "restart",
                "unexpected",
                attempt,
                $"{failure} backoffSeconds={(int)backoff.TotalSeconds}",
                result.ProcessId,
                stdoutPath,
                stderrPath);
            await _delay(backoff, cancellationToken).ConfigureAwait(false);
        }
    }

    private void Record(
        string operation,
        string status,
        int attempt,
        string reason,
        int processId,
        string stdoutPath,
        string stderrPath)
    {
        var now = _timeProvider.GetUtcNow();
        eventStore.AppendAsync(new RunEventAppend(
                RunEventTypes.ConductorSupervision,
                null,
                operation,
                status,
                $"{reason} stdout={stdoutPath} stderr={stderrPath}",
                JsonSerializer.Serialize(new { attempt, processId, stdoutPath, stderrPath, occurredAt = now }),
                now))
            .GetAwaiter()
            .GetResult();
    }

    private static void PrepareOutputFile(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite);
    }

    private static void TryDeleteArtifact(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

internal sealed class SystemConductorSupervisorProcessHost(
    Func<ConductorSupervisorProcessRequest, ProcessStartInfo>? startInfoFactory = null)
    : IConductorSupervisorProcessHost
{
    public async Task<ConductorSupervisorProcessResult> RunAsync(
        ConductorSupervisorProcessRequest request,
        CancellationToken cancellationToken)
    {
        var startInfo = startInfoFactory?.Invoke(request) ?? BuildDefaultStartInfo(request);
        startInfo.UseShellExecute = false;
        startInfo.CreateNoWindow = true;
        startInfo.WorkingDirectory = request.WorkingDirectory;
        startInfo.RedirectStandardInput = true;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.Environment[OrchestratorWorkspace.RepoRootEnvironmentVariable] = request.WorkingDirectory;
        startInfo.Environment[ConductorContinuitySupervisor.StdoutLogPathEnvironmentVariable] = request.StdoutPath;
        startInfo.Environment[ConductorContinuitySupervisor.StderrLogPathEnvironmentVariable] = request.StderrPath;

        using var stdoutWriter = CreateOutputWriter(request.StdoutPath);
        using var stderrWriter = CreateOutputWriter(request.StderrPath);
        var stdoutGate = new object();
        var stderrGate = new object();
        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data is not null)
            {
                Console.Out.WriteLine(eventArgs.Data);
                lock (stdoutGate)
                {
                    stdoutWriter.WriteLine(eventArgs.Data);
                }
            }
        };
        process.ErrorDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data is not null)
            {
                Console.Error.WriteLine(eventArgs.Data);
                lock (stderrGate)
                {
                    stderrWriter.WriteLine(eventArgs.Data);
                }
            }
        };
        if (!process.Start())
        {
            throw new InvalidOperationException("Failed to start the supervised conductor process.");
        }
        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        process.WaitForExit();
        return new ConductorSupervisorProcessResult(
            process.ExitCode,
            process.Id,
            request.StdoutPath,
            request.StderrPath);
    }

    private static ProcessStartInfo BuildDefaultStartInfo(ConductorSupervisorProcessRequest request)
    {
        var commandLineArgs = Environment.GetCommandLineArgs();
        var executable = Environment.ProcessPath ?? "dotnet";
        var startInfo = new ProcessStartInfo(executable);
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase) &&
            commandLineArgs.Length > 0)
        {
            startInfo.ArgumentList.Add(commandLineArgs[0]);
        }

        foreach (var argument in request.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    private static StreamWriter CreateOutputWriter(string path) =>
        new(
            new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite),
            new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = true
        };
}
