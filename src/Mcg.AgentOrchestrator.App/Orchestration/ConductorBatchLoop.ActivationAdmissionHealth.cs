namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorBatchLoop
{
    // The driver also captures censuses. Flow this per-Run reference to its tasks,
    // without sharing samples between concurrent loops or replacing it between ticks.
    private static readonly AsyncLocal<ActivationAdmissionAccumulator?> CurrentActivationAdmission = new();

    private sealed class ActivationAdmissionAccumulator : IDisposable
    {
        private readonly object _gate = new();
        private readonly ActivationAdmissionAccumulator? _previous = CurrentActivationAdmission.Value;
        private bool _closed;
        private bool _failed;
        private int _waiting;
        private int _occupied;
        private int _live;

        internal void Record(bool failed = false, int? waiting = null, int? occupied = null, int? live = null)
        {
            lock (_gate)
            {
                if (_closed) return;
                _failed |= failed;
                if (waiting is { } w) _waiting = w;
                if (occupied is { } o) _occupied = o;
                if (live is { } l) _live = l;
            }
        }

        internal ActivationAdmissionSample Take(int tick)
        {
            lock (_gate)
            {
                var sample = new ActivationAdmissionSample(tick, _failed, _waiting, _occupied, _live);
                _failed = false;
                _waiting = _occupied = _live = 0;
                return sample;
            }
        }

        public void Dispose()
        {
            lock (_gate) _closed = true;
            CurrentActivationAdmission.Value = _previous;
        }
    }

    private static IDisposable BeginActivationAdmissionHealth(bool emit)
    {
        var scope = new ActivationAdmissionAccumulator();
        CurrentActivationAdmission.Value = emit ? scope : null;
        return scope;
    }

    private static void RecordActivationAdmissionFailure() => CurrentActivationAdmission.Value?.Record(failed: true);
    private static void RecordActivationAdmissionWaiting(int waiting) => CurrentActivationAdmission.Value?.Record(waiting: waiting);
    private static void RecordActivationAdmissionCensus(int occupied, int live) =>
        CurrentActivationAdmission.Value?.Record(occupied: occupied, live: live);

    private static void EmitActivationAdmissionHealth(bool emit, int tick)
    {
        if (emit && CurrentActivationAdmission.Value is { } current)
            EmitProgress(current.Take(tick).Format());
    }
}
