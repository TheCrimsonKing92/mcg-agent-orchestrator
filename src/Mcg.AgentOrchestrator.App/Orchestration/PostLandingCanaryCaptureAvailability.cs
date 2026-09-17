using System.Diagnostics;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record RetainedCaptureObservation(
    bool ExclusiveOpenSucceeded,
    int Attempts,
    TimeSpan Elapsed,
    string? LastErrorCode,
    FileHandleHolderObservation? Holders = null,
    long? FirstObservedLength = null,
    long? LastObservedLength = null)
{
    /// <summary>
    /// True when the capture did not grow by a single byte across the whole unreadable window. Reading
    /// the length needs only FILE_READ_ATTRIBUTES, so it succeeds while the exclusive open is refused.
    /// This is diagnostic only: a byte-stable capture that still cannot be opened exclusively is
    /// reported as incomplete exactly like one that is still growing.
    /// </summary>
    internal bool LengthWasStable =>
        FirstObservedLength.HasValue &&
        LastObservedLength.HasValue &&
        FirstObservedLength.Value == LastObservedLength.Value;
}

internal static class PostLandingCanaryCaptureAvailability
{
    private static readonly TimeSpan DefaultBudget = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromMilliseconds(50);

    internal static RetainedCaptureObservation ObserveOnce(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var stopwatch = Stopwatch.StartNew();
        var (succeeded, errorCode) = TryOpenForCallerRead(path);
        stopwatch.Stop();
        return new RetainedCaptureObservation(
            succeeded,
            1,
            stopwatch.Elapsed,
            errorCode,
            succeeded ? null : FileHandleHolders.Read(path));
    }

    internal static async Task<RetainedCaptureObservation> AwaitReadableAsync(
        string path,
        CancellationToken cancellationToken,
        TimeSpan? budget = null,
        TimeSpan? pollInterval = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!OperatingSystem.IsWindows())
        {
            return new RetainedCaptureObservation(true, 0, TimeSpan.Zero, null);
        }

        var effectiveBudget = budget ?? DefaultBudget;
        var effectivePollInterval = pollInterval ?? DefaultPollInterval;
        if (effectiveBudget < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(budget));
        }

        if (effectivePollInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(pollInterval));
        }

        var stopwatch = Stopwatch.StartNew();
        var attempts = 0;
        string? lastErrorCode;
        long? firstObservedLength = null;
        long? lastObservedLength = null;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            attempts++;
            var (succeeded, errorCode) = TryOpenForCallerRead(path);
            lastErrorCode = errorCode;
            if (succeeded)
            {
                stopwatch.Stop();
                return new RetainedCaptureObservation(true, attempts, stopwatch.Elapsed, null);
            }

            lastObservedLength = TryReadLength(path);
            firstObservedLength ??= lastObservedLength;
            if (stopwatch.Elapsed >= effectiveBudget)
            {
                break;
            }

            var remaining = effectiveBudget - stopwatch.Elapsed;
            await Task.Delay(
                    remaining < effectivePollInterval ? remaining : effectivePollInterval,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        while (stopwatch.Elapsed < effectiveBudget);

        // Name the actual handle owner at the moment the budget expires - once, synchronously, with no
        // extra wait budget. Without this the terminal evidence can only report that ownership of every
        // process we launched was proven released, which is exactly the state that leaves a sharing
        // violation unexplained.
        var holders = FileHandleHolders.Read(path);
        lastObservedLength = TryReadLength(path) ?? lastObservedLength;
        stopwatch.Stop();
        return new RetainedCaptureObservation(
            false,
            attempts,
            stopwatch.Elapsed,
            lastErrorCode,
            holders,
            firstObservedLength,
            lastObservedLength);
    }

    private static long? TryReadLength(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? info.Length : null;
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

    /// <summary>
    /// The broad sharing mode (FileShare.ReadWrite | FileShare.Delete) that a retry-tolerant capture
    /// reader uses. Diagnostic only: the terminal consumers read with File.ReadAllText, which opens
    /// FileShare.Read and still throws while a foreign write handle lives, so a success here never
    /// decides that a capture is complete.
    /// </summary>
    internal static bool CanOpenForConsumerRead(string path)
    {
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static (bool Succeeded, string? ErrorCode) TryOpenForCallerRead(string path)
    {
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);
            return (true, null);
        }
        catch (FileNotFoundException)
        {
            return (false, "file-not-found");
        }
        catch (IOException ex)
        {
            return (false, $"0x{ex.HResult:X8}");
        }
    }
}
