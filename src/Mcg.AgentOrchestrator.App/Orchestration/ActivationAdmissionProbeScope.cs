using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Owns the attempts admitted within one loop's activation window and their reported phases.
internal sealed class ActivationAdmissionProbeScope(IDisposable health) : IDisposable
{
    internal static readonly AsyncLocal<ActivationAdmissionProbeScope?> Current = new();

    private readonly object _gate = new();
    private readonly ActivationAdmissionProbeScope? _previous = Current.Value;
    private readonly List<ActivationProbeAttempt> _attempts = [];
    private readonly AcceptanceHolderLease _lease = new();
    private int _index;
    private readonly ActivationAdmissionWaitingCensus _census = new();
    private Func<ActivationAdmissionWaitingCensus, int>? _waiting;
    private bool _closed;

    private sealed class ActivationProbeAttempt(AcceptanceHolderBinding binding, string result, string? grouped)
    {
        internal AcceptanceHolderBinding Binding { get; } = binding;
        internal string Result { get; } = result;
        internal string? Grouped { get; } = grouped;
        internal string Phase { get; set; } = "none";
    }

    internal void Add(AcceptanceHolderBinding binding, string result, string? grouped)
    {
        lock (_gate)
            if (!_closed && !_attempts.Any(attempt => attempt.Binding.AttemptId == binding.AttemptId))
                _attempts.Add(new(binding, result, grouped));
    }

    internal void Waiting(Func<ActivationAdmissionWaitingCensus, int> waiting)
    {
        lock (_gate)
        {
            _census.Clear();
            _waiting = waiting;
        }
    }

    internal static ConductorBatchLoop.SoloAcceptanceAdmission RecordSolo(
        GoalId goalId, ConductorBatchLoop.SoloAcceptanceAdmission admission)
    {
        Current.Value?._census.Record(goalId, admission.Kind);
        return admission;
    }

    internal ActivationAdmissionProbeLine Take(int tick)
    {
        lock (_gate)
        {
            var waiting = _waiting?.Invoke(_census) ?? 0;
            _waiting = null;
            while (_index < _attempts.Count)
            {
                var attempt = _attempts[_index];
                string? holderPhase = null;
                try { holderPhase = _lease.Read(attempt.Binding).Phase; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException) { }
                if (holderPhase is "Cancelled" or "Detached" or "Adopted")
                {
                    _index++;
                    continue;
                }
                // Report every phase even when publication and settlement happened in one tick.
                if (attempt.Phase == "none") attempt.Phase = "admitted";
                else if (attempt.Phase == "admitted" && HasTerminalProbeResult(attempt)) attempt.Phase = "terminal";
                else if (attempt.Phase == "terminal" && holderPhase == "Released") attempt.Phase = "released";
                return new(tick, attempt.Phase, attempt.Binding.AttemptId, waiting);
            }
            return new(tick, "none", "none", waiting);
        }
    }

    private static bool HasTerminalProbeResult(ActivationProbeAttempt attempt)
    {
        if (attempt.Grouped is null) return File.Exists(attempt.Result);
        try { return ConductorGroupedGateAttemptCoordinator.HasFinishedResult(
            ConductorGroupedGateAttemptCoordinator.Read(attempt.Grouped)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException) { return false; }
    }

    public void Dispose()
    {
        lock (_gate) _closed = true;
        Current.Value = _previous;
        health.Dispose();
    }
}
