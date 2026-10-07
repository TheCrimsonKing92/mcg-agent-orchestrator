using System.Diagnostics;
using Mcg.AgentOrchestrator.Core;
using static Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier;
using static Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.ProcessInvokerAccess;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed record AcceptanceProcessRequest
{
    public Action<AcceptanceProcessCleanupObservation>? CleanupObserver { get; init; } = null;
    public Action<SpawnProcessIdentity>? CommandIdentityObserver { get; init; } = null;
    public GateHeartbeatContext? HeartbeatContext { get; init; } = null;
    public Action<AcceptanceGateProgress>? ProgressSink { get; init; } = null;
    public AcceptanceGateEngineSettings? EngineSettings { get; init; } = null;
    public string? AttemptResultsPrefix { get; init; } = null;
    public string? GateInvocationId { get; init; } = null;
    public string? ApparatusReceiptPath { get; init; } = null;
    public TimeSpan? HeartbeatInterval { get; init; } = null;
    public TimeSpan? ProgressInterval { get; init; } = null;
    public TimeSpan? CapturePublicationInterval { get; init; } = null;
    public bool StopOnCaptureLimit { get; init; } = false;
    public bool? KeepCaptureFiles { get; init; } = null;
    public Action<int>? ResumeObserver { get; init; } = null;
}

internal static class AcceptanceCheckProcessInvoker
{
    internal static async Task<CommandResult> RunAsync(
        string[] arguments,
        string workingDirectory,
        TimeSpan commandTimeout,
        bool forceUtf8ConsoleOutput,
        CancellationToken cancellationToken,
        CancellationToken timeoutSignal,
        Func<Process, SpawnProcessIdentityReadResult>? registrationIdentityReader,
        AcceptanceProcessRequest request)
    {
        var cleanupObserver = request.CleanupObserver;
        var commandIdentityObserver = request.CommandIdentityObserver;
        var heartbeatContext = request.HeartbeatContext;
        var progressSink = request.ProgressSink;
        var engineSettings = request.EngineSettings;
        var attemptResultsPrefix = request.AttemptResultsPrefix;
        var gateInvocationId = request.GateInvocationId;
        var apparatusReceiptPath = request.ApparatusReceiptPath;
        var heartbeatInterval = request.HeartbeatInterval;
        var progressInterval = request.ProgressInterval;
        var capturePublicationInterval = request.CapturePublicationInterval;
        var stopOnCaptureLimit = request.StopOnCaptureLimit;
        var keepCaptureFiles = request.KeepCaptureFiles;
        var resumeObserver = request.ResumeObserver;
        engineSettings ??= new AcceptanceGateEngineSettings();
        // Own the capture file offsets in this process. The
        // drain keeps consuming after the cap so a noisy child cannot block or grow the files.
        var (stdoutPath, stderrPath) = CreateCaptureFilePaths();

        var commandLine = string.Join(' ', arguments.Select(QuoteForDisplay));
        var timedOut = false;
        using var captureLimitStop = new CaptureLimitStop(stopOnCaptureLimit);
        var elapsed = Stopwatch.StartNew();
        CancellationTokenSource? heartbeatCts = null;
        Task? heartbeatTask = null;
        GateHeartbeatRuntime? heartbeat = null;
        var keepOutputFiles = false;
        var stdoutPipeName = OperatingSystem.IsWindows() ? $"mcg-acc-{Guid.NewGuid():N}-out" : null;
        var stderrPipeName = OperatingSystem.IsWindows() ? $"mcg-acc-{Guid.NewGuid():N}-err" : null;
        var startInfo = BuildAcceptanceProcessStartInfo(
            arguments,
            workingDirectory,
            stdoutPipeName is null ? null : $@"\\.\pipe\{stdoutPipeName}",
            stderrPipeName is null ? null : $@"\\.\pipe\{stderrPipeName}",
            forceUtf8ConsoleOutput);
        CancellationTokenSource? captureDrainCts = null;
        Task<CaptureLimitResult>[]? captureDrains = null;
        Stream[]? captureSources = null;
        RegisteredOwnedProcess? process = null;
        AcceptanceCommandProcessIdentityTracker? commandIdentityTracker = null;
        var heartbeatFinalized = false;
        var registrationReleased = false;
        var processDisposed = false;

        ConfigureHermeticVerificationEnvironment(
            startInfo.Environment,
            workingDirectory,
            heartbeatContext?.BuildEnvironmentRoot);
        if (string.IsNullOrWhiteSpace(gateInvocationId))
            TempRootApparatusLossReceiptStore.ApplyCurrentScope(startInfo.Environment);
        else
            TempRootApparatusLossReceiptStore.ApplyScope(
                startInfo.Environment, gateInvocationId, apparatusReceiptPath);

        var directSdk = OperatingSystem.IsWindows() && !forceUtf8ConsoleOutput &&
            stdoutPipeName is not null && stderrPipeName is not null &&
            AcceptanceSdkConsoleRule.RequiresOwnConsole(arguments);
        ProcessStartInfo? directStartInfo = null;
        if (directSdk)
        {
            directStartInfo = new ProcessStartInfo
            {
                FileName = arguments[0],
                UseShellExecute = startInfo.UseShellExecute,
                CreateNoWindow = startInfo.CreateNoWindow,
                WindowStyle = startInfo.WindowStyle,
                WorkingDirectory = startInfo.WorkingDirectory,
                StandardInputEncoding = startInfo.StandardInputEncoding,
                StandardOutputEncoding = startInfo.StandardOutputEncoding,
                StandardErrorEncoding = startInfo.StandardErrorEncoding
            };
            foreach (var argument in arguments.Skip(1)) directStartInfo.ArgumentList.Add(argument);
            directStartInfo.Environment.Clear();
            foreach (var pair in startInfo.Environment) directStartInfo.Environment[pair.Key] = pair.Value;
        }

        int? startedProcessId = null;
        int? completedProcessId = null;
        DateTimeOffset? completedProcessStartedAt = null;
        try
        {
            captureDrainCts = new CancellationTokenSource();
            Task[]? captureConnections = null;
            void BeginOwnedCapture()
            {
                var stdoutPipe = CreateCapturePipe(stdoutPipeName!);
                var stderrPipe = CreateCapturePipe(stderrPipeName!);
                captureSources = [stdoutPipe, stderrPipe];
                var stdoutConnection = stdoutPipe.WaitForConnectionAsync(captureDrainCts.Token);
                var stderrConnection = stderrPipe.WaitForConnectionAsync(captureDrainCts.Token);
                captureConnections = [stdoutConnection, stderrConnection];
                // Begin accepting and draining before process start. The test-only legacy
                // start-then-attach control waits for the child to exit inside
                // StartAcceptanceProcess; delaying the drains until that method returned could
                // fill the named-pipe buffer and deadlock the child before the expected attach
                // failure was observed.
                captureDrains =
                [
                    ConnectAndDrainCappedCaptureAsync(
                        stdoutPipe,
                        stdoutConnection,
                        stdoutPath,
                        engineSettings.OutputCaptureLimitBytes,
                        () => DateTimeOffset.UtcNow,
                        onLimitReached: captureLimitStop.OnLimitReached,
                        captureDrainCts.Token, capturePublicationInterval),
                    ConnectAndDrainCappedCaptureAsync(
                        stderrPipe,
                        stderrConnection,
                        stderrPath,
                        engineSettings.OutputCaptureLimitBytes,
                        () => DateTimeOffset.UtcNow,
                        onLimitReached: captureLimitStop.OnLimitReached,
                        captureDrainCts.Token, capturePublicationInterval)
                ];
            }
            if (stdoutPipeName is not null && stderrPipeName is not null) BeginOwnedCapture();
            var requestOwnConsole = forceUtf8ConsoleOutput || AcceptanceSdkConsoleRule.RequiresOwnConsole(arguments);
            try
            {
                if (directStartInfo is null && !requestOwnConsole && resumeObserver is null)
                {
                    process = StartAcceptanceProcess(startInfo, workingDirectory, registrationIdentityReader);
                }
                else
                {
                    process = StartAcceptanceProcess(
                        startInfo, workingDirectory, registrationIdentityReader, requestOwnConsole,
                        directStartInfo,
                        stdoutPipeName is null ? null : $@"\\.\pipe\{stdoutPipeName}",
                        stderrPipeName is null ? null : $@"\\.\pipe\{stderrPipeName}",
                        resumeObserver);
                }
            }
            catch (InvalidOperationException ex) when (directSdk &&
                ex.InnerException is OwnedProcessLaunchException { NativeErrorCode: 2 or 3 or 267 })
            {
                // Only CreateProcess's missing-image failure permits fallback. Capture-open failures
                // are plain Win32Exceptions and must propagate. The client handles have closed, so
                // require EOF and no captured bytes before giving the shell fresh capture custody.
                var failedCaptures = await Task.WhenAll(captureDrains!)
                    .WaitAsync(CaptureDrainTimeout, cancellationToken).ConfigureAwait(false);
                if (failedCaptures.Any(capture => capture.WrittenBytes != 0)) throw;
                foreach (var source in captureSources!) source.Dispose();
                stdoutPipeName = $"mcg-acc-{Guid.NewGuid():N}-out";
                stderrPipeName = $"mcg-acc-{Guid.NewGuid():N}-err";
                var fallbackStartInfo = BuildAcceptanceProcessStartInfo(
                    arguments, workingDirectory,
                    $@"\\.\pipe\{stdoutPipeName}", $@"\\.\pipe\{stderrPipeName}", forceUtf8ConsoleOutput);
                fallbackStartInfo.Environment.Clear();
                foreach (var pair in startInfo.Environment) fallbackStartInfo.Environment[pair.Key] = pair.Value;
                BeginOwnedCapture();
                process = StartAcceptanceProcess(
                    fallbackStartInfo, workingDirectory, registrationIdentityReader, requestOwnConsole,
                    resumeObserver: resumeObserver);
                directSdk = false;
            }
            if (!directSdk)
            {
                commandIdentityTracker = new AcceptanceCommandProcessIdentityTracker(process, commandIdentityObserver);
                commandIdentityTracker.Start();
            }
            else if (process.Identity is { } identity)
            {
                commandIdentityObserver?.Invoke(identity);
            }
            startedProcessId = process.Id;
            completedProcessId = process.Id;
            completedProcessStartedAt = process.Identity?.StartedAt;
            ObserveProcessCleanup(cleanupObserver, process.Id, process, "started");
            if (captureConnections is not null)
            {
                await Task.WhenAll(captureConnections)
                    .WaitAsync(CaptureDrainTimeout, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                captureSources =
                [
                    process.StandardOutput.BaseStream,
                    process.StandardError.BaseStream
                ];
                captureDrains =
                [
                    DrainCappedCaptureAsync(
                        captureSources[0],
                        stdoutPath,
                        engineSettings.OutputCaptureLimitBytes,
                        () => DateTimeOffset.UtcNow,
                        onLimitReached: captureLimitStop.OnLimitReached,
                        cancellationToken: captureDrainCts.Token, publicationInterval: capturePublicationInterval),
                    DrainCappedCaptureAsync(
                        captureSources[1],
                        stderrPath,
                        engineSettings.OutputCaptureLimitBytes,
                        () => DateTimeOffset.UtcNow,
                        onLimitReached: captureLimitStop.OnLimitReached,
                        cancellationToken: captureDrainCts.Token, publicationInterval: capturePublicationInterval)
                ];
            }
            if (heartbeatContext is not null)
            {
                heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                heartbeat = new GateHeartbeatRuntime(
                    heartbeatContext,
                    process.Id,
                    stdoutPath,
                    stderrPath,
                    commandTimeout,
                    progressSink,
                    progressInterval ?? DefaultProgressInterval);
                heartbeatTask = WriteGateHeartbeatLoopAsync(
                    heartbeat,
                    heartbeatCts.Token,
                    heartbeatInterval ?? DefaultHeartbeatInterval);
            }

            using var timeoutCts = timeoutSignal.CanBeCanceled
                ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSignal, captureLimitStop.Token)
                : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, captureLimitStop.Token);
            if (!timeoutSignal.CanBeCanceled)
            {
                timeoutCts.CancelAfter(commandTimeout);
            }

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
                if (cancellationToken.IsCancellationRequested)
                    throw;

                timedOut = !captureLimitStop.Stopped;
                if (captureLimitStop.Stopped)
                    await process.WaitForExitAsync(CancellationToken.None).WaitAsync(CaptureDrainTimeout).ConfigureAwait(false);
            }

            var captureResults = await CompleteCaptureDrainsAsync(
                process,
                captureDrains,
                captureDrainCts,
                captureSources).ConfigureAwait(false);
            captureDrains = null;
            captureDrainCts.Dispose();
            captureDrainCts = null;
            DisposeCaptureSources(captureSources);
            captureSources = null;

            foreach (var capture in captureResults.Where(result => result.LimitReached))
            {
                EmitCaptureLimitReached(
                    heartbeatContext,
                    attemptResultsPrefix,
                    capture.Path,
                    engineSettings.OutputCaptureLimitBytes);
            }

            var cappedPaths = captureResults
                .Where(result => result.LimitReached)
                .Select(result => result.Path)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var stdout = await ReadCapturedFileWithRetryAsync(
                stdoutPath,
                cappedPaths.Contains(stdoutPath)).ConfigureAwait(false);
            var stderr = await ReadCapturedFileWithRetryAsync(
                stderrPath,
                cappedPaths.Contains(stderrPath)).ConfigureAwait(false);
            var stdoutBytes = TryGetFileLength(stdoutPath);
            var stderrBytes = TryGetFileLength(stderrPath);
            elapsed.Stop();
            var captureLimited = captureLimitStop.Stopped;
            timedOut &= !captureLimited;
            var exitCode = timedOut || captureLimited ? -1 : process.ExitCode;
            if (commandIdentityTracker is not null)
            {
                await commandIdentityTracker.DisposeAsync().ConfigureAwait(false);
                completedProcessId = commandIdentityTracker.Identity?.ProcessId ?? completedProcessId;
                completedProcessStartedAt = commandIdentityTracker.Identity?.StartedAt ?? completedProcessStartedAt;
                commandIdentityTracker = null;
            }
            keepOutputFiles = ShouldKeepCaptureFiles(keepCaptureFiles, timedOut, exitCode);
            WorkerProcessJobAccounting? accounting = null;
            try
            {
                if (heartbeat is not null)
                {
                    if (heartbeatCts is not null)
                    {
                        try { await heartbeatCts.CancelAsync().ConfigureAwait(false); } catch { }
                    }

                    if (heartbeatTask is not null)
                    {
                        try { await heartbeatTask.ConfigureAwait(false); } catch { }
                    }

                    heartbeatFinalized = true;
                    heartbeat.WriteFinal(timedOut ? "timed-out" : captureLimited ? "failed" : "completed", childPid: process.Id, exitCode: exitCode);
                    ObserveProcessCleanup(cleanupObserver, process.Id, process, "heartbeat-final");
                    heartbeat = null;
                    heartbeatCts?.Dispose();
                    heartbeatCts = null;
                    heartbeatTask = null;
                }
            }
            finally
            {
                try
                {
                    registrationReleased = true;
                    _ = process.Release(out accounting);
                    ObserveProcessCleanup(cleanupObserver, process.Id, process, "registration-released");
                }
                finally
                {
                    var disposedProcessId = process.Id;
                    processDisposed = true;
                    process.Dispose();
                    ObserveProcessCleanup(cleanupObserver, disposedProcessId, process, "owned-child-disposed");
                    process = null;
                    startedProcessId = null;
                }
            }
            return new CommandResult(
                exitCode,
                (stdout + stderr).Trim(),
                timedOut,
                commandLine,
                stdoutPath,
                stderrPath,
                commandTimeout,
                elapsed.Elapsed,
                accounting is null
                    ? null
                    : new TaskProcessResourceAccounting(
                        accounting.CpuMilliseconds,
                        accounting.PeakMemoryBytes,
                        accounting.IoBytes,
                        AccountingSource: accounting.AccountingSource),
                ResourceAccountingExpected: OperatingSystem.IsWindows(),
                stdoutBytes,
                stderrBytes,
                stderr,
                completedProcessId,
                completedProcessStartedAt,
                captureLimited,
                captureLimited ? engineSettings.OutputCaptureLimitBytes : null);
        }
        finally
        {
            if (commandIdentityTracker is not null)
            {
                await commandIdentityTracker.DisposeAsync().ConfigureAwait(false);
            }

            if (captureDrainCts is not null)
            {
                await CancelCaptureDrainsAsync(
                    captureDrainCts,
                    captureDrains,
                    captureSources).ConfigureAwait(false);
                captureDrainCts.Dispose();
            }

            if (heartbeatCts is not null)
            {
                try { await heartbeatCts.CancelAsync().ConfigureAwait(false); } catch { }
                if (heartbeatTask is not null)
                {
                    try { await heartbeatTask.ConfigureAwait(false); } catch { }
                }

                heartbeatCts.Dispose();
            }

            try
            {
                if (heartbeat is not null && !heartbeatFinalized)
                {
                    heartbeatFinalized = true;
                    heartbeat.WriteFinal("failed", childPid: startedProcessId, exitCode: null);
                    if (process is not null)
                    {
                        ObserveProcessCleanup(cleanupObserver, process.Id, process, "heartbeat-final");
                    }
                }
            }
            finally
            {
                try
                {
                    if (process is not null && startedProcessId is { } processId && !registrationReleased)
                    {
                        registrationReleased = true;
                        _ = process.Release(out _);
                        ObserveProcessCleanup(cleanupObserver, process.Id, process, "registration-released");
                    }
                }
                finally
                {
                    if (process is not null && !processDisposed)
                    {
                        var disposedProcessId = process.Id;
                        processDisposed = true;
                        process.Dispose();
                        ObserveProcessCleanup(cleanupObserver, disposedProcessId, process, "owned-child-disposed");
                        process = null;
                    }

                    startedProcessId = null;
                }
            }

            if (!keepOutputFiles)
            {
                TryDeleteFile(stdoutPath);
                TryDeleteFile(stderrPath);
            }
        }
    }

    private static void ObserveProcessCleanup(
        Action<AcceptanceProcessCleanupObservation>? observer,
        int processId,
        RegisteredOwnedProcess process,
        string stage)
    {
        if (observer is null)
        {
            return;
        }

        observer(new AcceptanceProcessCleanupObservation(
            processId,
            stage,
            WorkerProcessJobs.HasActiveRegistryEntryForTests(processId),
            WorkerProcessJobs.HasActiveJobForTests(processId),
            process.HasOpenNativeHandle,
            process.IsDisposed));
    }

    private static RegisteredOwnedProcess StartAcceptanceProcess(
        ProcessStartInfo startInfo,
        string workingDirectory,
        Func<Process, SpawnProcessIdentityReadResult>? registrationIdentityReader,
        bool requestOwnConsole = false,
        ProcessStartInfo? directStartInfo = null,
        string? stdoutPipePath = null,
        string? stderrPipePath = null,
        Action<int>? resumeObserver = null)
    {
        if (OperatingSystem.IsWindows() &&
            string.Equals(
                Environment.GetEnvironmentVariable(LegacyOwnedStartNegativeControlVariable),
                "wait-for-fast-exit",
                StringComparison.Ordinal))
        {
            // Cross-process rule-(l) control only: replay the former production sequence through the
            // __acceptance-gate-attempt child, and deterministically expose its start-then-attach window.
            // The external test sets this variable only on that one child process.
            var legacyProcess = ProcessTreeGuiSuppression.Start(startInfo);
            try
            {
                if (!legacyProcess.WaitForExit(TimeSpan.FromSeconds(30)))
                {
                    throw new TimeoutException("Legacy owned-start negative-control child did not exit.");
                }

                return WorkerProcessJobs.AdoptRegisteredOwnedOrThrow(
                    legacyProcess,
                    startInfo,
                    $"acceptance:{workingDirectory}");
            }
            catch
            {
                legacyProcess.Dispose();
                throw;
            }
        }

        if (directStartInfo is not null)
        {
            return WorkerProcessJobs.StartRegisteredOwnedWithFileCaptureOrThrow(
                directStartInfo, stdoutPipePath!, stderrPipePath!,
                $"acceptance:{workingDirectory}", registrationIdentityReader, resumeObserver);
        }

        return WorkerProcessJobs.StartRegisteredOwnedOrThrow(
            startInfo,
            $"acceptance:{workingDirectory}",
            registrationIdentityReader, resumeObserver: resumeObserver, requestOwnConsole: requestOwnConsole);
    }

    private static async Task WriteGateHeartbeatLoopAsync(
        GateHeartbeatRuntime heartbeat,
        CancellationToken cancellationToken,
        TimeSpan heartbeatInterval)
    {
        heartbeat.WriteRunning(emitProgress: true);
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(heartbeatInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            heartbeat.WriteRunning(emitProgress: false);
        }
    }
}
