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
        => LaunchLandingAsync(landing).GetAwaiter().GetResult();

    internal Task<PostLandingCanaryDisposition> LaunchLandingAsync(ConductorLandingReceipt landing)
    {
        try
        {
            if (!_configuration.Enabled)
            {
                return Task.FromResult(PostLandingCanaryDisposition.NotTriggered);
            }

            var trigger = PostLandingCanaryTrigger.Evaluate(
                landing.ChangedFiles,
                _configuration.AdditionalEnginePathPrefixes);
            if (!trigger.ShouldRun)
            {
                return Task.FromResult(PostLandingCanaryDisposition.NotTriggered);
            }

            if (string.IsNullOrWhiteSpace(landing.LandingSha))
            {
                throw new InvalidOperationException(
                    "An acceptance-engine landing did not provide its landing SHA; refusing to suppress the canary.");
            }

            var request = new PostLandingCanaryRequest(landing.LandingSha, trigger.TriggeringPaths);
            var queued = EnsureQueuedAsync(request, CancellationToken.None).GetAwaiter().GetResult();
            var task = queued.ExistingReceipt is { } existing
                ? Task.FromResult(ReceiptDisposition(existing))
                : Task.Run(() => RunQueuedAsync(request, CancellationToken.None));
            return ObservePostLandingTaskAsync(task, landing);
        }
        catch (Exception ex)
        {
            return Task.FromResult(RecordPostLandingFailure(landing, ex));
        }
    }

    internal async Task<PostLandingCanaryDisposition> RunAsync(
        PostLandingCanaryRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.LandingSha);
        var queued = await EnsureQueuedAsync(request, cancellationToken).ConfigureAwait(false);
        return queued.ExistingReceipt is { } existing
            ? ReceiptDisposition(existing)
            : await RunQueuedAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private async Task<(PostLandingCanaryEvent? ExistingReceipt, bool Appended)> EnsureQueuedAsync(
        PostLandingCanaryRequest request,
        CancellationToken cancellationToken)
    {
        if (await _events.FindReceiptAsync(request.LandingSha, cancellationToken).ConfigureAwait(false) is { } existing)
        {
            return (existing, false);
        }

        var queuedAt = _utcNow();
        var queuedPayload = Payload(
            request,
            detail: "Acceptance-engine landing queued for one post-landing canary cycle.");
        var queued = await _events.AppendOnceAsync(
            PostLandingCanaryEventKind.Queued,
            queuedPayload,
            PostLandingCanaryEventIds.Queued(request.LandingSha),
            queuedAt,
            cancellationToken).ConfigureAwait(false);
        if (queued.Appended)
        {
            _progress(
                $"CANARY_GATE sha={request.LandingSha} result=queued paths={string.Join(",", request.TriggeringPaths)}");
        }

        return (null, queued.Appended);
    }

    private async Task<PostLandingCanaryDisposition> RunQueuedAsync(
        PostLandingCanaryRequest request,
        CancellationToken cancellationToken)
    {
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
            PostLandingCanaryEventIds.Started(request.LandingSha),
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
                await ConfirmRunnerTerminatedAsync(runTask).ConfigureAwait(false);
                outcome = PostLandingCanaryOutcome.Failed(
                    PostLandingCanaryFailureReason.Timeout,
                    $"canary exceeded hard timeout of {_configuration.TimeoutSeconds} seconds");
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                timeoutCts.Cancel();
                await ConfirmRunnerTerminatedAsync(runTask).ConfigureAwait(false);
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
            finally
            {
                if (cancellationToken.IsCancellationRequested && !runTask.IsCompleted)
                {
                    timeoutCts.Cancel();
                    await ConfirmRunnerTerminatedAsync(runTask).ConfigureAwait(false);
                }
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
            PostLandingCanaryEventIds.Receipt(request.LandingSha),
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
            PostLandingCanaryEventIds.Escalation(request.LandingSha),
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

    private async Task<PostLandingCanaryDisposition> ObservePostLandingTaskAsync(
        Task<PostLandingCanaryDisposition> task,
        ConductorLandingReceipt landing)
    {
        try
        {
            return await task.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return RecordPostLandingFailure(landing, ex);
        }
    }

    private PostLandingCanaryDisposition RecordPostLandingFailure(
        ConductorLandingReceipt landing,
        Exception exception)
    {
        AcceptanceEngineHealthSnapshot? snapshot = null;
        try
        {
            snapshot = CircuitBreaker.SignalPostLandingFailure(
                landing.LandingSha,
                landing.ChangedFiles,
                exception);
        }
        catch
        {
            // This is a post-main safety boundary. Reporting failure must never escape.
        }

        try
        {
            _progress(
                $"CANARY_GATE sha={landing.LandingSha ?? "unknown"} result=failed " +
                $"reason=infrastructure-error receipt={snapshot?.ReceiptReference ?? "in-memory-emergency-circuit"} " +
                $"detail={exception.GetType().Name}");
        }
        catch
        {
            // Progress sinks are advisory after main has advanced.
        }

        return PostLandingCanaryDisposition.Failed;
    }

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

    private static string? FailureToken(PostLandingCanaryFailureReason? reason) => reason switch
    {
        null => null,
        PostLandingCanaryFailureReason.Reject => "reject",
        PostLandingCanaryFailureReason.EmptyReceipt => "empty-receipt",
        PostLandingCanaryFailureReason.Timeout => "timeout",
        PostLandingCanaryFailureReason.InfrastructureError => "infrastructure-error",
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "Unknown canary failure reason.")
    };

    private static async Task ConfirmRunnerTerminatedAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            // The timeout receipt is authoritative, but serialization is held until the
            // runner has completed its kill-and-wait path.
        }
    }
}

internal static class PostLandingCanaryFactory
{
    internal static PostLandingCanaryCoordinator CreateDefault(
        OrchestratorWorkspace workspace,
        Action<string>? progress = null)
    {
        var events = CreateEventStore(workspace, ensureSchema: true);
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

    internal static AcceptanceEngineCircuitBreaker CreateCircuit(OrchestratorWorkspace workspace)
    {
        try
        {
            return new AcceptanceEngineCircuitBreaker(CreateEventStore(
                workspace,
                ensureSchema: !File.Exists(workspace.RunEventStorePath)));
        }
        catch when (PostLandingCanaryEmergencyCircuit.TryRead(workspace.RunEventStorePath) is not null)
        {
            // The emergency signal must remain readable even when the durable store cannot
            // be opened. Defer all SQLite access; the circuit checks emergency state first.
            return new AcceptanceEngineCircuitBreaker(CreateEventStore(workspace, ensureSchema: false));
        }
    }

    internal static PostLandingCanaryDisposition HandleLandingAfterMainAdvanced(
        OrchestratorWorkspace workspace,
        ConductorLandingReceipt landing,
        Action<string>? progress = null)
    {
        try
        {
            return CreateDefault(workspace, progress).HandleLanding(landing);
        }
        catch (Exception ex)
        {
            var detail =
                $"Post-landing canary initialization failed after main advanced: {ex.GetType().Name}: {ex.Message}";
            try
            {
                PostLandingCanaryEmergencyCircuit.Signal(
                    workspace.RunEventStorePath,
                    landing.LandingSha,
                    detail,
                    DateTimeOffset.UtcNow);
            }
            catch
            {
            }

            try
            {
                progress?.Invoke(
                    $"CANARY_GATE sha={landing.LandingSha ?? "unknown"} result=failed " +
                    $"reason=infrastructure-error receipt=in-memory-emergency-circuit detail={ex.GetType().Name}");
            }
            catch
            {
            }

            return PostLandingCanaryDisposition.Failed;
        }
    }

    internal static string? BuildMutationBlockReason(OrchestratorWorkspace workspace)
    {
        var snapshot = CreateCircuit(workspace).Read();
        return snapshot.AllowsAcceptance
            ? null
            : $"acceptance engine circuit is {snapshot.Health} for {snapshot.LandingSha ?? "unknown-sha"}; " +
              $"reason={snapshot.FailureReason ?? "canary-pending"}";
    }

    private static PostLandingCanaryEventStore CreateEventStore(
        OrchestratorWorkspace workspace,
        bool ensureSchema) =>
        new(
            new SqliteRunEventStore(
                workspace.RunEventStorePath,
                ensureSchema),
            workspace.RunEventStorePath);
}

internal sealed class PostLandingCanarySerializationLease : IDisposable
{
    private readonly FileStream _lockStream;
    private bool _disposed;

    private PostLandingCanarySerializationLease(FileStream lockStream)
    {
        _lockStream = lockStream;
    }

    internal static async Task<PostLandingCanarySerializationLease> AcquireAsync(
        string identity,
        CancellationToken cancellationToken)
    {
        var lockPath = Path.GetFullPath(identity) + ".post-landing-canary.lock";
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var stream = new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.None);
                return new PostLandingCanarySerializationLease(stream);
            }
            catch (IOException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lockStream.Dispose();
    }
}
