using System.Threading.Channels;
using Mcg.AgentOrchestrator.App.OwnerConsole;

public sealed class OwnerConsoleEventPumpTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReadFailureIsReportedAndNextEventStillRefreshes(bool ioFailure)
    {
        var clock = new ManualStewardTimeProvider();
        var messages = Channel.CreateUnbounded<string>();
        var refreshed = new TaskCompletionSource<OwnerConductEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var events = new FailingOnceEvents(ioFailure);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var pump = new OwnerConsoleEventPump(events, clock, message => messages.Writer.TryWrite(message));
        var run = pump.RunAsync(item => { refreshed.SetResult(item); return Task.CompletedTask; }, stop.Token);
        try
        {
            var message = await messages.Reader.ReadAsync(TestContext.Current.CancellationToken);
            Assert.Contains("conductor event read failed; retrying: unavailable", message);
            await clock.TimerCreated.Task;
            clock.Advance(OwnerConsoleLoopOptions.Default.PollBound);

            Assert.Equal("acceptance", (await refreshed.Task.WaitAsync(TestContext.Current.CancellationToken)).EventKind);
            Assert.True(events.Reads >= 2);
        }
        finally { stop.Cancel(); await run; }
    }

    private sealed class FailingOnceEvents(bool ioFailure) : IConductEventSource
    {
        internal int Reads;
        public DateTimeOffset? LastActivity => null;
        public async ValueTask<OwnerConductEvent> ReadAsync(CancellationToken cancellationToken)
        {
            switch (++Reads)
            {
                case 1: throw ioFailure ? new IOException("unavailable") : new InvalidOperationException("unavailable");
                case 2: return new(DateTimeOffset.UnixEpoch, "acceptance", "11111111", "result=passed");
                default:
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    throw new InvalidOperationException("unreachable");
            }
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
