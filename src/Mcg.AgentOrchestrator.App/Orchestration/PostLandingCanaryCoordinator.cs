using System.Security.Cryptography;
using System.Text;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal enum PostLandingCanaryDisposition
{
    NotTriggered,
    AlreadyCompleted,
    Passed,
    Failed
}

internal sealed class PostLandingCanaryCoordinator
{
    private readonly PostLandingCanaryConfiguration _configuration;
    private readonly IPostLandingCanaryRunner _runner;
    private readonly PostLandingCanaryEventStore _events;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Action<string> _progress;

    internal PostLandingCanaryCoordinator(
        PostLandingCanaryConfiguration configuration,
        IPostLandingCanaryRunner runner,
        PostLandingCanaryEventStore events,
        AcceptanceEngineCircuitBreaker circuitBreaker,
        Func<DateTimeOffset>? utcNow = null,
        Action<string>? progress = null)
    {
        _configuration = configuration;
        _runner = runner;
        _events = events;
        CircuitBreaker = circuitBreaker;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _progress = progress ?? Console.WriteLine;
    }

    internal AcceptanceEngineCircuitBreaker CircuitBreaker { get; }

    internal PostLandingCanaryDisposition HandleLanding(ConductorLandingReceipt landing)
    {
        if (!_configuration.Enabled)
        {
            return PostLandingCanaryDisposition.NotTriggered;
        }

        var trigger = PostLandingCanaryTrigger.Evaluate(
            landing.ChangedFiles,
            _configuration.AdditionalEnginePathPrefixes);
        if (!trigger.ShouldRun)
        {
            return PostLandingCanaryDisposition.NotTriggered;
        }

        if (string.IsNullOrWhiteSpace(landing.LandingSha))
        {
            throw new InvalidOperationException(
                "An acceptance-engine landing did not provide its landing SHA; refusing to suppress the canary.");
        }

        return RunAsync(
                new PostLandingCanaryRequest(landing.LandingSha, trigger.TriggeringPaths),
                CancellationToken.None)
            .GetAwaiter()
            .GetResult();
    }

    internal async Task<PostLandingCanaryDisposition> RunAsync(
        PostLandingCanaryRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.LandingSha);
        if (await _events.FindReceiptAsync(request.LandingSha, cancellationToken).ConfigureAwait(false) is { } existing)
        {
            return ReceiptDisposition(existing);
        }

        var queuedAt = _utcNow();
        var queuedPayload = Payload(
            request,
            detail: "Acceptance-engine landing queued for one post-landing canary cycle.");
        var queued = await _events.AppendOnceAsync(
            PostLandingCanaryEventKind.Queued,
            queuedPayload,
            EventId(request.LandingSha, "queued"),
            queuedAt,
            cancellationToken).ConfigureAwait(false);
        if (queued.Appended)
        {
            _progress(
                $"CANARY_GATE sha={request.LandingSha} result=queued paths={string.Join(",", request.TriggeringPaths)}");
        }

        while (true)
        {
            using (await PostLandingCanarySerializationLease
                       .AcquireAsync(_events.Identity, cancellationToken)
                       .ConfigureAwait(false))
            {
                if (await _events.FindReceiptAsync(request.LandingSha, cancellationToken)
                        .ConfigureAwait(false) is { } completed)
                {
                    return ReceiptDisposition(completed);
                }

                var earliest = await _events.FindEarliestUnreceiptedQueueAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (earliest is null ||
                    string.Equals(
                        earliest.Payload.LandingSha,
                        request.LandingSha,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return await RunOwnedAsync(request, cancellationToken).ConfigureAwait(false);
                }
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<PostLandingCanaryDisposition> RunOwnedAsync(
        PostLandingCanaryRequest request,
        CancellationToken cancellationToken)
    {
        var startedAt = _utcNow();
        await _events.AppendOnceAsync(
            PostLandingCanaryEventKind.Started,
            Payload(request, "Post-landing canary process started.", startedAt: startedAt),
            EventId(request.LandingSha, "started"),
            startedAt,
            cancellationToken).ConfigureAwait(false);
        _progress(
            $"CANARY_GATE sha={request.LandingSha} result=started paths={string.Join(",", request.TriggeringPaths)}");

        PostLandingCanaryOutcome outcome;
        using (var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            var runTask = _runner.RunAsync(request, timeoutCts.Token);
            try
            {
                outcome = await runTask
                    .WaitAsync(_configuration.Timeout, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                timeoutCts.Cancel();
                ObserveLateCompletion(runTask);
                outcome = PostLandingCanaryOutcome.Failed(
                    PostLandingCanaryFailureReason.Timeout,
                    $"canary exceeded hard timeout of {_configuration.TimeoutSeconds} seconds");
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                timeoutCts.Cancel();
                ObserveLateCompletion(runTask);
                outcome = PostLandingCanaryOutcome.Failed(
                    PostLandingCanaryFailureReason.Timeout,
                    $"canary exceeded hard timeout of {_configuration.TimeoutSeconds} seconds");
            }
            catch (Exception ex)
            {
                outcome = PostLandingCanaryOutcome.Failed(
                    PostLandingCanaryFailureReason.InfrastructureError,
                    $"{ex.GetType().Name}: {ex.Message}");
            }
        }

        var completedAt = _utcNow();
        var failureReason = FailureToken(outcome.FailureReason);
        var receiptKind = outcome.Green
            ? PostLandingCanaryEventKind.Passed
            : PostLandingCanaryEventKind.Failed;
        var receipt = await _events.AppendOnceAsync(
            receiptKind,
            Payload(
                request,
                outcome.Detail,
                failureReason,
                outcome.ExecutedTestCount,
                startedAt,
                completedAt),
            EventId(request.LandingSha, "receipt"),
            completedAt,
            CancellationToken.None).ConfigureAwait(false);
        var receiptReference = $"run-event:{receipt.Event.Sequence}";

        if (outcome.Green)
        {
            _progress(
                $"CANARY_GATE sha={request.LandingSha} result=passed executed={outcome.ExecutedTestCount} receipt={receiptReference}");
            return PostLandingCanaryDisposition.Passed;
        }

        var escalationPayload = Payload(
            request,
            $"CanaryGateFailure: {outcome.Detail}; receipt={receiptReference}",
            failureReason ?? "infrastructure-error",
            outcome.ExecutedTestCount,
            startedAt,
            completedAt);
        await _events.AppendOnceAsync(
            PostLandingCanaryEventKind.Escalated,
            escalationPayload,
            EventId(request.LandingSha, "escalation"),
            completedAt,
            CancellationToken.None).ConfigureAwait(false);
        _progress(
            $"CANARY_GATE sha={request.LandingSha} result=failed reason={failureReason ?? "infrastructure-error"} receipt={receiptReference} escalation=CanaryGateFailure");
        return PostLandingCanaryDisposition.Failed;
    }

    private static PostLandingCanaryDisposition ReceiptDisposition(PostLandingCanaryEvent receipt) =>
        receipt.Kind == PostLandingCanaryEventKind.Passed
            ? PostLandingCanaryDisposition.AlreadyCompleted
            : PostLandingCanaryDisposition.AlreadyCompleted;

    private static PostLandingCanaryEventPayload Payload(
        PostLandingCanaryRequest request,
        string detail,
        string? failureReason = null,
        int executedTestCount = 0,
        DateTimeOffset? startedAt = null,
        DateTimeOffset? completedAt = null) =>
        new(
            PostLandingCanaryEventPayload.CanaryTag,
            request.LandingSha,
            request.TriggeringPaths,
            failureReason,
            executedTestCount,
            detail,
            startedAt,
            completedAt);

    private static string EventId(string landingSha, string suffix) =>
        $"post-landing-canary:{landingSha.ToLowerInvariant()}:{suffix}";

    private static string? FailureToken(PostLandingCanaryFailureReason? reason) => reason switch
    {
        null => null,
        PostLandingCanaryFailureReason.Reject => "reject",
        PostLandingCanaryFailureReason.EmptyReceipt => "empty-receipt",
        PostLandingCanaryFailureReason.Timeout => "timeout",
        PostLandingCanaryFailureReason.InfrastructureError => "infrastructure-error",
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "Unknown canary failure reason.")
    };

    private static void ObserveLateCompletion(Task task) =>
        _ = task.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
}

internal static class PostLandingCanaryFactory
{
    internal static PostLandingCanaryCoordinator CreateDefault(
        OrchestratorWorkspace workspace,
        Action<string>? progress = null)
    {
        var events = CreateEventStore(workspace);
        var circuit = new AcceptanceEngineCircuitBreaker(events);
        return new PostLandingCanaryCoordinator(
            PostLandingCanaryConfiguration.Load(AppContext.BaseDirectory),
            new PostLandingCanaryRunner(
                workspace.ExecutionDirectory,
                Environment.GetEnvironmentVariable("MCG_ORCHESTRATOR_DOTNET_PATH")),
            events,
            circuit,
            progress: progress);
    }

    internal static AcceptanceEngineCircuitBreaker CreateCircuit(OrchestratorWorkspace workspace) =>
        new(CreateEventStore(workspace));

    private static PostLandingCanaryEventStore CreateEventStore(OrchestratorWorkspace workspace) =>
        new(
            new SqliteRunEventStore(
                workspace.RunEventStorePath,
                ensureSchema: !File.Exists(workspace.RunEventStorePath)),
            workspace.RunEventStorePath);
}

internal sealed class PostLandingCanarySerializationLease : IDisposable
{
    private readonly Semaphore _semaphore;
    private bool _disposed;

    private PostLandingCanarySerializationLease(Semaphore semaphore)
    {
        _semaphore = semaphore;
    }

    internal static async Task<PostLandingCanarySerializationLease> AcquireAsync(
        string identity,
        CancellationToken cancellationToken)
    {
        var hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(identity))))[..24];
        var name = $"mcg-post-landing-canary-{hash}";
        var semaphore = new Semaphore(1, 1, name);
        try
        {
            while (!semaphore.WaitOne(TimeSpan.FromMilliseconds(100)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Yield();
            }

            return new PostLandingCanarySerializationLease(semaphore);
        }
        catch
        {
            semaphore.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _semaphore.Release();
        _semaphore.Dispose();
    }
}
