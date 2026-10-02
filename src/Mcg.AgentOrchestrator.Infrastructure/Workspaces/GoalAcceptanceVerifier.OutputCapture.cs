namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed partial class GoalAcceptanceVerifier
{
    private static readonly TimeSpan DefaultCapturePublicationInterval = TimeSpan.FromSeconds(1);

    internal static async Task<CaptureLimitResult> DrainCappedCaptureAsync(
        Stream source,
        string path,
        long limitBytes,
        Func<DateTimeOffset> utcNow,
        Action? onLimitReached,
        CancellationToken cancellationToken,
        TimeSpan? publicationInterval = null,
        Func<TimeSpan, CancellationToken, Task>? publicationDelaySource = null)
    {
        var buffer = new byte[64 * 1024];
        long writtenBytes = 0;
        long persistedBytes = 0;
        var limitReached = false;
        var publicationPending = false;
        CancellationTokenSource? publicationDelayCancellation = null;
        Task? publicationDelay = null;
        await using (var destination = new FileStream(
            path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete,
            buffer.Length,
            useAsync: true))
        {
            try
            {
                async Task PublishPendingOutputAsync()
                {
                    await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
                    publicationPending = false;
                    publicationDelayCancellation?.Cancel();
                    publicationDelayCancellation?.Dispose();
                    publicationDelayCancellation = null;
                    publicationDelay = null;
                }

                while (true)
                {
                    if (publicationPending && publicationDelay?.IsCompleted == true)
                    {
                        await PublishPendingOutputAsync().ConfigureAwait(false);
                    }

                    int read;
                    Task<int>? readTask = null;
                    try
                    {
                        readTask = source.ReadAsync(buffer, cancellationToken).AsTask();
                        if (publicationPending &&
                            publicationDelay is not null &&
                            !readTask.IsCompleted &&
                            ReferenceEquals(
                                 await Task.WhenAny(readTask, publicationDelay).ConfigureAwait(false),
                                 publicationDelay))
                        {
                            await PublishPendingOutputAsync().ConfigureAwait(false);
                        }

                        read = await readTask.ConfigureAwait(false);
                    }
                    catch (IOException ex) when (GoalAcceptanceVerifierCaptureCustody.IsClosedPipe(ex))
                    {
                        break;
                    }
                    catch
                    {
                        if (readTask is not null)
                        {
                            ObservePotentialTaskFailure(readTask);
                        }

                        throw;
                    }
                    if (read == 0)
                        break;

                    writtenBytes = writtenBytes > long.MaxValue - read
                        ? long.MaxValue
                        : writtenBytes + read;
                    var persist = checked((int)Math.Min(read, Math.Max(0, limitBytes - persistedBytes)));
                    if (persist > 0)
                    {
                        await destination.WriteAsync(buffer.AsMemory(0, persist), cancellationToken)
                            .ConfigureAwait(false);
                        persistedBytes += persist;
                        if (!publicationPending)
                        {
                            publicationPending = true;
                            var effectivePublicationInterval = publicationInterval ?? DefaultCapturePublicationInterval;
                            if (effectivePublicationInterval > TimeSpan.Zero &&
                                effectivePublicationInterval != Timeout.InfiniteTimeSpan)
                            {
                                publicationDelayCancellation =
                                    CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                                publicationDelay = publicationDelaySource is null
                                    ? Task.Delay(effectivePublicationInterval, publicationDelayCancellation.Token)
                                    : publicationDelaySource(effectivePublicationInterval, publicationDelayCancellation.Token);
                            }
                        }
                    }

                    if (!limitReached && writtenBytes >= limitBytes)
                    {
                        limitReached = true;
                        onLimitReached?.Invoke();
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // A descendant may inherit the pipe after the shell exits. Cancellation ends the
                // bounded drain; bytes already observed remain valid capture evidence.
            }
            catch (Exception ex) when (
                cancellationToken.IsCancellationRequested &&
                ex is IOException or ObjectDisposedException)
            {
                // Closing the pipe reader is the reliable cancellation mechanism for synchronous
                // redirected FileStreams on Windows; it can surface either exception.
            }
            finally
            {
                if (publicationDelayCancellation is not null)
                {
                    publicationDelayCancellation.Cancel();
                    publicationDelayCancellation.Dispose();
                }
            }

            await destination.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        }

        if (limitReached)
        {
            await GoalAcceptanceVerifierCaptureCustody.FinalizeCappedCaptureAsync(
                path,
                limitBytes,
                writtenBytes,
                utcNow()).ConfigureAwait(false);
        }

        return new CaptureLimitResult(path, writtenBytes, limitReached);
    }

    private static void ObservePotentialTaskFailure(Task task)
    {
        if (task.IsCompleted)
        {
            _ = task.Exception;
            return;
        }

        _ = task.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    }
}
