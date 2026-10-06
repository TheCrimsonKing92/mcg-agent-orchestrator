using System.Diagnostics;
using System.Text;

internal static class RealProcessSilenceGuard
{
    internal static readonly TimeSpan DefaultSilenceWindow = TimeSpan.FromMinutes(2);
    internal const int PollMilliseconds = 50;
    private static readonly AsyncLocal<Settings?> Current = new();

    internal static IDisposable Begin(Func<TimeSpan> elapsed, TimeSpan? silenceWindow = null,
        TimeSpan? totalCeiling = null, Action<Observation>? observe = null)
    {
        ArgumentNullException.ThrowIfNull(elapsed);
        var window = silenceWindow ?? DefaultSilenceWindow;
        var ceiling = totalCeiling ?? MtpTestRunnerScriptTests.NativeMtpRealProcessHangGuard;
        if (window < TimeSpan.FromSeconds(1))
            throw new ArgumentOutOfRangeException(nameof(silenceWindow));
        if (ceiling < window + TimeSpan.FromSeconds(1) || ceiling > MtpTestRunnerScriptTests.NativeMtpRealProcessHangGuard)
            throw new ArgumentOutOfRangeException(nameof(totalCeiling));
        var previous = Current.Value;
        Current.Value = new Settings(elapsed, window, ceiling, observe);
        return new Scope(previous);
    }

    internal static Tracker Start()
    {
        var stopwatch = Stopwatch.StartNew();
        return new Tracker(Current.Value ?? new Settings(() => stopwatch.Elapsed,
            DefaultSilenceWindow, MtpTestRunnerScriptTests.NativeMtpRealProcessHangGuard, null),
            Current.Value is not null);
    }

    internal static async Task<string> ReadChunksAsync(TextReader reader, Action<string> stamp,
        CancellationToken cancellationToken = default)
    {
        var captured = new StringBuilder();
        var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) != 0)
        {
            var chunk = new string(buffer, 0, count);
            captured.Append(chunk);
            stamp(chunk);
        }
        return captured.ToString();
    }

    internal readonly record struct Observation(TimeSpan Elapsed, string? Output);
    internal sealed record Settings(Func<TimeSpan> Elapsed, TimeSpan Window, TimeSpan Ceiling,
        Action<Observation>? Observe);

    internal sealed class Tracker(Settings settings, bool injected)
    {
        private readonly object _sync = new();
        private TimeSpan _lastOutput = settings.Elapsed();
        internal TimeSpan Ceiling => settings.Ceiling;
        internal string? Failure { get; private set; }

        internal void Stamp(string chunk)
        {
            lock (_sync)
            {
                _lastOutput = settings.Elapsed();
                settings.Observe?.Invoke(new Observation(_lastOutput, chunk));
            }
        }

        internal bool WaitForExit(Process process, TimeSpan? timeout)
        {
            // Preserve the existing production explicit-timeout wait exactly. Only tests inject time.
            if (timeout is { } explicitTimeout && !injected)
                return process.WaitForExit(checked((int)explicitTimeout.TotalMilliseconds));

            while (!process.WaitForExit(PollMilliseconds))
            {
                lock (_sync)
                {
                    var elapsed = settings.Elapsed();
                    Failure = timeout is { } budget
                        ? (elapsed >= budget ? "explicit total deadline" : null)
                        : elapsed >= settings.Ceiling
                            ? $"exceeded the total ceiling of {settings.Ceiling.TotalSeconds:0} seconds; elapsed={elapsed}."
                            : elapsed - _lastOutput >= settings.Window
                                ? $"produced no output for {settings.Window.TotalSeconds:0} seconds; elapsed={elapsed}."
                                : null;
                    settings.Observe?.Invoke(new Observation(elapsed, null));
                    if (Failure is not null)
                        return false;
                }
            }
            return true;
        }
    }

    private sealed class Scope(Settings? previous) : IDisposable
    {
        public void Dispose() => Current.Value = previous;
    }
}

internal sealed class ManualElapsedClock
{
    private readonly object _sync = new();
    private TimeSpan _elapsed;
    private TimeSpan _evaluated = TimeSpan.MinValue;
    private readonly StringBuilder _output = new();
    private readonly List<(TimeSpan Elapsed, string Output)> _chunks = [];
    private TaskCompletionSource _changed = NewSignal();
    internal TimeSpan Elapsed { get { lock (_sync) return _elapsed; } }

    internal void AdvanceTo(TimeSpan elapsed)
    {
        lock (_sync)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(elapsed, _elapsed);
            _elapsed = elapsed;
        }
    }

    internal void Observe(RealProcessSilenceGuard.Observation observation)
    {
        lock (_sync)
        {
            if (observation.Output is { } chunk)
            {
                _output.Append(chunk);
                _chunks.Add((observation.Elapsed, chunk));
            }
            else _evaluated = observation.Elapsed;
            var signal = _changed;
            _changed = NewSignal();
            signal.TrySetResult();
        }
    }

    internal Task WaitForEvaluationAtOrAfter(TimeSpan elapsed) =>
        WaitFor(() => _evaluated >= elapsed, $"guard evaluation at {elapsed}");

    internal Task WaitForOutputContaining(string text) =>
        WaitFor(() => _output.ToString().Contains(text, StringComparison.Ordinal), $"output containing '{text}'");

    internal Task WaitForOutputAtOrAfter(TimeSpan elapsed) =>
        WaitFor(() => _chunks.Any(chunk => chunk.Elapsed >= elapsed), $"output stamped at {elapsed}");

    private async Task WaitFor(Func<bool> ready, string eventName)
    {
        using var hangGuard = new CancellationTokenSource(MtpTestRunnerScriptTests.NativeMtpRealProcessHangGuard);
        while (true)
        {
            Task changed;
            lock (_sync)
            {
                if (ready()) return;
                changed = _changed.Task;
            }
            try { await changed.WaitAsync(hangGuard.Token); }
            catch (OperationCanceledException)
            {
                throw new TimeoutException($"Did not observe {eventName} before the real-process hang guard.");
            }
        }
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
