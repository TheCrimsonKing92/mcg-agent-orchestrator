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
    string StderrPath,
    IReadOnlyList<string>? CommandPrefix = null,
    Action<string>? OnStandardOutputLine = null);

internal sealed record ConductorSupervisorProcessResult(
    int ExitCode,
    int ProcessId,
    string StdoutPath = "",
    string StderrPath = "",
    bool TerminationConfirmed = true);

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

internal sealed partial class ConductorContinuitySupervisor(
    IConductorSupervisorProcessHost processHost,
    IRunEventStore eventStore,
    TimeProvider? timeProvider = null,
    Func<TimeSpan, CancellationToken, Task>? delay = null,
    int maxUnexpectedRestarts = 3,
    TimeSpan? restartWindow = null,
    int maxRenewalsWithoutProgress = ConductorLoopHandoff.DefaultMaxRenewalsWithoutLanding,
    Func<CancellationToken, ConductorPreparedSuccessor>? stageSuccessor = null,
    Action<string, string?, string>? appendConductEvent = null,
    TimeSpan? readinessTimeout = null,
    int maxConsecutiveStagingFailures = 2,
    string? dotnetPath = null,
    int? activationHealthyTicks = null,
    Func<TimeSpan, CancellationToken, Task>? activationDelay = null,
    TimeSpan? activationStallTimeout = null,
    Action<string>? raiseActivationAttention = null)
{
    public const string ChildFlag = "--continuity-child";
    public const string ExitArtifactFlag = "--continuity-exit-artifact";
    internal const string LoopReadyLinePrefix = "LOOP_READY ";
    internal const string StdoutLogPathEnvironmentVariable = "MCG_ORCHESTRATOR_STDOUT_LOG_PATH";
    internal const string StderrLogPathEnvironmentVariable = "MCG_ORCHESTRATOR_STDERR_LOG_PATH";
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;
    private readonly TimeSpan _restartWindow = restartWindow ?? TimeSpan.FromMinutes(10);
    private readonly TimeSpan _readinessTimeout = readinessTimeout ?? TimeSpan.FromMinutes(2);
    private readonly int _activationHealthyTicks = Math.Max(1, activationHealthyTicks ?? ResolveActivationHealthyTicks(
        Environment.GetEnvironmentVariable(ActivationHealthyTicksEnvironmentVariable)));
    private readonly Func<TimeSpan, CancellationToken, Task> _activationDelay = activationDelay ??
        ((duration, token) => Task.Delay(duration, timeProvider ?? TimeProvider.System, token));
    private readonly TimeSpan _activationStallTimeout = activationStallTimeout ?? readinessTimeout ?? TimeSpan.FromMinutes(2);
    private readonly Action<string>? _raiseActivationAttention = raiseActivationAttention;
    private readonly string _dotnetPath = dotnetPath ??
        Environment.GetEnvironmentVariable("MCG_ORCHESTRATOR_DOTNET_PATH") ??
        "dotnet";

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
        var consecutiveStagingFailures = 0;
        var stagingDisabled = false;
        ConductorPreparedSuccessor? pendingSuccessor = null;
        var currentBuild = new ConductorActivationBuild("incumbent", "default", null, null);
        ConductorActivationBuild? failedActivationBuild = null;
        string? blockedActivationCommit = null;
        var restoring = false;
        var attempt = 0;

        try
        {
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
            var successor = pendingSuccessor;
            var activationMonitor = new ActivationMonitor();
            var readiness = successor is null
                ? null
                : new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var commandPrefix = successor is null
                ? currentBuild.CommandPrefix
                : new[]
                {
                    _dotnetPath,
                    successor.AppDllPath
                };
            var request = new ConductorSupervisorProcessRequest(
                childArgs,
                workingDirectory,
                artifactPath,
                stdoutPath,
                stderrPath,
                commandPrefix,
                line =>
                {
                    activationMonitor.OnLine(line);
                    if (line.StartsWith(LoopReadyLinePrefix, StringComparison.Ordinal) ||
                        line.StartsWith("LOOP_START ", StringComparison.Ordinal))
                    {
                        readiness?.TrySetResult();
                    }
                });
            try
            {
                if (successor is null)
                {
                    if (restoring)
                    {
                        using var restoreCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        Task<ConductorSupervisorProcessResult> restoreTask;
                        try
                        {
                            restoreTask = processHost.RunAsync(request, restoreCts.Token);
                        }
                        catch (Exception ex)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            RecordActivation("failed-both", attempt, failedActivationBuild!, currentBuild,
                                ConductorActivationRevertReason.SuccessorExited,
                                $"restored-build-launch-failed {ex.GetType().Name}:{ex.Message}");
                            RaiseFailedBothAttention(workingDirectory, failedActivationBuild!, currentBuild,
                                $"restored-build-launch-failed {ex.GetType().Name}:{ex.Message}");
                            Record("restart", "escalated", attempt, "activation-failed-both", 0, stdoutPath, stderrPath);
                            Console.Error.WriteLine("[conduct supervisor] Escalated: both activation builds failed.");
                            return 1;
                        }
                        var restore = await ObserveActivationAsync(
                            restoreTask, activationMonitor, artifactPath, restoreCts, cancellationToken)
                            .ConfigureAwait(false);
                        if (!restore.Adopted)
                        {
                            if (!restore.TerminationConfirmed)
                            {
                                RecordActivation("failed-both", attempt, failedActivationBuild!, currentBuild,
                                    restore.Reason, restore.Detail + " terminationConfirmed=false");
                                RaiseFailedBothAttention(workingDirectory, failedActivationBuild!, currentBuild,
                                    restore.Detail + " terminationConfirmed=false");
                                return 1;
                            }

                            RecordActivation("failed-both", attempt, failedActivationBuild!, currentBuild,
                                restore.Reason, restore.Detail);
                            RaiseFailedBothAttention(workingDirectory, failedActivationBuild!, currentBuild,
                                restore.Detail);
                            Record("restart", "escalated", attempt, "activation-failed-both", 0, stdoutPath, stderrPath);
                            Console.Error.WriteLine("[conduct supervisor] Escalated: both activation builds failed.");
                            return 1;
                        }

                        restoring = false;
                        RecordActivation("restored", attempt, failedActivationBuild!, currentBuild, null, restore.Detail);
                        failedActivationBuild = null;
                        result = restore.ProcessResult ?? await restoreTask.ConfigureAwait(false);
                    }
                    else
                    {
                        result = await processHost.RunAsync(request, cancellationToken).ConfigureAwait(false);
                    }
                }
                else
                {
                    using var processCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    Task<ConductorSupervisorProcessResult> runTask;
                    try
                    {
                        runTask = processHost.RunAsync(request, processCts.Token);
                    }
                    catch (Exception ex)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        pendingSuccessor = null;
                        successor.RunDirectoryLease?.Dispose();
                        var failed = ConductorActivationBuild.FromSuccessor(successor, _dotnetPath);
                        blockedActivationCommit = failed.CommitSha;
                        failedActivationBuild = failed;
                        restoring = true;
                        RecordActivation("reverted", attempt, failed, currentBuild,
                            ConductorActivationRevertReason.SuccessorExited, ex.Message);
                        EmitHandoff(
                            "failed",
                            attempt,
                            $"LOOP_HANDOFF_FAILED attempt={attempt} phase=readiness " +
                            $"reason={Sanitize($"successor-launch-failed {ex.GetType().Name}:{ex.Message}")}",
                            0,
                            stdoutPath,
                            stderrPath);
                        TryDeleteArtifact(artifactPath);
                        continue;
                    }

                    var timeoutTask = _delay(_readinessTimeout, timeoutCts.Token);
                    var completed = await Task.WhenAny(runTask, readiness!.Task, timeoutTask)
                        .ConfigureAwait(false);
                    if (readiness.Task.IsCompletedSuccessfully)
                    {
                        timeoutCts.Cancel();
                        consecutiveStagingFailures = 0;
                        pendingSuccessor = null;
                        EmitHandoff(
                            "completed",
                            attempt,
                            $"LOOP_HANDOFF attempt={attempt} stagedSourceCommit={successor.StagedSourceCommit} " +
                            $"repositoryHead={successor.RepositoryHead} runDir={successor.RunDirectory}",
                            0,
                            stdoutPath,
                            stderrPath);
                        var activation = await ObserveActivationAsync(
                            runTask, activationMonitor, artifactPath, processCts, cancellationToken)
                            .ConfigureAwait(false);
                        var candidateBuild = ConductorActivationBuild.FromSuccessor(successor, _dotnetPath);
                        if (!activation.Adopted)
                        {
                            if (!activation.TerminationConfirmed)
                            {
                                candidateBuild.Lease?.Dispose();
                                EmitHandoff("failed", attempt,
                                    $"LOOP_HANDOFF_FAILED attempt={attempt} phase=activation fallbackSuppressed=true reason={activation.Reason}",
                                    activation.ProcessResult?.ProcessId ?? 0, stdoutPath, stderrPath);
                                return 1;
                            }

                            blockedActivationCommit = candidateBuild.CommitSha;
                            failedActivationBuild = candidateBuild;
                            restoring = true;
                            candidateBuild.Lease?.Dispose();
                            RecordActivation("reverted", attempt, candidateBuild, currentBuild,
                                activation.Reason, activation.Detail);
                            TryDeleteArtifact(artifactPath);
                            continue;
                        }

                        currentBuild.Lease?.Dispose();
                        currentBuild = candidateBuild;
                        RecordActivation("adopted", attempt, candidateBuild, currentBuild, null, activation.Detail);
                        result = activation.ProcessResult ?? await runTask.ConfigureAwait(false);
                    }
                    else
                    {
                        processCts.Cancel();
                        timeoutCts.Cancel();
                        var failureReason = completed == runTask
                            ? await DescribeReadinessExit(runTask).ConfigureAwait(false)
                            : $"timeoutSeconds={(int)_readinessTimeout.TotalSeconds}";
                        ConductorSupervisorProcessResult? stoppedResult = null;
                        if (completed != runTask)
                        {
                            stoppedResult = await ObserveStoppedSuccessor(runTask).ConfigureAwait(false);
                        }

                        cancellationToken.ThrowIfCancellationRequested();
                        if (completed != runTask && stoppedResult?.TerminationConfirmed != true)
                        {
                            EmitHandoff(
                                "failed",
                                attempt,
                                $"LOOP_HANDOFF_FAILED attempt={attempt} phase=readiness " +
                                $"reason={Sanitize(failureReason)} terminationConfirmed=false fallbackSuppressed=true",
                                stoppedResult?.ProcessId ?? 0,
                                stdoutPath,
                                stderrPath);
                            return 1;
                        }

                        pendingSuccessor = null;
                        successor.RunDirectoryLease?.Dispose();
                        var failed = ConductorActivationBuild.FromSuccessor(successor, _dotnetPath);
                        blockedActivationCommit = failed.CommitSha;
                        failedActivationBuild = failed;
                        restoring = true;
                        RecordActivation("reverted", attempt, failed, currentBuild,
                            completed == runTask ? ConductorActivationRevertReason.SuccessorExited :
                                ConductorActivationRevertReason.ReadinessNeverReported, failureReason);
                        EmitHandoff(
                            "failed",
                            attempt,
                            $"LOOP_HANDOFF_FAILED attempt={attempt} phase=readiness reason={Sanitize(failureReason)}",
                            0,
                            stdoutPath,
                            stderrPath);
                        TryDeleteArtifact(artifactPath);
                        continue;
                    }
                }
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
            cancellationToken.ThrowIfCancellationRequested();
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
                if (stageSuccessor is not null && !stagingDisabled)
                {
                    TryAppendConductEvent(
                        $"LOOP_HANDOFF_STAGING attempt={attempt} repositoryHead=resolve-pending");
                    try
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        pendingSuccessor?.RunDirectoryLease?.Dispose();
                        pendingSuccessor = stageSuccessor(cancellationToken);
                        if (pendingSuccessor is not null &&
                            string.Equals(pendingSuccessor.RepositoryHead, blockedActivationCommit,
                                StringComparison.OrdinalIgnoreCase))
                        {
                            var suppressed = ConductorActivationBuild.FromSuccessor(pendingSuccessor, _dotnetPath);
                            suppressed.Lease?.Dispose();
                            RecordActivation("suppressed", attempt, suppressed, currentBuild, null,
                                "same-HEAD activation already reverted");
                            pendingSuccessor = null;
                        }
                        else if (pendingSuccessor is not null)
                        {
                            blockedActivationCommit = null;
                        }
                        cancellationToken.ThrowIfCancellationRequested();
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        consecutiveStagingFailures++;
                        stagingDisabled = consecutiveStagingFailures >= Math.Max(1, maxConsecutiveStagingFailures);
                        var phase = ex is ConductorSelfRelaunchPreparationException preparation
                            ? preparation.Phase
                            : "stage";
                        EmitHandoff(
                            "failed",
                            attempt,
                            $"LOOP_HANDOFF_FAILED attempt={attempt} phase={phase} " +
                            $"reason={Sanitize($"{ex.GetType().Name}:{ex.Message}")}" +
                            (stagingDisabled ? " stagingDisabled=true" : string.Empty),
                            result.ProcessId,
                            stdoutPath,
                            stderrPath);
                    }
                }
                else if (stageSuccessor is not null)
                {
                    EmitHandoff(
                        "failed",
                        attempt,
                        $"LOOP_HANDOFF_FAILED attempt={attempt} phase=stage " +
                        $"reason=staging-disabled consecutiveFailures={consecutiveStagingFailures}",
                        result.ProcessId,
                        stdoutPath,
                        stderrPath);
                }
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
        finally
        {
            pendingSuccessor?.RunDirectoryLease?.Dispose();
            currentBuild.Lease?.Dispose();
        }
    }

    private static async Task<string> DescribeReadinessExit(
        Task<ConductorSupervisorProcessResult> runTask)
    {
        try
        {
            var result = await runTask.ConfigureAwait(false);
            return $"successor-exited-before-readiness exit={result.ExitCode}";
        }
        catch (Exception ex)
        {
            return $"successor-launch-failed {ex.GetType().Name}:{ex.Message}";
        }
    }

    private static async Task<ConductorSupervisorProcessResult?> ObserveStoppedSuccessor(
        Task<ConductorSupervisorProcessResult> runTask)
    {
        try
        {
            return await runTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void EmitHandoff(
        string status,
        int attempt,
        string detail,
        int processId,
        string stdoutPath,
        string stderrPath)
    {
        TryAppendConductEvent(detail);
        try
        {
            Record("handoff", status, attempt, detail, processId, stdoutPath, stderrPath);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"[conduct supervisor] Could not record handoff run event: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void TryAppendConductEvent(string detail)
    {
        try
        {
            appendConductEvent?.Invoke("loop-handoff", null, detail);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"[conduct supervisor] Could not append conduct event: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static string Sanitize(string value) =>
        value.Replace('\r', ' ').Replace('\n', ' ').Trim();

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
                try
                {
                    request.OnStandardOutputLine?.Invoke(eventArgs.Data);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine(
                        $"[conduct supervisor] Standard-output observer failed: {ex.GetType().Name}: {ex.Message}");
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
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            var terminationConfirmed = process.HasExited;
            try
            {
                if (!terminationConfirmed)
                {
                    process.Kill(entireProcessTree: true);
                    terminationConfirmed = process.WaitForExit(5000);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                terminationConfirmed = process.HasExited;
            }

            return new ConductorSupervisorProcessResult(
                -1,
                process.Id,
                request.StdoutPath,
                request.StderrPath,
                terminationConfirmed);
        }
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
        var hasCommandPrefix = request.CommandPrefix is { Count: > 0 };
        var executable = hasCommandPrefix
            ? request.CommandPrefix![0]
            : Environment.ProcessPath ?? "dotnet";
        var startInfo = new ProcessStartInfo(executable);
        if (hasCommandPrefix)
        {
            foreach (var argument in request.CommandPrefix!.Skip(1))
            {
                startInfo.ArgumentList.Add(argument);
            }
        }
        else if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase) &&
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
