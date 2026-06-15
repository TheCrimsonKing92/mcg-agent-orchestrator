namespace Mcg.AgentOrchestrator.App.Dashboard.Api;

/// <summary>
/// Thread-safe in-memory store for conductor tick events pushed by the CLI loop.
/// The dashboard SSE stream reads from this to broadcast real-time tick events to clients.
/// </summary>
internal static class ConductorEventBus
{
    private static long _seq;
    private static readonly object _lock = new();
    private static readonly List<ConductorTickEvent> _recent = new(capacity: 200);

    public static void RecordTick(
        int tick,
        int advanced,
        int held,
        int escalated,
        int retried,
        int done,
        bool watchSleeping)
    {
        lock (_lock)
        {
            var seq = System.Threading.Interlocked.Increment(ref _seq);
            _recent.Add(new ConductorTickEvent(seq, DateTimeOffset.UtcNow, tick, advanced, held, escalated, retried, done, watchSleeping));
            if (_recent.Count > 200)
                _recent.RemoveAt(0);
        }
    }

    public static IReadOnlyList<ConductorTickEvent> GetSince(long sinceSeq)
    {
        lock (_lock)
        {
            return _recent.Where(e => e.Seq > sinceSeq).ToList();
        }
    }

    public static void Clear()
    {
        lock (_lock)
        {
            _recent.Clear();
        }
    }
}

internal sealed record ConductorTickEvent(
    long Seq,
    DateTimeOffset OccurredAt,
    int Tick,
    int Advanced,
    int Held,
    int Escalated,
    int Retried,
    int Done,
    bool WatchSleeping);
