using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed class ConductorLifecycleRecorder(
    IRunEventStore store,
    Func<DateTimeOffset>? utcNow = null,
    Func<string>? generationId = null,
    Func<IReadOnlyList<WorkerAdoptionCensusRow>>? readAdoptionCensus = null)
{
    private readonly Func<DateTimeOffset> _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    private readonly Func<string> _generationId = generationId ?? (() => Guid.NewGuid().ToString("N"));

    public ConductorLifecycleSession Start(
        string policy,
        string? goalId,
        DateTimeOffset startedAt,
        int? maxIterations,
        TimeSpan? maxDuration)
    {
        var generation = _generationId();
        Append(
            generation,
            operation: "start",
            status: "running",
            goalId,
            startedAt,
            ticks: 0,
            detail: $"policy={policy} maxIterations={maxIterations?.ToString() ?? "none"} maxDurationSeconds={(maxDuration.HasValue ? ((int)maxDuration.Value.TotalSeconds).ToString() : "none")}");
        if (readAdoptionCensus is not null)
        {
            IReadOnlyList<WorkerAdoptionCensusRow> rows;
            try
            {
                rows = readAdoptionCensus();
            }
            catch (Exception exception)
            {
                Append(generation, "adoption-census", "unavailable", null, startedAt, 0,
                    $"exception={exception.GetType().Name}");
                return new ConductorLifecycleSession(this, generation, goalId, startedAt);
            }

            foreach (var row in rows)
                Append(generation, "adoption-census", row.Verdict, null, startedAt, 0,
                    WorkerAdoptionCensus.FormatRowDetail(row));
            Append(generation, "adoption-census-summary", "complete", null, startedAt, 0,
                WorkerAdoptionCensus.FormatSummaryDetail(rows));
        }
        return new ConductorLifecycleSession(this, generation, goalId, startedAt);
    }

    internal void Append(
        string generation,
        string operation,
        string status,
        string? goalId,
        DateTimeOffset occurredAt,
        int ticks,
        string detail)
    {
        var payload = JsonSerializer.Serialize(new
        {
            generationId = generation,
            ticks,
            occurredAt
        });
        store.AppendAsync(new RunEventAppend(
                RunEventTypes.ConductorLifecycle,
                goalId,
                operation,
                status,
                detail,
                payload,
                occurredAt))
            .GetAwaiter()
            .GetResult();
    }

    internal DateTimeOffset UtcNow() => _utcNow();
}

internal sealed class ConductorLifecycleSession(
    ConductorLifecycleRecorder recorder,
    string generationId,
    string? goalId,
    DateTimeOffset startedAt)
{
    private readonly object _gate = new();
    private bool _stopped;

    public string GenerationId { get; } = generationId;

    public bool IsStopped
    {
        get
        {
            lock (_gate)
            {
                return _stopped;
            }
        }
    }

    public void Stop(string reason, int ticks, string? detail = null)
    {
        lock (_gate)
        {
            if (_stopped)
            {
                return;
            }

            var stoppedAt = recorder.UtcNow();
            recorder.Append(
                GenerationId,
                operation: "stop",
                status: reason,
                goalId,
                stoppedAt,
                ticks,
                detail: $"reason={reason} ticks={ticks} durationSeconds={(long)Math.Max(0, (stoppedAt - startedAt).TotalSeconds)}" +
                        (string.IsNullOrWhiteSpace(detail) ? string.Empty : $" {detail}"));
            _stopped = true;
        }
    }
}
