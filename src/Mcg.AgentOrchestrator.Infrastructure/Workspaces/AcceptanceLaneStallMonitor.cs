namespace Mcg.AgentOrchestrator.Infrastructure;

// One monitor owns one heartbeat-backed child. Only output changes reset its silence window.
internal sealed class AcceptanceLaneStallMonitor : IDisposable
{
    private readonly string _lane;
    private readonly TimeProvider _time;
    private readonly Action<string> _emit;
    private readonly CancellationTokenSource _source = new();
    private DateTimeOffset _lastProgressAt;
    private long _outputBytes = -1;
    // 0 = observing, 1 = stalled, 2 = child wait completed. A stop wins at most once.
    private int _state;

    internal AcceptanceLaneStallMonitor(string lane, TimeProvider time, TimeSpan limit, Action<string>? emit = null)
    {
        _lane = lane;
        _time = time;
        Limit = limit;
        _lastProgressAt = time.GetUtcNow();
        _emit = emit ?? (line => { Console.WriteLine(line); Console.Out.Flush(); });
    }

    internal static AcceptanceLaneStallMonitor? ForHeartbeat(GateHeartbeatContext? context, TimeProvider time) =>
        context?.Phase == "verification-check"
            ? new(context.CurrentTarget, time, AcceptanceLaneStallPolicy.StallLimit)
            : null;

    internal CancellationToken Token => _source.Token;
    internal bool Stopped => Volatile.Read(ref _state) == 1;
    internal string? StopReason => Stopped ? AcceptanceLaneStallPolicy.LaneStalledReason : null;
    internal TimeSpan? Silent { get; private set; }
    internal TimeSpan Limit { get; }

    internal void Observe(GateHeartbeatSnapshot snapshot)
    {
        if (Volatile.Read(ref _state) != 0)
            return;

        var now = _time.GetUtcNow();
        if (snapshot.OutputBytes != _outputBytes)
        {
            _outputBytes = snapshot.OutputBytes;
            _lastProgressAt = now;
            return;
        }

        if (!AcceptanceLaneStallPolicy.IsStalled(_lastProgressAt, now, Limit) ||
            Interlocked.CompareExchange(ref _state, 1, 0) != 0)
            return;

        Silent = now - _lastProgressAt;
        try
        {
            _emit(FormattableString.Invariant($"LANE_STALLED lane=\"{_lane.ReplaceLineEndings(" ")}\" silent_seconds={(long)Silent.Value.TotalSeconds} limit_seconds={(long)Limit.TotalSeconds}"));
        }
        catch { /* Diagnostic failure must not prevent the child stop. */ }
        try { _source.Cancel(); } catch (ObjectDisposedException) { }
    }

    // Retire when WaitForExit completes, before capture drains and heartbeat teardown.
    internal void Complete() => Interlocked.CompareExchange(ref _state, 2, 0);

    public void Dispose() => _source.Dispose();
}
