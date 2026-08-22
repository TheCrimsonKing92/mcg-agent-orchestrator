using System.Globalization;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class AcceptanceGatePhaseNames
{
    internal const string GatePlan = "gate-plan";
    internal const string BuildServerShutdown = "build-server-shutdown";
    internal const string PlanConstruction = "plan-construction";
    internal const string SharedPrebuild = "shared-prebuild";
    internal const string LaneExecution = "lane-execution";
    internal const string CheckExecution = "check-execution";
    internal const string PolicySynthesis = "policy-synthesis";
    internal const string StructuralCoverage = "structural-coverage";
    internal const string AdvisoryAndTamper = "advisory-and-tamper";
    internal const string Finalize = "finalize";
}

public sealed record AcceptanceGatePhaseDuration(string Name, TimeSpan Duration);

public sealed record AcceptanceGatePhaseBreakdown(
    string Scope,
    string Outcome,
    TimeSpan TotalDuration,
    TimeSpan LaneExecutionDuration,
    TimeSpan AttributedPhaseDuration,
    TimeSpan UnattributedDuration,
    IReadOnlyList<AcceptanceGatePhaseDuration> Phases);

internal sealed class AcceptanceGatePhaseAccountant : IDisposable
{
    private static readonly AsyncLocal<AcceptanceGatePhaseAccountant?> CurrentAccountant = new();
    private readonly TimeProvider _timeProvider;
    private readonly Action<AcceptanceGateProgress> _emit;
    private readonly CancellationToken _cancellationToken;
    private readonly AcceptanceGatePhaseAccountant? _previous;
    private readonly string? _goalId;
    private readonly long _startedTimestamp;
    private readonly DateTimeOffset _startedAt;
    private readonly List<string> _phaseOrder = [];
    private readonly Dictionary<string, TimeSpan> _phaseDurations = new(StringComparer.Ordinal);
    private string? _currentPhase;
    private long _currentPhaseStartedTimestamp;
    private TimeSpan _recordedLaneDuration;
    private bool _hasRecordedLaneDuration;
    private string _outcome = "faulted";
    private bool _disposed;

    private AcceptanceGatePhaseAccountant(
        TimeProvider timeProvider,
        string? goalId,
        Action<AcceptanceGateProgress> emit,
        CancellationToken cancellationToken)
    {
        _timeProvider = timeProvider;
        _goalId = goalId;
        _emit = emit;
        _cancellationToken = cancellationToken;
        _previous = CurrentAccountant.Value;
        _startedTimestamp = timeProvider.GetTimestamp();
        _startedAt = timeProvider.GetUtcNow();
        CurrentAccountant.Value = this;
    }

    internal static AcceptanceGatePhaseAccountant Start(
        TimeProvider timeProvider,
        string? goalId,
        Action<AcceptanceGateProgress> emit,
        CancellationToken cancellationToken = default) =>
        new(timeProvider, goalId, emit, cancellationToken);

    internal static void TransitionCurrent(string phase) => CurrentAccountant.Value?.TransitionTo(phase);

    internal static void RecordCurrentLaneExecution(TimeSpan duration) =>
        CurrentAccountant.Value?.RecordLaneExecution(duration);

    internal void TransitionTo(string phase)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(phase);
        if (string.Equals(_currentPhase, phase, StringComparison.Ordinal))
        {
            return;
        }

        var now = _timeProvider.GetTimestamp();
        CloseCurrentPhase(now);
        _currentPhase = phase;
        _currentPhaseStartedTimestamp = now;
        EnsurePhase(phase);
    }

    internal IDisposable BeginPhase(string phase)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var parent = _currentPhase;
        TransitionTo(phase);
        return new PhaseScope(this, phase, parent);
    }

    internal void Pause()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        CloseCurrentPhase(_timeProvider.GetTimestamp());
        _currentPhase = null;
    }

    internal void RecordLaneExecution(TimeSpan duration)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _recordedLaneDuration += duration;
        _hasRecordedLaneDuration = true;
        if (string.Equals(_currentPhase, AcceptanceGatePhaseNames.LaneExecution, StringComparison.Ordinal))
        {
            _phaseDurations[AcceptanceGatePhaseNames.LaneExecution] += duration;
            _currentPhase = null;
        }
    }

    internal void MarkCompleted(bool passed) => _outcome = passed ? "completed" : "failed";

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            var completedTimestamp = _timeProvider.GetTimestamp();
            CloseCurrentPhase(completedTimestamp);
            _currentPhase = null;
            if (_cancellationToken.IsCancellationRequested)
            {
                _outcome = "cancelled";
            }

            var breakdown = BuildBreakdown(completedTimestamp);
            var observedAt = _timeProvider.GetUtcNow();
            _emit(new AcceptanceGateProgress(
                _goalId,
                "gate-phase-breakdown",
                FormatCompact(breakdown),
                null,
                Environment.ProcessId,
                null,
                _startedAt,
                observedAt,
                observedAt,
                breakdown.TotalDuration,
                0,
                string.Empty,
                PhaseBreakdown: breakdown));
        }
        catch
        {
            // Instrumentation must not replace a gate verdict, fault, or cancellation.
        }
        finally
        {
            CurrentAccountant.Value = _previous;
        }
    }

    private void CloseCurrentPhase(long timestamp)
    {
        if (_currentPhase is null)
        {
            return;
        }

        var elapsed = _timeProvider.GetElapsedTime(_currentPhaseStartedTimestamp, timestamp);
        _phaseDurations[_currentPhase] += elapsed;
    }

    private void EnsurePhase(string phase)
    {
        if (_phaseDurations.TryAdd(phase, TimeSpan.Zero))
        {
            _phaseOrder.Add(phase);
        }
    }

    private AcceptanceGatePhaseBreakdown BuildBreakdown(long completedTimestamp)
    {
        var phases = _phaseOrder
            .Select(name => new AcceptanceGatePhaseDuration(name, _phaseDurations[name]))
            .ToArray();
        var laneDuration = _hasRecordedLaneDuration
            ? _recordedLaneDuration
            : phases.Where(phase => phase.Name == AcceptanceGatePhaseNames.LaneExecution)
                .Aggregate(TimeSpan.Zero, (total, phase) => total + phase.Duration);
        var attributedDuration = phases
            .Where(phase => phase.Name != AcceptanceGatePhaseNames.LaneExecution)
            .Aggregate(TimeSpan.Zero, (total, phase) => total + phase.Duration);
        var totalDuration = _timeProvider.GetElapsedTime(_startedTimestamp, completedTimestamp);
        var unattributedDuration = totalDuration - attributedDuration - laneDuration;
        return new AcceptanceGatePhaseBreakdown(
            "verifier-run",
            _outcome,
            totalDuration,
            laneDuration,
            attributedDuration,
            unattributedDuration,
            phases);
    }

    private static string FormatCompact(AcceptanceGatePhaseBreakdown breakdown)
    {
        static string Milliseconds(TimeSpan value) =>
            value.TotalMilliseconds.ToString("0.###", CultureInfo.InvariantCulture);

        var fields = new List<string>
        {
            $"scope={breakdown.Scope}",
            $"outcome={breakdown.Outcome}",
            $"total_ms={Milliseconds(breakdown.TotalDuration)}",
            $"lane_ms={Milliseconds(breakdown.LaneExecutionDuration)}",
            $"attributed_ms={Milliseconds(breakdown.AttributedPhaseDuration)}",
            $"unattributed_ms={Milliseconds(breakdown.UnattributedDuration)}"
        };
        fields.AddRange(breakdown.Phases.Select(
            phase => $"{phase.Name}_ms={Milliseconds(phase.Duration)}"));
        return string.Join(';', fields);
    }

    private sealed class PhaseScope(
        AcceptanceGatePhaseAccountant owner,
        string phase,
        string? parent) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            if (!string.Equals(owner._currentPhase, phase, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Cannot close phase '{phase}' while '{owner._currentPhase ?? "none"}' is active.");
            }

            if (parent is null)
            {
                owner.Pause();
            }
            else
            {
                owner.TransitionTo(parent);
            }

            _disposed = true;
        }
    }
}
