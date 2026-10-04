using System.Diagnostics;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed partial class GoalAcceptanceVerifier
{
    private static async Task<CommandResult> RunProcessAsync(
        string[] arguments,
        string workingDirectory,
        TimeSpan commandTimeout,
        CancellationToken cancellationToken) =>
        await RunProcessAsync(
            arguments,
            workingDirectory,
            commandTimeout,
            forceUtf8ConsoleOutput: false,
            cancellationToken).ConfigureAwait(false);

    internal static Task<CommandResult> RunProcessForTestsAsync(
        string[] arguments,
        string workingDirectory,
        TimeSpan commandTimeout,
        CancellationToken cancellationToken = default,
        Action<AcceptanceProcessCleanupObservation>? cleanupObserver = null,
        CancellationToken timeoutSignal = default,
        Action<SpawnProcessIdentity>? commandIdentityObserver = null,
        bool? keepCaptureFiles = null) =>
        RunProcessAsync(
            arguments,
            workingDirectory,
            commandTimeout,
            forceUtf8ConsoleOutput: false,
            cancellationToken,
            cleanupObserver,
            timeoutSignal,
            commandIdentityObserver: commandIdentityObserver,
            keepCaptureFiles: keepCaptureFiles);

    internal static Task<(CommandResult Result, string HeartbeatPath)> RunProcessWithHeartbeatForTestsAsync(
        string[] arguments,
        string workingDirectory,
        TimeSpan commandTimeout,
        string heartbeatPath,
        Action<AcceptanceProcessCleanupObservation> cleanupObserver,
        CancellationToken cancellationToken = default,
        CancellationToken timeoutSignal = default) =>
        RunProcessWithHeartbeatForTestsAsync(
            arguments,
            workingDirectory,
            commandTimeout,
            heartbeatPath,
            cleanupObserver,
            cancellationToken,
            timeoutSignal,
            registrationIdentityReader: null);

    internal static async Task<(CommandResult Result, string HeartbeatPath)> RunProcessWithHeartbeatForTestsAsync(
        string[] arguments,
        string workingDirectory,
        TimeSpan commandTimeout,
        string heartbeatPath,
        Action<AcceptanceProcessCleanupObservation> cleanupObserver,
        CancellationToken cancellationToken = default,
        CancellationToken timeoutSignal = default,
        Func<Process, SpawnProcessIdentity>? registrationIdentityReader = null)
    {
        var heartbeatContext = new GateHeartbeatContext(
            "test-goal",
            "verification-check",
            "owned-child-cleanup",
            null,
            heartbeatPath,
            null,
            string.Join(' ', arguments.Select(QuoteForDisplay)),
            null);
        var result = await RunProcessAsync(
                arguments,
                workingDirectory,
                commandTimeout,
                forceUtf8ConsoleOutput: false,
                cancellationToken,
                cleanupObserver,
                timeoutSignal,
                registrationIdentityReader is null
                    ? null
                    : process => new SpawnProcessIdentityReadResult(
                        registrationIdentityReader(process),
                        1,
                        "test-identity-seam"),
                heartbeatContext: heartbeatContext).ConfigureAwait(false);
        return (result, heartbeatPath);
    }

    private static async Task<CommandResult> RunUtf8DiscoveryProcessAsync(
        string[] arguments,
        string workingDirectory,
        TimeSpan commandTimeout,
        CancellationToken cancellationToken) =>
        await RunProcessAsync(
            arguments,
            workingDirectory,
            commandTimeout,
            forceUtf8ConsoleOutput: true,
            cancellationToken).ConfigureAwait(false);

    private static async Task<CommandResult> RunProcessAsync(
        string[] arguments,
        string workingDirectory,
        TimeSpan commandTimeout,
        bool forceUtf8ConsoleOutput,
        CancellationToken cancellationToken,
        Action<AcceptanceProcessCleanupObservation>? cleanupObserver = null,
        CancellationToken timeoutSignal = default,
        Func<Process, SpawnProcessIdentityReadResult>? registrationIdentityReader = null,
        Action<SpawnProcessIdentity>? commandIdentityObserver = null,
        GateHeartbeatContext? heartbeatContext = null,
        Action<AcceptanceGateProgress>? progressSink = null,
        AcceptanceGateEngineSettings? engineSettings = null,
        string? attemptResultsPrefix = null,
        string? gateInvocationId = null,
        string? apparatusReceiptPath = null,
        TimeSpan? heartbeatInterval = null, TimeSpan? progressInterval = null,
        TimeSpan? capturePublicationInterval = null,
        bool stopOnCaptureLimit = false,
        bool? keepCaptureFiles = null)
    {
        engineSettings ??= new AcceptanceGateEngineSettings();
        // Keep the shell command semantics, but own the capture file offsets in this process. The
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

        int? startedProcessId = null;
        int? completedProcessId = null;
        DateTimeOffset? completedProcessStartedAt = null;
        try
        {
            captureDrainCts = new CancellationTokenSource();
            Task[]? captureConnections = null;
            if (stdoutPipeName is not null && stderrPipeName is not null)
            {
                var stdoutPipe = CreateCapturePipe(stdoutPipeName);
                var stderrPipe = CreateCapturePipe(stderrPipeName);
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

            process = StartAcceptanceProcess(startInfo, workingDirectory, registrationIdentityReader);
            commandIdentityTracker = new AcceptanceCommandProcessIdentityTracker(process, commandIdentityObserver);
            commandIdentityTracker.Start();
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
            await commandIdentityTracker.DisposeAsync().ConfigureAwait(false);
            completedProcessId = commandIdentityTracker.Identity?.ProcessId ?? completedProcessId;
            completedProcessStartedAt = commandIdentityTracker.Identity?.StartedAt ?? completedProcessStartedAt;
            commandIdentityTracker = null;
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
        Func<Process, SpawnProcessIdentityReadResult>? registrationIdentityReader)
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

        return WorkerProcessJobs.StartRegisteredOwnedOrThrow(
            startInfo,
            $"acceptance:{workingDirectory}",
            registrationIdentityReader);
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
