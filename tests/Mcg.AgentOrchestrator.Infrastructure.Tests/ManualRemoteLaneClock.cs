internal sealed class ManualRemoteLaneClock : TimeProvider
{
    private long _ticks = new DateTimeOffset(2026, 10, 6, 0, 0, 0, TimeSpan.Zero).UtcTicks;
    public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);
    internal void Advance(TimeSpan elapsed) => Interlocked.Add(ref _ticks, elapsed.Ticks);
}
