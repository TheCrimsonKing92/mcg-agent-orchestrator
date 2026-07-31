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

internal sealed class PostLandingCanaryEventStore
{
    private const int ReadPageSize = 5000;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly IRunEventStore _store;

    internal PostLandingCanaryEventStore(IRunEventStore store, string identity)
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
            var existing = (await ReadAllAsync(cancellationToken).ConfigureAwait(false))
                .SingleOrDefault(item => item.EventId.Equals(eventId, StringComparison.Ordinal));
            if (existing is not null)
            {
                return (existing, false);
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
            var page = await _store.ReadSinceAsync(
                afterSequence,
                goalId: null,
                maxCount: ReadPageSize,
                cancellationToken).ConfigureAwait(false);
            foreach (var record in page)
            {
                if (record.EventType.Equals(RunEventTypes.PostLandingCanary, StringComparison.Ordinal))
                {
                    events.Add(Parse(record));
                }
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
        CancellationToken cancellationToken = default) =>
        (await ReadAllAsync(cancellationToken).ConfigureAwait(false))
            .Where(item =>
                item.IsReceipt &&
                string.Equals(item.Payload.LandingSha, landingSha, StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => item.Sequence)
            .FirstOrDefault();

    internal async Task<PostLandingCanaryEvent?> FindEarliestUnreceiptedQueueAsync(
        CancellationToken cancellationToken = default)
    {
        var events = await ReadAllAsync(cancellationToken).ConfigureAwait(false);
        var lastClearSequence = events
            .Where(item => item.Kind == PostLandingCanaryEventKind.Cleared)
            .Select(item => item.Sequence)
            .DefaultIfEmpty(0)
            .Max();
        var receipted = events
            .Where(item => item.IsReceipt && !string.IsNullOrWhiteSpace(item.Payload.LandingSha))
            .Select(item => item.Payload.LandingSha!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return events
            .Where(item =>
                item.Kind == PostLandingCanaryEventKind.Queued &&
                item.Sequence > lastClearSequence &&
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
        var health = AcceptanceEngineHealthSnapshot.Healthy(_utcNow());
        foreach (var evt in await _events.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            switch (evt.Kind)
            {
                case PostLandingCanaryEventKind.Queued:
                case PostLandingCanaryEventKind.Started:
                    if (health.Health != AcceptanceEngineHealth.Unhealthy)
                    {
                        health = new AcceptanceEngineHealthSnapshot(
                            AcceptanceEngineHealth.Pending,
                            evt.Payload.LandingSha,
                            null,
                            null,
                            evt.OccurredAt);
                    }
                    break;

                case PostLandingCanaryEventKind.Passed:
                    if (health.Health != AcceptanceEngineHealth.Unhealthy)
                    {
                        health = AcceptanceEngineHealthSnapshot.Healthy(
                            evt.OccurredAt,
                            $"canary passed for {evt.Payload.LandingSha}; receipt=run-event:{evt.Sequence}");
                    }
                    break;

                case PostLandingCanaryEventKind.Failed:
                    health = new AcceptanceEngineHealthSnapshot(
                        AcceptanceEngineHealth.Unhealthy,
                        evt.Payload.LandingSha,
                        evt.Payload.FailureReason ?? "infrastructure-error",
                        $"run-event:{evt.Sequence}",
                        evt.OccurredAt);
                    break;

                case PostLandingCanaryEventKind.Cleared:
                    health = AcceptanceEngineHealthSnapshot.Healthy(
                        evt.OccurredAt,
                        evt.Payload.OperatorNote);
                    break;

                case PostLandingCanaryEventKind.Escalated:
                    break;

                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        return health;
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
