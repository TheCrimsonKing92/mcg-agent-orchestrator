using System.Collections.Concurrent;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal enum AcceptanceEngineHealth
{
    Healthy,
    Pending,
    Unhealthy,
    Unavailable
}

internal enum AcceptanceEngineUnavailablePolicy
{
    FailOpen,
    FailClosed
}

internal sealed record AcceptanceEngineAcceptanceDecision(
    bool Allowed,
    AcceptanceEngineHealth Health,
    AcceptanceEngineUnavailablePolicy PolicyApplied,
    string Reason);

internal static class AcceptanceEngineAcceptanceGate
{
    // An unreadable safety circuit cannot vouch for the verifier. Pausing landings is reversible;
    // landing past a genuinely tripped circuit is not. Change this one value to change the default.
    internal const AcceptanceEngineUnavailablePolicy DefaultUnavailablePolicy =
        AcceptanceEngineUnavailablePolicy.FailClosed;

    internal static AcceptanceEngineAcceptanceDecision Decide(
        AcceptanceEngineHealth health,
        AcceptanceEngineUnavailablePolicy unavailablePolicy) =>
        health switch
        {
            AcceptanceEngineHealth.Healthy => Decision(
                allowed: true,
                health,
                unavailablePolicy,
                "acceptance engine state was read and is healthy"),
            AcceptanceEngineHealth.Pending => Decision(
                allowed: false,
                health,
                unavailablePolicy,
                "post-landing canary evaluation is pending"),
            AcceptanceEngineHealth.Unhealthy => Decision(
                allowed: false,
                health,
                unavailablePolicy,
                "acceptance engine circuit is tripped"),
            AcceptanceEngineHealth.Unavailable => unavailablePolicy switch
            {
                AcceptanceEngineUnavailablePolicy.FailOpen => Decision(
                    allowed: true,
                    health,
                    unavailablePolicy,
                    "acceptance engine state could not be checked, proceeding under policy FailOpen"),
                AcceptanceEngineUnavailablePolicy.FailClosed => Decision(
                    allowed: false,
                    health,
                    unavailablePolicy,
                    "acceptance engine state could not be checked, blocking under policy FailClosed"),
                _ => Decision(
                    allowed: false,
                    health,
                    unavailablePolicy,
                    "unrecognized unavailable-state policy; defaulting to deny")
            },
            _ => Decision(
                allowed: false,
                health,
                unavailablePolicy,
                "unrecognized acceptance-engine health; defaulting to deny")
        };

    private static AcceptanceEngineAcceptanceDecision Decision(
        bool allowed,
        AcceptanceEngineHealth health,
        AcceptanceEngineUnavailablePolicy unavailablePolicy,
        string detail) =>
        new(
            allowed,
            health,
            unavailablePolicy,
            $"health={health} policy={unavailablePolicy} outcome={(allowed ? "permitted" : "denied")}; {detail}");
}

internal sealed record AcceptanceEngineHealthSnapshot(
    AcceptanceEngineHealth Health,
    string? LandingSha,
    string? FailureReason,
    string? ReceiptReference,
    DateTimeOffset UpdatedAt,
    string? OperatorNote = null)
{
    internal static AcceptanceEngineHealthSnapshot Healthy(DateTimeOffset now, string? note = null) =>
        new(AcceptanceEngineHealth.Healthy, null, null, null, now, note);
}

internal enum PostLandingCanaryFailureReason
{
    Reject,
    EmptyReceipt,
    Timeout,
    InfrastructureError,
    EvaluatedArtifactFailure
}

internal enum PostLandingCanaryFaultDisposition
{
    ResourceBusy,
    EnvironmentFault,
    VerdictFailure
}

internal static class PostLandingCanaryFailureClassifier
{
    internal static PostLandingCanaryFaultDisposition Classify(Exception exception) => exception switch
    {
        DotnetBuildSlotsBusyException => PostLandingCanaryFaultDisposition.ResourceBusy,
        PostLandingCanaryEvaluationException => PostLandingCanaryFaultDisposition.VerdictFailure,
        _ => PostLandingCanaryFaultDisposition.EnvironmentFault
    };

    internal static PostLandingCanaryFaultDisposition Classify(PostLandingCanaryOutcome outcome) =>
        outcome.FailureReason switch
        {
            PostLandingCanaryFailureReason.Reject => PostLandingCanaryFaultDisposition.VerdictFailure,
            PostLandingCanaryFailureReason.EvaluatedArtifactFailure =>
                PostLandingCanaryFaultDisposition.VerdictFailure,
            PostLandingCanaryFailureReason.EmptyReceipt => PostLandingCanaryFaultDisposition.VerdictFailure,
            PostLandingCanaryFailureReason.Timeout or
            PostLandingCanaryFailureReason.InfrastructureError => PostLandingCanaryFaultDisposition.EnvironmentFault,
            null when outcome.Green => throw new InvalidOperationException(
                "A green canary outcome has no failure disposition."),
            _ => throw new ArgumentOutOfRangeException(
                nameof(outcome), outcome.FailureReason, "Unknown canary failure reason.")
        };
}

internal sealed class PostLandingCanaryEvaluationException : Exception
{
    internal PostLandingCanaryEvaluationException(string message)
        : base(message)
    {
    }
}

internal enum PostLandingCanaryEventKind
{
    Queued,
    Started,
    Deferred,
    Passed,
    Failed,
    Abandoned,
    Escalated,
    Cleared
}

internal sealed record PostLandingCanaryRequest(
    string LandingSha,
    IReadOnlyList<string> TriggeringPaths);

internal sealed record PostLandingCanaryOutcome(
    bool Green,
    PostLandingCanaryFailureReason? FailureReason,
    int ExecutedTestCount,
    string Detail)
{
    internal static PostLandingCanaryOutcome Passed(int executedTestCount, string detail) =>
        new(true, null, executedTestCount, detail);

    internal static PostLandingCanaryOutcome Failed(
        PostLandingCanaryFailureReason reason,
        string detail,
        int executedTestCount = 0) =>
        new(false, reason, executedTestCount, detail);
}

internal sealed record PostLandingCanaryEventPayload(
    string Tag,
    string? LandingSha,
    IReadOnlyList<string> TriggeringPaths,
    string? FailureReason,
    int ExecutedTestCount,
    string Detail,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    string? OperatorNote = null,
    int AttemptCount = 0,
    DateTimeOffset? NotBefore = null)
{
    internal const string CanaryTag = "canary";
}

internal sealed record PostLandingCanaryEvent(
    long Sequence,
    string EventId,
    DateTimeOffset OccurredAt,
    PostLandingCanaryEventKind Kind,
    PostLandingCanaryEventPayload Payload)
{
    internal bool IsReceipt =>
        Kind is PostLandingCanaryEventKind.Passed or PostLandingCanaryEventKind.Failed;

    internal bool IsTerminal => IsReceipt || Kind == PostLandingCanaryEventKind.Abandoned;
}

internal static class PostLandingCanaryEventIds
{
    internal static string Queued(string landingSha) => Build(landingSha, "queued");
    internal static string Started(string landingSha, int attempt) => Build(landingSha, $"started:{attempt}");
    internal static string Deferred(string landingSha, int attempt) => Build(landingSha, $"deferred:{attempt}");
    internal static string Receipt(string landingSha) => Build(landingSha, "receipt");
    internal static string Abandoned(string landingSha) => Build(landingSha, "abandoned");
    internal static string Escalation(string landingSha) => Build(landingSha, "escalation");

    private static string Build(string landingSha, string suffix) =>
        $"post-landing-canary:{landingSha.ToLowerInvariant()}:{suffix}";
}

internal interface IAcceptanceEngineStateReader
{
    Task<IReadOnlyList<PostLandingCanaryEvent>> ReadProjectionEventsAsync(
        CancellationToken cancellationToken = default);
}

internal sealed class PostLandingCanaryEventStore : IAcceptanceEngineStateReader
{
    private const int ReadPageSize = 5000;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly SqliteRunEventStore _store;

    internal PostLandingCanaryEventStore(SqliteRunEventStore store, string identity)
    {
        _store = store;
        Identity = Path.GetFullPath(identity);
    }

    internal string Identity { get; }

    internal async Task<(PostLandingCanaryEvent Event, bool Appended)> AppendOnceAsync(
        PostLandingCanaryEventKind kind,
        PostLandingCanaryEventPayload payload,
        string eventId,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var record = await _store.AppendAsync(
                new RunEventAppend(
                    RunEventTypes.PostLandingCanary,
                    GoalId: null,
                    Operation: OperationToken(kind),
                    Status: StatusToken(kind),
                    Detail: payload.Detail,
                    PayloadJson: JsonSerializer.Serialize(payload, JsonOptions),
                    OccurredAt: occurredAt,
                    EventId: eventId),
                cancellationToken).ConfigureAwait(false);
            return (Parse(record), true);
        }
        catch
        {
            var existing = await _store.ReadByEventIdAsync(eventId, cancellationToken).ConfigureAwait(false);
            if (existing is not null &&
                existing.EventType.Equals(RunEventTypes.PostLandingCanary, StringComparison.Ordinal))
            {
                return (Parse(existing), false);
            }

            throw;
        }
    }

    internal async Task<IReadOnlyList<PostLandingCanaryEvent>> ReadAllAsync(
        CancellationToken cancellationToken = default)
    {
        var afterSequence = 0L;
        var events = new List<PostLandingCanaryEvent>();
        while (true)
        {
            var page = await _store.ReadByTypeSinceAsync(
                RunEventTypes.PostLandingCanary,
                afterSequence: afterSequence,
                maxCount: ReadPageSize,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            foreach (var record in page)
            {
                events.Add(Parse(record));
            }

            if (page.Count < ReadPageSize)
            {
                return events;
            }

            afterSequence = page[^1].Sequence;
        }
    }

    internal async Task<PostLandingCanaryEvent?> FindReceiptAsync(
        string landingSha,
        CancellationToken cancellationToken = default)
    {
        var record = await _store
            .ReadByEventIdAsync(PostLandingCanaryEventIds.Receipt(landingSha), cancellationToken)
            .ConfigureAwait(false);
        return record is null ? null : Parse(record);
    }

    internal async Task<PostLandingCanaryEvent?> FindTerminalAsync(
        string landingSha,
        CancellationToken cancellationToken = default)
    {
        var receipt = await FindReceiptAsync(landingSha, cancellationToken).ConfigureAwait(false);
        if (receipt is not null)
        {
            return receipt;
        }

        var abandoned = await _store
            .ReadByEventIdAsync(PostLandingCanaryEventIds.Abandoned(landingSha), cancellationToken)
            .ConfigureAwait(false);
        return abandoned is null ? null : Parse(abandoned);
    }

    internal async Task<IReadOnlyList<PostLandingCanaryEvent>> ReadForLandingAsync(
        string landingSha,
        CancellationToken cancellationToken = default) =>
        (await ReadProjectionEventsAsync(cancellationToken).ConfigureAwait(false))
        .Where(item => string.Equals(
            item.Payload.LandingSha,
            landingSha,
            StringComparison.OrdinalIgnoreCase))
        .OrderBy(item => item.Sequence)
        .ToArray();

    public async Task<IReadOnlyList<PostLandingCanaryEvent>> ReadProjectionEventsAsync(
        CancellationToken cancellationToken = default)
    {
        var clearRecord = await _store
            .ReadLatestAsync(RunEventTypes.PostLandingCanary, "clear", cancellationToken)
            .ConfigureAwait(false);
        var afterSequence = clearRecord?.Sequence ?? 0L;
        var events = new List<PostLandingCanaryEvent>();
        if (clearRecord is not null)
        {
            events.Add(Parse(clearRecord));
        }

        while (true)
        {
            var page = await _store.ReadByTypeSinceAsync(
                RunEventTypes.PostLandingCanary,
                afterSequence: afterSequence,
                maxCount: ReadPageSize,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            events.AddRange(page.Select(Parse));
            if (page.Count < ReadPageSize)
            {
                var latestClearSequence = events
                    .Where(item => item.Kind == PostLandingCanaryEventKind.Cleared)
                    .Select(item => item.Sequence)
                    .DefaultIfEmpty(0)
                    .Max();
                return events
                    .Where(item => item.Sequence >= latestClearSequence)
                    .ToArray();
            }

            afterSequence = page[^1].Sequence;
        }
    }

    internal async Task<PostLandingCanaryEvent?> FindEarliestRunnableQueueAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var events = await ReadProjectionEventsAsync(cancellationToken).ConfigureAwait(false);
        var latestByLanding = events
            .Where(item => !string.IsNullOrWhiteSpace(item.Payload.LandingSha))
            .GroupBy(item => item.Payload.LandingSha!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(item => item.Sequence).First(),
                StringComparer.OrdinalIgnoreCase);
        var completed = events
            .Where(item => item.IsTerminal && !string.IsNullOrWhiteSpace(item.Payload.LandingSha))
            .Select(item => item.Payload.LandingSha!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return events
            .Where(item =>
                item.Kind == PostLandingCanaryEventKind.Queued &&
                !string.IsNullOrWhiteSpace(item.Payload.LandingSha) &&
                !completed.Contains(item.Payload.LandingSha!) &&
                latestByLanding.TryGetValue(item.Payload.LandingSha!, out var latest) &&
                (latest.Kind != PostLandingCanaryEventKind.Deferred ||
                 latest.Payload.NotBefore is null ||
                 latest.Payload.NotBefore <= now))
            .OrderBy(item => item.Sequence)
            .FirstOrDefault();
    }

    private static PostLandingCanaryEvent Parse(RunEventRecord record)
    {
        if (record.GoalId is not null)
        {
            throw new InvalidDataException(
                $"Canary run event {record.EventId} was incorrectly attributed to goal {record.GoalId}.");
        }

        if (record.Operation == "receipt" &&
            record.Status is not "Passed" and not "Failed")
        {
            throw new PostLandingCanaryUnparseableReceiptException(record.EventId, record.Status);
        }

        if (!TryParseKind(record.Operation, record.Status, out var kind))
        {
            throw new InvalidDataException(
                $"Canary run event {record.EventId} has unknown operation '{record.Operation}'.");
        }

        var payload = string.IsNullOrWhiteSpace(record.PayloadJson)
            ? null
            : JsonSerializer.Deserialize<PostLandingCanaryEventPayload>(record.PayloadJson, JsonOptions);
        return new PostLandingCanaryEvent(
            record.Sequence,
            record.EventId,
            record.OccurredAt,
            kind,
            payload ?? throw new InvalidDataException(
                $"Canary run event {record.EventId} has no typed payload."));
    }

    private static string OperationToken(PostLandingCanaryEventKind kind) => kind switch
    {
        PostLandingCanaryEventKind.Queued => "queued",
        PostLandingCanaryEventKind.Started => "started",
        PostLandingCanaryEventKind.Deferred => "deferred",
        PostLandingCanaryEventKind.Passed => "receipt",
        PostLandingCanaryEventKind.Failed => "receipt",
        PostLandingCanaryEventKind.Abandoned => "abandoned",
        PostLandingCanaryEventKind.Escalated => "escalation",
        PostLandingCanaryEventKind.Cleared => "clear",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown canary event kind.")
    };

    private static string StatusToken(PostLandingCanaryEventKind kind) => kind switch
    {
        PostLandingCanaryEventKind.Queued => "Pending",
        PostLandingCanaryEventKind.Started => "Running",
        PostLandingCanaryEventKind.Deferred => "CouldNotEvaluate",
        PostLandingCanaryEventKind.Passed => "Passed",
        PostLandingCanaryEventKind.Failed => "Failed",
        PostLandingCanaryEventKind.Abandoned => "Unverified",
        PostLandingCanaryEventKind.Escalated => "CanaryGateFailure",
        PostLandingCanaryEventKind.Cleared => "Cleared",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown canary event kind.")
    };

    private static bool TryParseKind(
        string? operation,
        string? status,
        out PostLandingCanaryEventKind kind)
    {
        kind = operation switch
        {
            "queued" => PostLandingCanaryEventKind.Queued,
            "started" => PostLandingCanaryEventKind.Started,
            "deferred" => PostLandingCanaryEventKind.Deferred,
            "receipt" when status?.Equals("Passed", StringComparison.Ordinal) == true =>
                PostLandingCanaryEventKind.Passed,
            "receipt" when status?.Equals("Failed", StringComparison.Ordinal) == true =>
                PostLandingCanaryEventKind.Failed,
            "abandoned" => PostLandingCanaryEventKind.Abandoned,
            "escalation" => PostLandingCanaryEventKind.Escalated,
            "clear" => PostLandingCanaryEventKind.Cleared,
            _ => default
        };
        if (operation != "receipt")
        {
            return operation is "queued" or "started" or "deferred" or "abandoned" or "escalation" or "clear";
        }

        return status is "Passed" or "Failed";
    }
}

internal sealed class PostLandingCanaryUnparseableReceiptException : Exception
{
    internal PostLandingCanaryUnparseableReceiptException(string eventId, string? status)
        : base($"Canary receipt {eventId} has unparseable status '{status ?? "<null>"}'.")
    {
    }
}

internal sealed class AcceptanceEngineCircuitBreaker
{
    internal const string OperatorItemCorrelationKey = "acceptance-engine:unhealthy-episode";
    internal const string StateUnavailableOperatorItemCorrelationKey = "acceptance-engine:canary-state-unavailable";
    internal const int StateReadMaxAttempts = 3;
    internal const int StateReadAttemptBusyTimeoutMilliseconds = 50;
    private static readonly TimeSpan StateReadRetryDelay = TimeSpan.FromMilliseconds(20);
    private static readonly TimeSpan StateReadTotalBudget = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan OperatorItemWriteBudget = TimeSpan.FromMilliseconds(250);
    private static readonly ConcurrentDictionary<string, byte> PendingStateUnavailableItemResolutions =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly PostLandingCanaryEventStore _events;
    private readonly IAcceptanceEngineStateReader _stateReader;
    private readonly ICollaborationItemStore? _operatorItems;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Action<string>? _stateUnavailableFallback;

    internal AcceptanceEngineCircuitBreaker(
        PostLandingCanaryEventStore events,
        ICollaborationItemStore? operatorItems = null,
        Func<DateTimeOffset>? utcNow = null,
        IAcceptanceEngineStateReader? stateReader = null,
        Action<string>? stateUnavailableFallback = null)
    {
        _events = events;
        _stateReader = stateReader ?? events;
        _operatorItems = operatorItems;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _stateUnavailableFallback = stateUnavailableFallback;
    }

    internal AcceptanceEngineHealthSnapshot Read(
        AcceptanceEngineUnavailablePolicy unavailablePolicy =
            AcceptanceEngineAcceptanceGate.DefaultUnavailablePolicy)
    {
        using var cancellation = new CancellationTokenSource();
        try
        {
            var snapshot = ReadWithRetryAsync(cancellation.Token)
                .WaitAsync(StateReadTotalBudget)
                .GetAwaiter()
                .GetResult();
            ScheduleStateUnavailableItemResolution();
            return snapshot;
        }
        catch (Exception ex)
        {
            cancellation.Cancel();
            var decision = AcceptanceEngineAcceptanceGate.Decide(
                AcceptanceEngineHealth.Unavailable,
                unavailablePolicy);
            RaiseStateUnavailableItem(ex, decision);
            return new AcceptanceEngineHealthSnapshot(
                AcceptanceEngineHealth.Unavailable,
                null,
                "state-unavailable",
                null,
                _utcNow(),
                $"canary state unavailable; {decision.Reason}; {ex.GetType().Name}: {ex.Message}");
        }
    }

    private async Task<AcceptanceEngineHealthSnapshot> ReadWithRetryAsync(
        CancellationToken cancellationToken)
    {
        Exception? lastFailure = null;
        for (var attempt = 1; attempt <= StateReadMaxAttempts; attempt++)
        {
            try
            {
                return await ReadAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                lastFailure = ex;
                if (attempt < StateReadMaxAttempts)
                {
                    await Task.Delay(StateReadRetryDelay, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        throw lastFailure ?? new InvalidOperationException("Acceptance-engine state read failed without an exception.");
    }

    internal async Task<AcceptanceEngineHealthSnapshot> ReadAsync(
        CancellationToken cancellationToken = default)
    {
        if (PostLandingCanaryEmergencyCircuit.TryRead(_events.Identity) is { } emergency)
        {
            return emergency;
        }

        var events = await _stateReader.ReadProjectionEventsAsync(cancellationToken).ConfigureAwait(false);
        var clear = events.FirstOrDefault(item => item.Kind == PostLandingCanaryEventKind.Cleared);
        var receipts = new Dictionary<string, PostLandingCanaryEvent>(StringComparer.OrdinalIgnoreCase);
        PostLandingCanaryEvent? failed = null;
        foreach (var evt in events)
        {
            if (!evt.IsReceipt || string.IsNullOrWhiteSpace(evt.Payload.LandingSha))
            {
                continue;
            }

            receipts[evt.Payload.LandingSha] = evt;
            if (evt.Kind == PostLandingCanaryEventKind.Failed)
            {
                failed = evt;
            }
        }

        if (failed is not null)
        {
            return new AcceptanceEngineHealthSnapshot(
                AcceptanceEngineHealth.Unhealthy,
                failed.Payload.LandingSha,
                failed.Payload.FailureReason ?? "infrastructure-error",
                $"run-event:{failed.Sequence}",
                failed.OccurredAt);
        }

        var pending = events
            .Where(item => !string.IsNullOrWhiteSpace(item.Payload.LandingSha))
            .GroupBy(item => item.Payload.LandingSha!, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(item => item.Sequence).First())
            .Where(item => item.Kind is PostLandingCanaryEventKind.Queued or PostLandingCanaryEventKind.Started)
            .OrderBy(item => item.Sequence)
            .FirstOrDefault();
        if (pending is not null)
        {
            return new AcceptanceEngineHealthSnapshot(
                AcceptanceEngineHealth.Pending,
                pending.Payload.LandingSha,
                null,
                null,
                pending.OccurredAt);
        }

        var deferred = events
            .Where(item => !string.IsNullOrWhiteSpace(item.Payload.LandingSha))
            .GroupBy(item => item.Payload.LandingSha!, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(item => item.Sequence).First())
            .Where(item => item.Kind == PostLandingCanaryEventKind.Deferred)
            .OrderBy(item => item.Sequence)
            .FirstOrDefault();
        if (deferred is not null)
        {
            return new AcceptanceEngineHealthSnapshot(
                AcceptanceEngineHealth.Healthy,
                deferred.Payload.LandingSha,
                deferred.Payload.FailureReason ?? "could-not-evaluate",
                $"run-event:{deferred.Sequence}",
                deferred.OccurredAt,
                $"canary deferred until {deferred.Payload.NotBefore:O}: {deferred.Payload.Detail}");
        }

        var latestPass = receipts.Values
            .Where(item => item.Kind == PostLandingCanaryEventKind.Passed)
            .OrderByDescending(item => item.Sequence)
            .FirstOrDefault();
        if (latestPass is not null)
        {
            return AcceptanceEngineHealthSnapshot.Healthy(
                latestPass.OccurredAt,
                $"canary passed for {latestPass.Payload.LandingSha}; receipt=run-event:{latestPass.Sequence}");
        }

        return AcceptanceEngineHealthSnapshot.Healthy(
            clear?.OccurredAt ?? _utcNow(),
            clear?.Payload.OperatorNote);
    }

    private void RaiseStateUnavailableItem(
        Exception exception,
        AcceptanceEngineAcceptanceDecision decision)
    {
        if (_operatorItems is null)
        {
            return;
        }

        var detail = $"{exception.GetType().Name}: {exception.Message}";
        try
        {
            using var cancellation = new CancellationTokenSource(OperatorItemWriteBudget);
            _operatorItems.RaiseAsync(
                    CollaborationItemType.Verify,
                    goalId: null,
                    subject: "Post-landing canary state is unavailable",
                    body:
                        $"Canary state could not be read after {StateReadMaxAttempts} attempts; {decision.Reason}.\n{detail}",
                    correlationKey: StateUnavailableOperatorItemCorrelationKey,
                    cancellationToken: cancellation.Token)
                .WaitAsync(OperatorItemWriteBudget)
                .GetAwaiter()
                .GetResult();
        }
        catch (Exception itemException)
        {
            var fallback =
                $"CANARY_GATE result=operator-item-error reason=state-unavailable " +
                $"decision=\"{decision.Reason}\" item-error={itemException.GetType().Name}: {itemException.Message}";
            try
            {
                _stateUnavailableFallback?.Invoke(fallback);
            }
            catch
            {
                // Stderr below remains the final non-database operator channel.
            }

            Console.Error.WriteLine(fallback);
            Console.Error.Flush();
        }
    }

    private void ScheduleStateUnavailableItemResolution()
    {
        if (_operatorItems is null ||
            !PendingStateUnavailableItemResolutions.TryAdd(_events.Identity, 0))
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                using var cancellation = new CancellationTokenSource(OperatorItemWriteBudget);
                await _operatorItems.TryResolveAsync(
                        StateUnavailableOperatorItemCorrelationKey,
                        "Post-landing canary state is readable again.",
                        cancellation.Token)
                    .WaitAsync(OperatorItemWriteBudget)
                    .ConfigureAwait(false);
            }
            catch
            {
                // Operator-item cleanup is advisory and cannot change circuit health.
            }
            finally
            {
                PendingStateUnavailableItemResolutions.TryRemove(_events.Identity, out _);
            }
        });
    }

    internal async Task RaiseUnhealthyEpisodeItemAsync(
        PostLandingCanaryEvent receipt,
        CancellationToken cancellationToken = default)
    {
        if (_operatorItems is null || receipt.Kind != PostLandingCanaryEventKind.Failed)
        {
            return;
        }

        var events = await _events.ReadProjectionEventsAsync(cancellationToken).ConfigureAwait(false);
        var repeatCount = events.Count(item => item.Kind == PostLandingCanaryEventKind.Failed);
        var receiptReference = $"run-event:{receipt.Sequence}";
        await _operatorItems.RaiseAsync(
            CollaborationItemType.Verify,
            goalId: null,
            subject: "Acceptance engine circuit is Unhealthy",
            body:
                $"Landing {receipt.Payload.LandingSha ?? "unknown"} failed post-landing evaluation. " +
                $"repeat-count={repeatCount}\n{receipt.Payload.Detail}\n{receiptReference}",
            correlationKey: OperatorItemCorrelationKey,
            cancellationToken).ConfigureAwait(false);
    }

    internal AcceptanceEngineHealthSnapshot Clear(string operatorNote)
    {
        if (string.IsNullOrWhiteSpace(operatorNote))
        {
            throw new ArgumentException("An operator clear note is required.", nameof(operatorNote));
        }

        var now = _utcNow();
        _events.AppendOnceAsync(
                PostLandingCanaryEventKind.Cleared,
                new PostLandingCanaryEventPayload(
                    PostLandingCanaryEventPayload.CanaryTag,
                    LandingSha: null,
                    TriggeringPaths: [],
                    FailureReason: null,
                    ExecutedTestCount: 0,
                    Detail: "Acceptance engine circuit explicitly cleared by operator.",
                    StartedAt: null,
                    CompletedAt: now,
                    OperatorNote: operatorNote.Trim()),
                $"post-landing-canary:clear:{Guid.NewGuid():N}",
                now)
            .GetAwaiter()
            .GetResult();
        PostLandingCanaryEmergencyCircuit.Clear(_events.Identity);
        if (_operatorItems is not null)
        {
            _operatorItems.TryResolveAsync(
                    OperatorItemCorrelationKey,
                    $"Acceptance engine circuit cleared: {operatorNote.Trim()}")
                .GetAwaiter()
                .GetResult();
        }
        return Read();
    }
}

internal static class PostLandingCanaryEmergencyCircuit
{
    private static readonly ConcurrentDictionary<string, AcceptanceEngineHealthSnapshot> Failures =
        new(StringComparer.OrdinalIgnoreCase);

    internal static AcceptanceEngineHealthSnapshot Signal(
        string identity,
        string? landingSha,
        string detail,
        DateTimeOffset now)
    {
        var snapshot = new AcceptanceEngineHealthSnapshot(
            AcceptanceEngineHealth.Unhealthy,
            string.IsNullOrWhiteSpace(landingSha) ? null : landingSha.Trim(),
            "infrastructure-error",
            null,
            now,
            detail);
        return Signal(identity, snapshot);
    }

    internal static AcceptanceEngineHealthSnapshot Signal(
        string identity,
        AcceptanceEngineHealthSnapshot snapshot)
    {
        Failures[Path.GetFullPath(identity)] = snapshot;
        return snapshot;
    }

    internal static AcceptanceEngineHealthSnapshot? TryRead(string identity) =>
        Failures.TryGetValue(Path.GetFullPath(identity), out var snapshot)
            ? snapshot
            : null;

    internal static void Clear(string identity) =>
        Failures.TryRemove(Path.GetFullPath(identity), out _);
}
