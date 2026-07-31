using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal enum AcceptanceEngineHealth
{
    Healthy,
    Pending,
    Unhealthy
}

internal sealed record AcceptanceEngineHealthSnapshot(
    AcceptanceEngineHealth Health,
    string? LandingSha,
    string? FailureReason,
    string? ReceiptReference,
    DateTimeOffset UpdatedAt,
    string? OperatorNote = null)
{
    internal bool AllowsAcceptance => Health == AcceptanceEngineHealth.Healthy;

    internal static AcceptanceEngineHealthSnapshot Healthy(DateTimeOffset now, string? note = null) =>
        new(AcceptanceEngineHealth.Healthy, null, null, null, now, note);
}

internal enum PostLandingCanaryFailureReason
{
    Reject,
    EmptyReceipt,
    Timeout,
    InfrastructureError
}

internal enum PostLandingCanaryEventKind
{
    Queued,
    Started,
    Passed,
    Failed,
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
    string? OperatorNote = null)
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
}

internal static class PostLandingCanaryEventIds
{
    internal static string Queued(string landingSha) => Build(landingSha, "queued");
    internal static string Started(string landingSha) => Build(landingSha, "started");
    internal static string Receipt(string landingSha) => Build(landingSha, "receipt");
    internal static string Escalation(string landingSha) => Build(landingSha, "escalation");

    private static string Build(string landingSha, string suffix) =>
        $"post-landing-canary:{landingSha.ToLowerInvariant()}:{suffix}";
}

internal sealed class PostLandingCanaryEventStore
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

    internal async Task<IReadOnlyList<PostLandingCanaryEvent>> ReadProjectionEventsAsync(
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

    internal async Task<PostLandingCanaryEvent?> FindEarliestUnreceiptedQueueAsync(
        CancellationToken cancellationToken = default)
    {
        var events = await ReadProjectionEventsAsync(cancellationToken).ConfigureAwait(false);
        var receipted = events
            .Where(item => item.IsReceipt && !string.IsNullOrWhiteSpace(item.Payload.LandingSha))
            .Select(item => item.Payload.LandingSha!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return events
            .Where(item =>
                item.Kind == PostLandingCanaryEventKind.Queued &&
                !string.IsNullOrWhiteSpace(item.Payload.LandingSha) &&
                !receipted.Contains(item.Payload.LandingSha!))
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
        PostLandingCanaryEventKind.Passed => "receipt",
        PostLandingCanaryEventKind.Failed => "receipt",
        PostLandingCanaryEventKind.Escalated => "escalation",
        PostLandingCanaryEventKind.Cleared => "clear",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown canary event kind.")
    };

    private static string StatusToken(PostLandingCanaryEventKind kind) => kind switch
    {
        PostLandingCanaryEventKind.Queued => "Pending",
        PostLandingCanaryEventKind.Started => "Running",
        PostLandingCanaryEventKind.Passed => "Passed",
        PostLandingCanaryEventKind.Failed => "Failed",
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
            "receipt" when status?.Equals("Passed", StringComparison.Ordinal) == true =>
                PostLandingCanaryEventKind.Passed,
            "receipt" => PostLandingCanaryEventKind.Failed,
            "escalation" => PostLandingCanaryEventKind.Escalated,
            "clear" => PostLandingCanaryEventKind.Cleared,
            _ => default
        };
        if (operation != "receipt")
        {
            return operation is "queued" or "started" or "escalation" or "clear";
        }

        return true;
    }
}

internal sealed class AcceptanceEngineCircuitBreaker
{
    private readonly PostLandingCanaryEventStore _events;
    private readonly Func<DateTimeOffset> _utcNow;

    internal AcceptanceEngineCircuitBreaker(
        PostLandingCanaryEventStore events,
        Func<DateTimeOffset>? utcNow = null)
    {
        _events = events;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    internal AcceptanceEngineHealthSnapshot Read()
    {
        try
        {
            return ReadAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            return new AcceptanceEngineHealthSnapshot(
                AcceptanceEngineHealth.Unhealthy,
                null,
                "infrastructure-error",
                null,
                _utcNow(),
                $"canary event store unreadable: {ex.GetType().Name}");
        }
    }

    internal async Task<AcceptanceEngineHealthSnapshot> ReadAsync(
        CancellationToken cancellationToken = default)
    {
        var events = await _events.ReadProjectionEventsAsync(cancellationToken).ConfigureAwait(false);
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
            .Where(item =>
                item.Kind is PostLandingCanaryEventKind.Queued or PostLandingCanaryEventKind.Started &&
                !string.IsNullOrWhiteSpace(item.Payload.LandingSha) &&
                !receipts.ContainsKey(item.Payload.LandingSha!))
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
        return Read();
    }
}
