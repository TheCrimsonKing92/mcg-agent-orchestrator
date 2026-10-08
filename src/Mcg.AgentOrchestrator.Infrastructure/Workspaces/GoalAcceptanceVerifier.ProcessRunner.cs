using System.Diagnostics;
using System.IO.Pipes;
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
        bool? keepCaptureFiles = null,
        Action<int>? resumeObserver = null,
        bool forceUtf8ConsoleOutput = false) =>
        RunProcessAsync(
            arguments,
            workingDirectory,
            commandTimeout,
            forceUtf8ConsoleOutput,
            cancellationToken,
            cleanupObserver,
            timeoutSignal,
            commandIdentityObserver: commandIdentityObserver,
            keepCaptureFiles: keepCaptureFiles,
            resumeObserver: resumeObserver);

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
        bool? keepCaptureFiles = null,
        Action<int>? resumeObserver = null) =>
        await AcceptanceCheckProcessInvoker.RunAsync(
            arguments, workingDirectory, commandTimeout, forceUtf8ConsoleOutput, cancellationToken,
            timeoutSignal, registrationIdentityReader, new AcceptanceProcessRequest
            {
                CleanupObserver = cleanupObserver,
                CommandIdentityObserver = commandIdentityObserver,
                HeartbeatContext = heartbeatContext,
                ProgressSink = progressSink,
                EngineSettings = engineSettings,
                AttemptResultsPrefix = attemptResultsPrefix,
                GateInvocationId = gateInvocationId,
                ApparatusReceiptPath = apparatusReceiptPath,
                HeartbeatInterval = heartbeatInterval,
                ProgressInterval = progressInterval,
                CapturePublicationInterval = capturePublicationInterval,
                StopOnCaptureLimit = stopOnCaptureLimit,
                KeepCaptureFiles = keepCaptureFiles,
                ResumeObserver = resumeObserver,
            }).ConfigureAwait(false);

    internal static class ProcessInvokerAccess
    {
        internal static (string Stdout, string Stderr) CreateCaptureFilePaths() =>
            GoalAcceptanceVerifier.CreateCaptureFilePaths();

        internal static bool ShouldKeepCaptureFiles(bool? keepCaptureFiles, bool timedOut, int exitCode) =>
            GoalAcceptanceVerifier.ShouldKeepCaptureFiles(keepCaptureFiles, timedOut, exitCode);

        internal static string QuoteForDisplay(string value) => GoalAcceptanceVerifier.QuoteForDisplay(value);

        internal static TimeSpan DefaultHeartbeatInterval => GoalAcceptanceVerifier.DefaultHeartbeatInterval;
        internal static TimeSpan DefaultProgressInterval => GoalAcceptanceVerifier.DefaultProgressInterval;

        internal static NamedPipeServerStream CreateCapturePipe(string pipeName) =>
            GoalAcceptanceVerifier.CreateCapturePipe(pipeName);

        internal static Task<CaptureLimitResult> ConnectAndDrainCappedCaptureAsync(
            NamedPipeServerStream source,
            Task connection,
            string path,
            long limitBytes,
            Func<DateTimeOffset> utcNow,
            Action? onLimitReached, CancellationToken cancellationToken, TimeSpan? capturePublicationInterval) =>
            GoalAcceptanceVerifier.ConnectAndDrainCappedCaptureAsync(
                source, connection, path, limitBytes, utcNow, onLimitReached, cancellationToken, capturePublicationInterval);

        internal static Task<IReadOnlyList<CaptureLimitResult>> CompleteCaptureDrainsAsync(
            RegisteredOwnedProcess process,
            Task<CaptureLimitResult>[] captureDrains,
            CancellationTokenSource captureDrainCts,
            IReadOnlyList<Stream> captureSources) =>
            GoalAcceptanceVerifier.CompleteCaptureDrainsAsync(process, captureDrains, captureDrainCts, captureSources);

        internal static Task CancelCaptureDrainsAsync(
            CancellationTokenSource captureDrainCts,
            Task<CaptureLimitResult>[]? captureDrains,
            IReadOnlyList<Stream>? captureSources) =>
            GoalAcceptanceVerifier.CancelCaptureDrainsAsync(captureDrainCts, captureDrains, captureSources);

        internal static void DisposeCaptureSources(IReadOnlyList<Stream>? captureSources) =>
            GoalAcceptanceVerifier.DisposeCaptureSources(captureSources);

        internal static void EmitCaptureLimitReached(
            GateHeartbeatContext? context, string? attemptResultsPrefix, string path, long capBytes) =>
            GoalAcceptanceVerifier.EmitCaptureLimitReached(context, attemptResultsPrefix, path, capBytes);
    }
}
