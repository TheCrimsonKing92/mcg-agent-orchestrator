internal sealed class ManualStewardTimeProvider : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<ManualTimer> _timers = [];
    private TimeSpan _elapsed;

    internal TaskCompletionSource TimerCreated { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public override ITimer CreateTimer(TimerCallback callback, object? state,
        TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        TimerCreated.TrySetResult();
        return timer;
    }

    internal void Advance(TimeSpan by)
    {
        List<ManualTimer> due;
        lock (_gate)
        {
            _elapsed += by;
            due = _timers.Where(timer => timer.IsDue(_elapsed)).ToList();
            foreach (var timer in due)
                timer.RemoveDue();
        }

        foreach (var timer in due)
            timer.Fire();
    }

    private sealed class ManualTimer(
        ManualStewardTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        private TimeSpan? _due;
        private bool _disposed;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._gate)
            {
                if (_disposed) return false;
                if (!owner._timers.Contains(this)) owner._timers.Add(this);
                _due = dueTime == Timeout.InfiniteTimeSpan ? null : owner._elapsed + dueTime;
                return true;
            }
        }

        internal bool IsDue(TimeSpan now) => !_disposed && _due is { } due && due <= now;
        internal void RemoveDue() => _due = null;
        internal void Fire() => callback(state);

        public void Dispose()
        {
            lock (owner._gate)
            {
                _disposed = true;
                owner._timers.Remove(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
