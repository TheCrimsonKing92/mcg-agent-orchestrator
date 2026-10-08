using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal enum PostLandingCanaryDisposition
{
    NotTriggered,
    AlreadyCompleted,
    Deferred,
    Abandoned,
    Passed,
    Failed
}

internal sealed class PostLandingCanaryCoordinator
{
    private const int RepeatedIdenticalFaultThreshold = 3;
    private const int ProgressDetailLimit = 500;

    private readonly PostLandingCanaryConfiguration _configuration;
    private readonly IPostLandingCanaryRunner _runner;
    private readonly PostLandingCanaryEventStore _events;
    private readonly ICollaborationItemStore? _operatorItems;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Func<TimeSpan, CancellationToken, Task>? _leaseRetryWait;
    private readonly Action<string> _progress;

    internal PostLandingCanaryCoordinator(
        PostLandingCanaryConfiguration configuration,
        IPostLandingCanaryRunner runner,
        PostLandingCanaryEventStore events,
        AcceptanceEngineCircuitBreaker circuitBreaker,
        ICollaborationItemStore? operatorItems = null,
        Func<DateTimeOffset>? utcNow = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Action<string>? progress = null,
        Func<TimeSpan, CancellationToken, Task>? leaseRetryWait = null)
    {
        _configuration = configuration;
        _runner = runner;
        _events = events;
        _operatorItems = operatorItems;
        CircuitBreaker = circuitBreaker;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _delay = delay ?? Task.Delay;
        _leaseRetryWait = leaseRetryWait;
        _progress = progress ?? Console.WriteLine;
    }

    internal AcceptanceEngineCircuitBreaker CircuitBreaker { get; }

    internal void ResumePending()
    {
        _ = ResumePendingAsync();
    }

    private async Task ResumePendingAsync()
    {
        try
        {
            var events = await _events.ReadProjectionEventsAsync(CancellationToken.None).ConfigureAwait(false);
            var pending = events
                .Where(item => !string.IsNullOrWhiteSpace(item.Payload.LandingSha))
                .GroupBy(item => item.Payload.LandingSha!, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.OrderByDescending(item => item.Sequence).First())
                .Where(item => item.Kind is
                    PostLandingCanaryEventKind.Queued or
                    PostLandingCanaryEventKind.Started or
                    PostLandingCanaryEventKind.Deferred)
                .OrderBy(item => item.Sequence)
                .ToArray();
            foreach (var item in pending)
            {
                ScheduleDeferredRetry(new PostLandingCanaryRequest(
                    item.Payload.LandingSha!,
                    item.Payload.TriggeringPaths));
            }
        }
        catch (Exception ex)
        {
            try
            {
                _progress($"CANARY_GATE result=resume-error detail={ex.GetType().Name}: {ex.Message}");
            }
            catch
            {
            }
        }
    }

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
        if (await _events.FindTerminalAsync(request.LandingSha, cancellationToken).ConfigureAwait(false) is { } existing)
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
            ReportProgress(
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
            TimeSpan? retryDelay = null;
            using (await PostLandingCanarySerializationLease
                       .AcquireAsync(_events.Identity, cancellationToken, _leaseRetryWait)
                       .ConfigureAwait(false))
            {
                if (await _events.FindTerminalAsync(request.LandingSha, cancellationToken)
                        .ConfigureAwait(false) is { } completed)
                {
                    return ReceiptDisposition(completed);
                }

                var landingEvents = await _events.ReadForLandingAsync(request.LandingSha, cancellationToken)
                    .ConfigureAwait(false);
                var latest = landingEvents.LastOrDefault();
                var now = _utcNow();
                if (latest is { Kind: PostLandingCanaryEventKind.Deferred, Payload.NotBefore: { } notBefore } &&
                    notBefore > now)
                {
                    retryDelay = notBefore - now;
                }

                var earliest = retryDelay is null
                    ? await _events.FindEarliestRunnableQueueAsync(now, cancellationToken).ConfigureAwait(false)
                    : null;
                if (earliest is null ||
                    retryDelay is null && string.Equals(
                        earliest.Payload.LandingSha,
                        request.LandingSha,
                        StringComparison.OrdinalIgnoreCase))
                {
                    if (retryDelay is null)
                    {
                        return await RunOwnedAsync(request, cancellationToken).ConfigureAwait(false);
                    }
                }
            }

            if (retryDelay is { } delay)
            {
                await _delay(delay, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<PostLandingCanaryDisposition> RunOwnedAsync(
        PostLandingCanaryRequest request,
        CancellationToken cancellationToken)
    {
        var priorEvents = await _events.ReadForLandingAsync(request.LandingSha, cancellationToken)
            .ConfigureAwait(false);
        var runOrdinal = priorEvents.Count(item => item.Kind == PostLandingCanaryEventKind.Started) + 1;
        var attempt = ConsumedAttemptCount(priorEvents) + 1;
        var startedAt = _utcNow();
        var started = await _events.AppendOnceAsync(
            PostLandingCanaryEventKind.Started,
            Payload(
                request,
                $"Post-landing canary attempt {attempt} started.",
                startedAt: startedAt,
                attemptCount: attempt),
            PostLandingCanaryEventIds.Started(request.LandingSha, runOrdinal),
            startedAt,
            cancellationToken).ConfigureAwait(false);
        if (!started.Appended)
        {
            return await DeferOrAbandonAsync(
                    request,
                    attempt,
                    runOrdinal,
                    PostLandingCanaryFaultDisposition.EnvironmentFault,
                    "InvalidOperationException: canary attempt start was already recorded without a terminal outcome.",
                    startedAt,
                    _utcNow(),
                    priorEvents)
                .ConfigureAwait(false);
        }
        ReportProgress(
            $"CANARY_GATE sha={request.LandingSha} result=started attempt={attempt} paths={string.Join(",", request.TriggeringPaths)}");

        PostLandingCanaryOutcome outcome;
        Exception? runnerException = null;
        using (var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            Task<PostLandingCanaryOutcome>? runTask = null;
            try
            {
                runTask = _runner.RunAsync(request, timeoutCts.Token);
                outcome = await runTask
                    .WaitAsync(_configuration.Timeout, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                timeoutCts.Cancel();
                if (runTask is not null)
                {
                    await ConfirmRunnerTerminatedAsync(runTask).ConfigureAwait(false);
                }
                outcome = PostLandingCanaryOutcome.Failed(
                    PostLandingCanaryFailureReason.Timeout,
                    $"canary exceeded hard timeout of {_configuration.TimeoutSeconds} seconds");
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                timeoutCts.Cancel();
                if (runTask is not null)
                {
                    await ConfirmRunnerTerminatedAsync(runTask).ConfigureAwait(false);
                }
                runnerException = ex;
                outcome = PostLandingCanaryOutcome.Failed(
                    PostLandingCanaryFailureReason.InfrastructureError,
                    $"runner cancelled internally: {ex.GetType().Name}: {ex.Message}");
            }
            catch (Exception ex)
            {
                runnerException = ex;
                outcome = PostLandingCanaryOutcome.Failed(
                    ex is PostLandingCanaryEvaluationException
                        ? PostLandingCanaryFailureReason.EvaluatedArtifactFailure
                        : PostLandingCanaryFailureReason.InfrastructureError,
                    $"{ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                if (cancellationToken.IsCancellationRequested && runTask is { IsCompleted: false })
                {
                    timeoutCts.Cancel();
                    await ConfirmRunnerTerminatedAsync(runTask).ConfigureAwait(false);
                }
            }
        }

        var completedAt = _utcNow();
        if (!outcome.Green)
        {
            var faultDisposition = runnerException is null
                ? PostLandingCanaryFailureClassifier.Classify(outcome)
                : PostLandingCanaryFailureClassifier.Classify(runnerException);
            if (faultDisposition != PostLandingCanaryFaultDisposition.VerdictFailure)
            {
                return await DeferOrAbandonAsync(
                        request,
                        attempt,
                        runOrdinal,
                        faultDisposition,
                        outcome.Detail,
                        startedAt,
                        completedAt,
                        priorEvents)
                    .ConfigureAwait(false);
            }
        }

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
                completedAt,
                slotResolution: outcome.SlotResolution),
            PostLandingCanaryEventIds.Receipt(request.LandingSha),
            completedAt,
            CancellationToken.None).ConfigureAwait(false);
        var receiptReference = $"run-event:{receipt.Event.Sequence}";

        if (outcome.Green)
        {
            if (_operatorItems is not null)
            {
                try
                {
                    await _operatorItems.TryResolveAsync(
                            PreconditionCorrelationKey(request.LandingSha),
                            "Post-landing canary precondition cleared and the landed SHA passed.",
                            CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    ReportProgress(
                        $"CANARY_GATE sha={request.LandingSha} result=operator-item-error " +
                        $"receipt={receiptReference} detail={ex.GetType().Name}: {ex.Message}");
                }
            }
            ReportProgress(
                $"CANARY_GATE sha={request.LandingSha} result=passed executed={outcome.ExecutedTestCount} receipt={receiptReference}");
            return PostLandingCanaryDisposition.Passed;
        }

        var verdictFailureReason = failureReason ?? throw new InvalidOperationException(
            "A failed canary verdict must carry a typed failure reason.");
        try
        {
            await CircuitBreaker.RaiseUnhealthyEpisodeItemAsync(receipt.Event, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            try
            {
                _progress(
                    $"CANARY_GATE sha={request.LandingSha} result=operator-item-error " +
                    $"receipt={receiptReference} detail={ex.GetType().Name}: {ex.Message}");
            }
            catch
            {
            }
        }

        var escalationPayload = Payload(
            request,
            $"CanaryGateFailure: {outcome.Detail}; receipt={receiptReference}",
            verdictFailureReason,
            outcome.ExecutedTestCount,
            startedAt,
            completedAt,
            slotResolution: outcome.SlotResolution);
        try
        {
            await _events.AppendOnceAsync(
                PostLandingCanaryEventKind.Escalated,
                escalationPayload,
                PostLandingCanaryEventIds.Escalation(request.LandingSha),
                completedAt,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            try
            {
                _progress(
                    $"CANARY_GATE sha={request.LandingSha} result=escalation-error " +
                    $"receipt={receiptReference} detail={ex.GetType().Name}: {ex.Message}");
            }
            catch
            {
            }
        }

        try
        {
            _progress(
                $"CANARY_GATE sha={request.LandingSha} result=failed reason={verdictFailureReason} receipt={receiptReference} escalation=CanaryGateFailure");
        }
        catch
        {
        }
        return PostLandingCanaryDisposition.Failed;
    }

    private async Task<PostLandingCanaryDisposition> DeferOrAbandonAsync(
        PostLandingCanaryRequest request,
        int attempt,
        int runOrdinal,
        PostLandingCanaryFaultDisposition faultDisposition,
        string detail,
        DateTimeOffset startedAt,
        DateTimeOffset completedAt,
        IReadOnlyList<PostLandingCanaryEvent> priorEvents)
    {
        var reason = FaultReasonToken(faultDisposition);
        var consumesAttempt = faultDisposition != PostLandingCanaryFaultDisposition.PreconditionFailure;
        var completedAttemptCount = consumesAttempt ? attempt : attempt - 1;
        var repeatedIdenticalFault = consumesAttempt && HasRepeatedIdenticalFault(priorEvents, reason, detail);
        if (consumesAttempt && (attempt >= _configuration.MaxAttempts || repeatedIdenticalFault))
        {
            var abandoned = await _events.AppendOnceAsync(
                    PostLandingCanaryEventKind.Abandoned,
                    Payload(
                        request,
                        detail,
                        reason,
                        startedAt: startedAt,
                        completedAt: completedAt,
                        attemptCount: completedAttemptCount),
                    PostLandingCanaryEventIds.Abandoned(request.LandingSha),
                    completedAt,
                    CancellationToken.None)
                .ConfigureAwait(false);
            var receiptReference = $"run-event:{abandoned.Event.Sequence}";
            if (_operatorItems is not null)
            {
                try
                {
                    await _operatorItems.RaiseAsync(
                            CollaborationItemType.Verify,
                            goalId: null,
                            subject: $"Post-landing canary never evaluated {request.LandingSha}",
                            body:
                                $"Landing {request.LandingSha} remains UNVERIFIED after {attempt} attempts" +
                                $"{(repeatedIdenticalFault ? " because the same fault repeated three times" : string.Empty)}.\n" +
                                $"{detail}\n{receiptReference}",
                            correlationKey: $"post-landing-canary:unverified:{request.LandingSha.ToLowerInvariant()}",
                            cancellationToken: CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    try
                    {
                        _progress(
                            $"CANARY_GATE sha={request.LandingSha} result=operator-item-error " +
                            $"receipt={receiptReference} detail={ex.GetType().Name}: {ex.Message}");
                    }
                    catch
                    {
                    }
                }
            }

            ReportProgress(
                $"CANARY_GATE sha={request.LandingSha} result=unverified attempts={attempt} reason={reason} " +
                $"detail={FormatProgressDetail(detail)} receipt={receiptReference}" +
                $"{(repeatedIdenticalFault ? " escalation=repeated-identical-fault" : string.Empty)}");
            return PostLandingCanaryDisposition.Abandoned;
        }

        var notBefore = completedAt + _configuration.RetryDelay(attempt);
        var deferred = await _events.AppendOnceAsync(
                PostLandingCanaryEventKind.Deferred,
                Payload(
                    request,
                    detail,
                    reason,
                    startedAt: startedAt,
                    completedAt: completedAt,
                    attemptCount: completedAttemptCount,
                    notBefore: notBefore),
                PostLandingCanaryEventIds.Deferred(request.LandingSha, runOrdinal),
                completedAt,
                CancellationToken.None)
            .ConfigureAwait(false);
        if (faultDisposition == PostLandingCanaryFaultDisposition.PreconditionFailure && _operatorItems is not null)
        {
            try
            {
                await _operatorItems.RaiseAsync(
                        CollaborationItemType.Verify,
                        goalId: null,
                        subject: $"Post-landing canary precondition requires operator action for {request.LandingSha}",
                        body:
                            $"Resolve the reported repository precondition, then allow the queued canary retry.\n" +
                            $"{detail}\nrun-event:{deferred.Event.Sequence}",
                        correlationKey: PreconditionCorrelationKey(request.LandingSha),
                        cancellationToken: CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                ReportProgress(
                    $"CANARY_GATE sha={request.LandingSha} result=operator-item-error " +
                    $"receipt=run-event:{deferred.Event.Sequence} detail={ex.GetType().Name}: {ex.Message}");
            }
        }
        ReportProgress(
            $"CANARY_GATE sha={request.LandingSha} result=deferred attempt={completedAttemptCount} reason={reason} " +
            $"detail={FormatProgressDetail(detail)} not-before={notBefore:O} receipt=run-event:{deferred.Event.Sequence}");
        ScheduleDeferredRetry(request);
        return PostLandingCanaryDisposition.Deferred;
    }

    private void ScheduleDeferredRetry(PostLandingCanaryRequest request) =>
        _ = ObserveDeferredRetryAsync(request);

    private void ReportProgress(string message)
    {
        try
        {
            _progress(message);
        }
        catch
        {
            // Progress sinks are advisory and cannot change a persisted canary outcome.
        }
    }

    private async Task ObserveDeferredRetryAsync(PostLandingCanaryRequest request)
    {
        try
        {
            await RunQueuedAsync(request, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            try
            {
                _progress(
                    $"CANARY_GATE sha={request.LandingSha} result=retry-scheduler-error " +
                    $"detail={ex.GetType().Name}: {ex.Message}");
            }
            catch
            {
            }
        }
    }

    private static PostLandingCanaryDisposition ReceiptDisposition(PostLandingCanaryEvent receipt) =>
        receipt.IsTerminal
            ? PostLandingCanaryDisposition.AlreadyCompleted
            : throw new InvalidOperationException($"Canary event {receipt.EventId} is not terminal.");

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
        var now = _utcNow();
        var durableLandingSha = string.IsNullOrWhiteSpace(landing.LandingSha)
            ? $"missing-sha-{Guid.NewGuid():N}"
            : landing.LandingSha.Trim();
        var detail = $"{exception.GetType().Name}: {exception.Message}";
        var faultDisposition = PostLandingCanaryFailureClassifier.Classify(exception);
        var reason = FaultReasonToken(faultDisposition);
        try
        {
            var request = new PostLandingCanaryRequest(durableLandingSha, landing.ChangedFiles);
            var priorEvents = _events.ReadForLandingAsync(durableLandingSha, CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            var runOrdinal = priorEvents.Count(item => item.Kind == PostLandingCanaryEventKind.Started) + 1;
            var attempt = ConsumedAttemptCount(priorEvents) + 1;
            return DeferOrAbandonAsync(
                    request,
                    attempt,
                    runOrdinal,
                    faultDisposition,
                    detail,
                    now,
                    now,
                    priorEvents)
                .GetAwaiter()
                .GetResult();
        }
        catch
        {
            // If the run-event store itself is unavailable, preserve the non-verdict invariant
            // and surface the unverified landing through the independent operator-item store.
        }

        try
        {
            _operatorItems?.RaiseAsync(
                    CollaborationItemType.Verify,
                    goalId: null,
                    subject: $"Post-landing canary could not evaluate {durableLandingSha}",
                    body: $"Landing {durableLandingSha} is UNVERIFIED.\n{detail}",
                    correlationKey: $"post-landing-canary:{reason}:{durableLandingSha.ToLowerInvariant()}",
                    cancellationToken: CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            _progress(
                $"CANARY_GATE sha={durableLandingSha} result=deferred reason={reason} " +
                $"detail={FormatProgressDetail(detail)}");
        }
        catch
        {
            // Progress sinks are advisory after main has advanced.
        }

        return PostLandingCanaryDisposition.Deferred;
    }

    private static PostLandingCanaryEventPayload Payload(
        PostLandingCanaryRequest request,
        string detail,
        string? failureReason = null,
        int executedTestCount = 0,
        DateTimeOffset? startedAt = null,
        DateTimeOffset? completedAt = null,
        int attemptCount = 0,
        DateTimeOffset? notBefore = null,
        string? slotResolution = null) =>
        new(
            PostLandingCanaryEventPayload.CanaryTag,
            request.LandingSha,
            request.TriggeringPaths,
            failureReason,
            executedTestCount,
            detail,
            startedAt,
            completedAt,
            AttemptCount: attemptCount,
            NotBefore: notBefore,
            SlotResolution: slotResolution);

    private static string? FailureToken(PostLandingCanaryFailureReason? reason) => reason switch
    {
        null => null,
        PostLandingCanaryFailureReason.Reject => "reject",
        PostLandingCanaryFailureReason.EmptyReceipt => "empty-receipt",
        PostLandingCanaryFailureReason.Timeout => "timeout",
        PostLandingCanaryFailureReason.InfrastructureError => "infrastructure-error",
        PostLandingCanaryFailureReason.EvaluatedArtifactFailure => "evaluated-artifact-failure",
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "Unknown canary failure reason.")
    };

    private static int ConsumedAttemptCount(IReadOnlyList<PostLandingCanaryEvent> events) =>
        events
            .Where(item => item.Kind is PostLandingCanaryEventKind.Deferred or PostLandingCanaryEventKind.Abandoned)
            .Select(item => item.Payload.AttemptCount)
            .DefaultIfEmpty(0)
            .Max();

    private static bool HasRepeatedIdenticalFault(
        IReadOnlyList<PostLandingCanaryEvent> priorEvents,
        string reason,
        string detail)
    {
        var signatureDetail = NormalizeFaultDetail(detail);
        var matchingCount = 1;
        foreach (var prior in priorEvents
                     .Where(item =>
                         item.Kind == PostLandingCanaryEventKind.Deferred &&
                         item.Payload.AttemptCount > 0)
                     .Reverse())
        {
            if (!string.Equals(prior.Payload.FailureReason, reason, StringComparison.Ordinal) ||
                !string.Equals(
                    NormalizeFaultDetail(prior.Payload.Detail),
                    signatureDetail,
                    StringComparison.Ordinal))
            {
                break;
            }

            matchingCount++;
            if (matchingCount >= RepeatedIdenticalFaultThreshold)
            {
                return true;
            }
        }

        return false;
    }

    private static string NormalizeFaultDetail(string detail) =>
        string.Join(' ', detail.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string FormatProgressDetail(string detail)
    {
        var normalized = NormalizeFaultDetail(detail);
        return normalized.Length <= ProgressDetailLimit
            ? normalized
            : normalized[..ProgressDetailLimit] + "...";
    }

    private static string FaultReasonToken(PostLandingCanaryFaultDisposition disposition) => disposition switch
    {
        PostLandingCanaryFaultDisposition.ResourceBusy => "resource-busy",
        PostLandingCanaryFaultDisposition.PreconditionFailure => "precondition-failure",
        PostLandingCanaryFaultDisposition.EnvironmentFault => "environment-fault",
        PostLandingCanaryFaultDisposition.UnexpectedFault => "unexpected-fault",
        PostLandingCanaryFaultDisposition.VerdictFailure => "verdict-failure",
        _ => throw new ArgumentOutOfRangeException(
            nameof(disposition), disposition, "Unknown canary fault disposition.")
    };

    private static string PreconditionCorrelationKey(string landingSha) =>
        $"post-landing-canary:precondition:{landingSha.ToLowerInvariant()}";

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
        Action<string>? progress = null) =>
        CreateDefault(workspace, progress, OrchestratorHome.ResolveForProcess());

    internal static PostLandingCanaryCoordinator CreateDefault(
        OrchestratorWorkspace workspace,
        Action<string>? progress,
        OrchestratorHome home,
        IPostLandingCanaryRunner? runner = null)
    {
        var events = CreateEventStore(workspace, ensureSchema: true);
        var operatorItems = CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory);
        var circuit = CreateCircuit(workspace, events, operatorItems, progress);
        var configuration = PostLandingCanaryConfiguration.Load(AppContext.BaseDirectory);
        if (!home.IsHome(workspace))
        {
            return new PostLandingCanaryCoordinator(
                configuration with { Enabled = false }, new NotHomeRunner(),
                events, circuit, operatorItems, progress: progress);
        }

        var coordinator = new PostLandingCanaryCoordinator(
            configuration,
            runner ?? new PostLandingCanaryRunner(
                workspace.ExecutionDirectory,
                Environment.GetEnvironmentVariable("MCG_ORCHESTRATOR_DOTNET_PATH"),
                logDirectory: workspace.LogDirectory,
                applicationBinaryResolver: new LandingAppBuildStoreCanaryBinaryResolver(
                    LandingAppBuildStore.ForRepository(workspace.ExecutionDirectory,
                        Environment.GetEnvironmentVariable("MCG_ORCHESTRATOR_DOTNET_PATH")))),
            events,
            circuit,
            operatorItems,
            progress: progress);
        coordinator.ResumePending();
        return coordinator;
    }

    private sealed class NotHomeRunner : IPostLandingCanaryRunner
    {
        public Task<PostLandingCanaryOutcome> RunAsync(
            PostLandingCanaryRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A non-home target cannot run the orchestrator canary.");
    }

    internal static AcceptanceEngineCircuitBreaker CreateCircuit(OrchestratorWorkspace workspace)
    {
        try
        {
            return new AcceptanceEngineCircuitBreaker(
                CreateEventStore(
                    workspace,
                    ensureSchema: !File.Exists(workspace.RunEventStorePath)),
                CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory),
                stateReader: CreateBoundedStateReader(workspace),
                stateUnavailableFallback: CreateStateUnavailableFallback(workspace));
        }
        catch
        {
            // Defer SQLite access so Read() can report Unavailable under the explicit landing policy.
            return new AcceptanceEngineCircuitBreaker(
                CreateEventStore(workspace, ensureSchema: false),
                CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory),
                stateReader: CreateBoundedStateReader(workspace),
                stateUnavailableFallback: CreateStateUnavailableFallback(workspace));
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
            var landingIdentity = string.IsNullOrWhiteSpace(landing.LandingSha)
                ? $"goal-{landing.GoalId}"
                : landing.LandingSha.Trim();
            try
            {
                CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory)
                    .RaiseAsync(
                        CollaborationItemType.Verify,
                        goalId: null,
                        subject: $"Post-landing canary could not initialize for {landingIdentity}",
                        body: $"Landing {landingIdentity} is UNVERIFIED.\n{detail}",
                        correlationKey: $"post-landing-canary:initialization:{landingIdentity.ToLowerInvariant()}")
                    .GetAwaiter()
                    .GetResult();
            }
            catch
            {
            }

            try
            {
                progress?.Invoke(
                    $"CANARY_GATE sha={landingIdentity} result=deferred " +
                    $"reason=initialization-error detail={ex.GetType().Name}");
            }
            catch
            {
            }

            return PostLandingCanaryDisposition.Deferred;
        }
    }

    internal static string? BuildMutationBlockReason(OrchestratorWorkspace workspace)
    {
        var snapshot = CreateCircuit(workspace).Read();
        var decision = AcceptanceEngineAcceptanceGate.Decide(
            snapshot.Health,
            AcceptanceEngineAcceptanceGate.DefaultUnavailablePolicy);
        return decision.Allowed
            ? null
            : $"{decision.Reason}; landing={snapshot.LandingSha ?? "unknown-sha"}; " +
              $"failure={snapshot.FailureReason ?? "canary-pending"}";
    }

    private static PostLandingCanaryEventStore CreateEventStore(
        OrchestratorWorkspace workspace,
        bool ensureSchema) =>
        new(
            new SqliteRunEventStore(
                workspace.RunEventStorePath,
                ensureSchema),
            workspace.RunEventStorePath);

    private static AcceptanceEngineCircuitBreaker CreateCircuit(
        OrchestratorWorkspace workspace,
        PostLandingCanaryEventStore events,
        ICollaborationItemStore operatorItems,
        Action<string>? progress) =>
        new(
            events,
            operatorItems,
            stateReader: CreateBoundedStateReader(workspace),
            stateUnavailableFallback: CreateStateUnavailableFallback(workspace, progress));

    private static PostLandingCanaryEventStore CreateBoundedStateReader(OrchestratorWorkspace workspace) =>
        new(
            new SqliteRunEventStore(
                workspace.RunEventStorePath,
                ensureSchema: false,
                busyTimeoutMilliseconds: AcceptanceEngineCircuitBreaker.StateReadAttemptBusyTimeoutMilliseconds,
                maxBusyRetries: 1),
            workspace.RunEventStorePath);

    private static Action<string> CreateStateUnavailableFallback(
        OrchestratorWorkspace workspace,
        Action<string>? progress = null) =>
        detail =>
        {
            try
            {
                progress?.Invoke(detail);
            }
            catch
            {
                // The required conduct event below is the durable fallback.
            }

            new ConductEventLogWriter(workspace.ConductEventsLogPath).AppendRequired(
                "acceptance-engine-state-unavailable",
                goalId: null,
                detail);
        };
}

internal sealed class PostLandingCanarySerializationLease : IDisposable
{
    internal static readonly TimeSpan RetryInterval = TimeSpan.FromMilliseconds(100);
    private readonly FileStream _lockStream;
    private bool _disposed;

    private PostLandingCanarySerializationLease(FileStream lockStream)
    {
        _lockStream = lockStream;
    }

    internal static async Task<PostLandingCanarySerializationLease> AcquireAsync(
        string identity,
        CancellationToken cancellationToken,
        Func<TimeSpan, CancellationToken, Task>? retryWait = null)
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
                await (retryWait ?? Task.Delay)(RetryInterval, cancellationToken).ConfigureAwait(false);
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
