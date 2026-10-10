using System.Globalization;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// A scope owns one pre-loop phase, including its ticker and stop/emit ordering.
internal sealed class ConductorStartupHeartbeat
{
    internal static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(15);
    // Half of the continuity supervisor's default two-minute silence window.
    internal static readonly TimeSpan MaxInterval = TimeSpan.FromSeconds(60);
    internal static readonly ConductorStartupHeartbeat Shipped = new(
        line => { Console.WriteLine(line); Console.Out.Flush(); },
        () => DateTimeOffset.UtcNow, Task.Delay);

    internal static class Phases
    {
        internal const string SweepKernelLoad = "sweep-kernel-load";
        internal const string TerminalSweep = "terminal-sweep";
        internal const string MetadataExclusionCount = "metadata-exclusion-count";
        internal const string SweepSurfacing = "sweep-surfacing";
        internal const string SweepPersistence = "sweep-persistence";
        internal const string OrphanWorktreeSweep = "orphan-worktree-sweep";
        internal const string RemoteMirrorStart = "remote-mirror-start";
        internal const string LoopSetup = "loop-setup";
        internal static IReadOnlyList<string> All { get; } = Array.AsReadOnly(new[]
        {
            SweepKernelLoad, TerminalSweep, MetadataExclusionCount, SweepSurfacing,
            SweepPersistence, OrphanWorktreeSweep, RemoteMirrorStart, LoopSetup
        });
    }

    private readonly Action<string> _emit;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly object _emitLock = new();
    internal TimeSpan Interval { get; }

    internal ConductorStartupHeartbeat(Action<string> emit, Func<DateTimeOffset> clock,
        Func<TimeSpan, CancellationToken, Task> delay, TimeSpan? interval = null)
    {
        ArgumentNullException.ThrowIfNull(emit);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(delay);
        Interval = interval ?? DefaultInterval;
        if (Interval <= TimeSpan.Zero || Interval > MaxInterval)
            throw new ArgumentOutOfRangeException(nameof(interval));
        _emit = emit;
        _clock = clock;
        _delay = delay;
    }

    internal Phase Begin(string phase) => new(this, phase);

    internal void Run(string phase, Action body)
    {
        using var scope = Begin(phase);
        body();
    }

    internal T Run<T>(string phase, Func<T> body)
    {
        using var scope = Begin(phase);
        return body();
    }

    internal static string Format(string phase, TimeSpan elapsed) =>
        $"STARTUP_PROGRESS phase={phase} elapsed_ms={Math.Max(0, elapsed.Ticks / TimeSpan.TicksPerMillisecond).ToString(CultureInfo.InvariantCulture)}";

    internal sealed class Phase : IDisposable
    {
        private readonly ConductorStartupHeartbeat _owner;
        private readonly string _name;
        private readonly DateTimeOffset _started;
        private readonly CancellationTokenSource _stop = new();
        private bool _stopped;
        internal Task Completion { get; }

        internal Phase(ConductorStartupHeartbeat owner, string name)
        {
            _owner = owner;
            _name = name;
            _started = owner._clock();
            lock (owner._emitLock) Emit(TimeSpan.Zero);
            Completion = Task.Run(TickAsync);
        }

        private void Emit(TimeSpan elapsed)
        {
            try { _owner._emit(Format(_name, elapsed)); }
            catch (Exception) { /* Progress reporting must not change the body's outcome. */ }
        }

        private async Task TickAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    await _owner._delay(_owner.Interval, _stop.Token).ConfigureAwait(false);
                    lock (_owner._emitLock)
                    {
                        if (_stopped) return;
                        Emit(_owner._clock() - _started);
                    }
                }
            }
            catch (Exception) { /* Observe delay, clock and cancellation faults. */ }
        }

        public void Dispose()
        {
            lock (_owner._emitLock)
            {
                if (_stopped) return;
                _stopped = true;
            }
            try { _stop.Cancel(); }
            catch (Exception) { /* Cancellation callbacks cannot mask the body's exception. */ }
            _ = Completion.ContinueWith(_ => _stop.Dispose(), CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }
}
