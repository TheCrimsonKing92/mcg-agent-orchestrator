namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed class ActivationAdmissionAccumulator : IDisposable
{
    // The driver also captures censuses. Flow this per-Run reference to its tasks,
    // without sharing samples between concurrent loops or replacing it between ticks.
    internal static readonly AsyncLocal<ActivationAdmissionAccumulator?> Current = new();
    private readonly object _gate = new();
    private readonly ActivationAdmissionAccumulator? _previous = Current.Value;
    private bool _closed;
    private bool _failed;
    private int _waiting;
    private int _occupied;
    private int _live;
    private int _evidenceHeld;

    internal static IDisposable Begin(bool emit)
    {
        var scope = new ActivationAdmissionAccumulator();
        Current.Value = emit ? scope : null;
        return scope;
    }

    internal void Record(bool failed = false, int? waiting = null, int? occupied = null, int? live = null,
        int? evidenceHeld = null)
    {
        lock (_gate)
        {
            if (_closed) return;
            _failed |= failed;
            if (waiting is { } w) _waiting = w;
            if (occupied is { } o) _occupied = o;
            if (live is { } l) _live = l;
            if (evidenceHeld is { } e) _evidenceHeld = e;
        }
    }

    internal ActivationAdmissionSample Take(int tick)
    {
        lock (_gate)
        {
            var sample = new ActivationAdmissionSample(tick, _failed, _waiting, _occupied, _live, _evidenceHeld);
            _failed = false;
            _waiting = _occupied = _live = _evidenceHeld = 0;
            return sample;
        }
    }

    public void Dispose()
    {
        lock (_gate) _closed = true;
        Current.Value = _previous;
    }
}
