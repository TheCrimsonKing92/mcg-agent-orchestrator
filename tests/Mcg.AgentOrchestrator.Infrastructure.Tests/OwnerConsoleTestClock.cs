internal sealed class OwnerConsoleTestClock : TimeProvider
{
    private readonly ManualStewardTimeProvider _timers = new();
    internal DateTimeOffset Now { get; set; } = new(2026, 1, 1, 0, 5, 0, TimeSpan.Zero);
    internal TimeZoneInfo Zone { get; set; } = TimeZoneInfo.Utc;
    internal Action<TimeSpan>? TimerChanged { get; set; }
    public override DateTimeOffset GetUtcNow() => Now;
    public override TimeZoneInfo LocalTimeZone => Zone;
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = _timers.CreateTimer(callback, state, dueTime, period);
        TimerChanged?.Invoke(dueTime);
        return new ObservedTimer(timer, due => TimerChanged?.Invoke(due));
    }

    private sealed class ObservedTimer(ITimer timer, Action<TimeSpan> changed) : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            var result = timer.Change(dueTime, period);
            if (result) changed(dueTime);
            return result;
        }
        public void Dispose() => timer.Dispose();
        public ValueTask DisposeAsync() => timer.DisposeAsync();
    }

    // Move only the elapsed timers; the fixed wall time keeps notice expectations stable.
    internal void Advance(TimeSpan by) => _timers.Advance(by);
}
