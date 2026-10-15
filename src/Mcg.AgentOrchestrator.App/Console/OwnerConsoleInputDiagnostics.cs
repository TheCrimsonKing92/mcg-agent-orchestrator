using System.Text.Json;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed record OwnerConsoleTracePane(int Index, string? Identity, int Count,
    int X, int Y, int Width, int Height, int RowOffset = 0, int ColumnOffset = 0);

internal sealed record OwnerConsoleTraceState(string Focus, bool CommandEditing, bool Modal,
    OwnerConsoleTracePane Decisions, OwnerConsoleTracePane Board, OwnerConsoleTracePane Activity);

internal sealed record OwnerConsoleInputTraceRecord(long Timestamp, long Input, long Span, long Parent,
    string Phase, string Boundary, string? Detail, OwnerConsoleTraceState? State);

// A session observer, never a scheduler. Callbacks only append bounded records in memory.
internal sealed class OwnerConsoleInputDiagnostics
{
    private readonly TimeProvider _clock;
    private readonly int _capacity;
    private readonly object _gate = new();
    private readonly List<OwnerConsoleInputTraceRecord> _records = [];
    private readonly AsyncLocal<(long Input, long Span)> _context = new();
    private readonly long _origin;
    private readonly DateTimeOffset _opened;
    private long _input;
    private long _span;
    private long _dropped;
    private bool _sealed;

    internal OwnerConsoleInputDiagnostics(TimeProvider? clock = null, int capacity = 20000)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        _clock = clock ?? TimeProvider.System;
        _capacity = capacity;
        _opened = _clock.GetUtcNow();
        _origin = _clock.GetTimestamp();
    }

    internal Scope Begin(string phase, Func<OwnerConsoleTraceState>? state = null, string? detail = null,
        bool input = false)
    {
        var previous = _context.Value;
        var current = (Input: input ? Interlocked.Increment(ref _input) : previous.Input,
            Span: Interlocked.Increment(ref _span));
        _context.Value = current;
        Record(phase, "begin", detail, state, current.Input, current.Span, previous.Span);
        return new(this, previous, current, phase, state);
    }

    internal void Mark(string phase, Func<OwnerConsoleTraceState>? state = null, string? detail = null)
    {
        var context = _context.Value;
        Record(phase, "point", detail, state, context.Input, context.Span, 0);
    }

    // Capture identity explicitly across Invoke; the UI queue need not preserve ExecutionContext.
    internal Action ObservePost(string phase, Action action, Func<OwnerConsoleTraceState>? state = null)
    {
        var parent = _context.Value;
        var span = Interlocked.Increment(ref _span);
        Record(phase, "posted", null, null, parent.Input, span, parent.Span);
        return () =>
        {
            var previous = _context.Value;
            _context.Value = (parent.Input, span);
            var outcome = "completed";
            Record(phase, "begin", null, state, parent.Input, span, parent.Span);
            try { action(); }
            catch { outcome = "faulted"; throw; }
            finally
            {
                Record(phase, "end", outcome, state, parent.Input, span, parent.Span);
                _context.Value = previous;
            }
        };
    }

    private void Record(string phase, string boundary, string? detail, Func<OwnerConsoleTraceState>? state,
        long input, long span, long parent)
    {
        lock (_gate)
        {
            if (_sealed) return;
            if (_records.Count == _capacity) { _dropped++; return; }
            var timestamp = _clock.GetTimestamp() - _origin;
            _records.Add(new(timestamp, input, span, parent, phase, boundary, detail, state?.Invoke()));
        }
    }

    // Called only after the host restores the terminal. Open spans and overflow invalidate measurements.
    internal void Export(Stream destination)
    {
        OwnerConsoleInputTraceRecord[] records;
        long dropped;
        lock (_gate) { _sealed = true; records = _records.ToArray(); dropped = _dropped; }
        var ended = records.Where(record => record.Boundary == "end").Select(record => record.Span).ToHashSet();
        var incomplete = records.Where(record => (record.Boundary is "begin" or "posted") && !ended.Contains(record.Span))
            .Select(record => record.Span).Distinct().ToArray();
        JsonSerializer.Serialize(destination, new
        {
            Contract = 1, OpenedUtc = _opened, TimestampFrequency = _clock.TimestampFrequency,
            ClockOrigin = _origin, Capacity = _capacity, DroppedRecords = dropped, IncompleteSpans = incomplete,
            MeasurementLimits = new[]
            {
                "keyboard-callback is delivery to this view, not physical input emission or OS queue arrival",
                "headless-input bypasses the production keyboard and terminal",
                "state snapshots describe selection and viewport, not visible presentation",
                "layout-draw-complete is an application drawing callback, not terminal flush or visible pixels; Input=0 is uncorrelated",
                "draw callbacks can batch inputs; missing callbacks, open spans or overflow cannot establish latency",
                "correlate external timestamped input and visible capture; record diagnostic overhead and keep scenario constant"
            },
            Records = records
        });
        destination.Flush();
    }

    internal sealed class Scope(OwnerConsoleInputDiagnostics owner, (long Input, long Span) previous,
        (long Input, long Span) current, string phase, Func<OwnerConsoleTraceState>? state) : IDisposable
    {
        internal string Outcome { get; set; } = "completed";
        private bool _disposed;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            owner.Record(phase, "end", Outcome, state, current.Input, current.Span, previous.Span);
            owner._context.Value = previous;
        }
    }
}
