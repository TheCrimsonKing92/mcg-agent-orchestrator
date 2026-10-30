using System.Text.Json;

// Parallel-safe: owned files, injected environment and monotonic clock; no process globals.
public sealed class HostCapacitySlotTimingTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("slot-timing-").FullName;

    [Fact]
    public async Task Enter_TwoOccupiedSlots_RecordsQueueAndAcquiredIntervals()
    {
        var path = Path.Combine(root, "uses.jsonl");
        var clock = new ControlledClock();
        var budget = new HostCapacityTestBudget(2, _ => path, clock);
        await using var first = await budget.EnterAsync();
        clock.Advance(7);
        await using var second = await budget.EnterAsync();
        clock.Advance(11);
        var thirdPending = budget.EnterAsync().AsTask();
        Assert.Equal(0, budget.AvailableSlots);
        Assert.False(thirdPending.IsCompleted);

        // The pending acquisition is the event gate. Advance only while both slots are
        // occupied; the controlled monotonic interval is independent of machine speed.
        clock.Advance(37);
        await first.DisposeAsync();
        await using var third = await thirdPending.WaitAsync(TimeSpan.FromSeconds(30));
        clock.Advance(13);
        await third.DisposeAsync();
        await second.DisposeAsync();
        await first.DisposeAsync(); // A repeated disposal emits no extra row or release.

        var rows = File.ReadAllLines(path).Select(line => JsonSerializer.Deserialize<Row>(line)!).ToArray();
        Assert.Equal(3, rows.Length);
        var queued = Assert.Single(rows, row => row.wait_ms > 0);
        Assert.True(queued.wait_ms >= 37);
        Assert.Equal(37, queued.wait_ms);
        Assert.Equal(13, queued.held_ms); // Must exclude its 37 ms queue interval.
        Assert.Equal(new double[] { 13, 55, 61 }, rows.Select(row => row.held_ms).Order());
        Assert.Equal(2, budget.AvailableSlots);
        Assert.All(rows, row =>
        {
            Assert.Equal(nameof(HostCapacitySlotTimingTests) + "." +
                nameof(Enter_TwoOccupiedSlots_RecordsQueueAndAcquiredIntervals), row.test_name);
            Assert.Equal(2, row.slot_count);
            Assert.Equal(Environment.ProcessId, row.process_id);
        });
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task Enter_DisabledTiming_WritesNothingAndReturnsWorkingSlot(string? destination)
    {
        var path = Path.Combine(root, "enabled.jsonl");
        var budget = new HostCapacityTestBudget(1, _ => destination);
        await using (var held = await budget.EnterAsync()) Assert.Equal(0, budget.AvailableSlots);
        Assert.Equal(1, budget.AvailableSlots);
        Assert.Empty(Directory.GetFiles(root));

        // Positive control: the same acquisition/disposal path really creates telemetry.
        var enabled = new HostCapacityTestBudget(1, _ => path);
        await using (var held = await enabled.EnterAsync()) Assert.Equal(0, enabled.AvailableSlots);
        Assert.Single(File.ReadAllLines(path));
        Assert.Equal(1, enabled.AvailableSlots);
    }

    private sealed record Row(string test_name, double wait_ms, double held_ms, int slot_count, int process_id);

    private sealed class ControlledClock : TimeProvider
    {
        private long timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref timestamp);
        internal void Advance(int milliseconds) => Interlocked.Add(ref timestamp, milliseconds * TimeSpan.TicksPerMillisecond);
    }

    public void Dispose() => Directory.Delete(root, recursive: true);
}
