using System.Diagnostics;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorBatchLoop
{
    // Only progress diagnostics consume this scoped clock. Scheduling, CPU accounting
    // and janitorial budgets retain their existing independent sources.
    private static readonly AsyncLocal<TimeProvider?> DiagnosticTime = new();
    private TimeProvider? _diagnosticTimeProvider;

    internal ConductorBatchLoop WithDiagnosticTimeProvider(TimeProvider provider)
    {
        _diagnosticTimeProvider = provider;
        return this;
    }

    private IDisposable ActivateDiagnosticTime(IDisposable ledgerScope)
    {
        if (_diagnosticTimeProvider is null)
            return ledgerScope;
        var scope = new DiagnosticTimeScope(DiagnosticTime.Value, ledgerScope);
        DiagnosticTime.Value = _diagnosticTimeProvider;
        return scope;
    }

    private static DateTimeOffset DiagnosticUtcNow() =>
        DiagnosticTime.Value?.GetUtcNow() ?? DateTimeOffset.UtcNow;

    internal static DiagnosticTimer StartDiagnosticTimer() => new(DiagnosticTime.Value);

    internal sealed class DiagnosticTimer
    {
        private readonly TimeProvider? _provider;
        private readonly Stopwatch? _stopwatch;
        private long _started;
        private TimeSpan _elapsed;
        private bool _running = true;

        internal DiagnosticTimer(TimeProvider? provider)
        {
            _provider = provider;
            if (provider is null)
                _stopwatch = Stopwatch.StartNew();
            else
                _started = provider.GetTimestamp();
        }

        internal bool IsRunning => _stopwatch?.IsRunning ?? _running;
        internal TimeSpan Elapsed => _stopwatch?.Elapsed ??
            (_running ? _elapsed + _provider!.GetElapsedTime(_started) : _elapsed);

        internal void Stop()
        {
            if (_stopwatch is not null)
                _stopwatch.Stop();
            else if (_running)
            {
                _elapsed = Elapsed;
                _running = false;
            }
        }

        internal void Restart()
        {
            if (_stopwatch is not null)
                _stopwatch.Restart();
            else
            {
                _elapsed = TimeSpan.Zero;
                _started = _provider!.GetTimestamp();
                _running = true;
            }
        }
    }

    private sealed class DiagnosticTimeScope(TimeProvider? previous, IDisposable ledgerScope) : IDisposable
    {
        public void Dispose()
        {
            DiagnosticTime.Value = previous;
            ledgerScope.Dispose();
        }
    }
}
