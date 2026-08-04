using System.Diagnostics;
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
    string ExitArtifactPath);

internal sealed record ConductorSupervisorProcessResult(int ExitCode, int ProcessId);

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
        CancellationToken cancellationToken = default)
    {
        var unexpectedStarts = new Queue<DateTimeOffset>();
        var renewalsWithoutProgress = 0;
        var attempt = 0;

        while (true)
        {
            attempt++;
            var artifactPath = Path.Combine(artifactDirectory, $"conduct-{Guid.NewGuid():N}.json");
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
                    new ConductorSupervisorProcessRequest(childArgs, workingDirectory, artifactPath),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                launchFailure = $"launch={ex.GetType().Name}:{ex.Message}";
                result = new ConductorSupervisorProcessResult(-1, 0);
            }
            var artifact = ConductorContinuityExitArtifact.TryRead(artifactPath);
            TryDeleteArtifact(artifactPath);

            if (result.ExitCode == 0 && artifact is { RestartRequested: false })
            {
                Record("stopped", "completed", attempt, artifact.StopReason, result.ProcessId);
                return 0;
            }

            if (result.ExitCode == 0 && artifact is { RestartRequested: true })
            {
                renewalsWithoutProgress = artifact.Done > 0 ? 0 : renewalsWithoutProgress + 1;
                if (renewalsWithoutProgress > maxRenewalsWithoutProgress)
                {
                    var reason = $"renewal-cap count={renewalsWithoutProgress} max={maxRenewalsWithoutProgress}";
                    Record("restart", "escalated", attempt, reason, result.ProcessId);
                    Console.Error.WriteLine($"[conduct supervisor] Escalated: {reason}.");
                    return 1;
                }

                Record("restart", "planned", attempt, artifact.StopReason, result.ProcessId);
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
                Record("restart", "escalated", attempt, reason, result.ProcessId);
                Console.Error.WriteLine($"[conduct supervisor] Escalated: {reason}.");
                return 1;
            }

            var backoff = TimeSpan.FromSeconds(Math.Min(30, 1 << (unexpectedStarts.Count - 1)));
            Record("restart", "unexpected", attempt, $"{failure} backoffSeconds={(int)backoff.TotalSeconds}", result.ProcessId);
            await _delay(backoff, cancellationToken).ConfigureAwait(false);
        }
    }

    private void Record(string operation, string status, int attempt, string reason, int processId)
    {
        var now = _timeProvider.GetUtcNow();
        eventStore.AppendAsync(new RunEventAppend(
                RunEventTypes.ConductorSupervision,
                null,
                operation,
                status,
                reason,
                JsonSerializer.Serialize(new { attempt, processId, occurredAt = now }),
                now))
            .GetAwaiter()
            .GetResult();
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

internal sealed class SystemConductorSupervisorProcessHost : IConductorSupervisorProcessHost
{
    public async Task<ConductorSupervisorProcessResult> RunAsync(
        ConductorSupervisorProcessRequest request,
        CancellationToken cancellationToken)
    {
        var commandLineArgs = Environment.GetCommandLineArgs();
        var executable = Environment.ProcessPath ?? "dotnet";
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = request.WorkingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase) &&
            commandLineArgs.Length > 0)
        {
            startInfo.ArgumentList.Add(commandLineArgs[0]);
        }
        foreach (var argument in request.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        startInfo.Environment[OrchestratorWorkspace.RepoRootEnvironmentVariable] = request.WorkingDirectory;

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data is not null)
            {
                Console.Out.WriteLine(eventArgs.Data);
            }
        };
        process.ErrorDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data is not null)
            {
                Console.Error.WriteLine(eventArgs.Data);
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
        return new ConductorSupervisorProcessResult(process.ExitCode, process.Id);
    }
}
