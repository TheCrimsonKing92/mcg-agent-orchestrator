internal sealed class OwnerConsoleTestClock : TimeProvider
{
    private readonly ManualStewardTimeProvider _timers = new();
    internal DateTimeOffset Now { get; set; } = new(2026, 1, 1, 0, 5, 0, TimeSpan.Zero);
    internal TimeZoneInfo Zone { get; set; } = TimeZoneInfo.Utc;
    public override DateTimeOffset GetUtcNow() => Now;
    public override TimeZoneInfo LocalTimeZone => Zone;
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
        _timers.CreateTimer(callback, state, dueTime, period);

    // Move only the elapsed timers; the fixed wall time keeps notice expectations stable.
    internal void Advance(TimeSpan by) => _timers.Advance(by);
}
