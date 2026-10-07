using System.Collections.Concurrent;
using System.Threading.Channels;
using Mcg.AgentOrchestrator.App.OwnerConsole;

public sealed class OwnerConsoleLoopResponsivenessTests
{
    private static CancellationToken TestToken => TestContext.Current.CancellationToken;

    [Fact(Timeout = 30000)]
    public async Task ReadyCommandAndEvent_CommandRunsFirst()
    {
        var harness = new Harness();
        harness.Events.Send(1);
        harness.Input.Send("help");
        var run = harness.RunAsync();
        await harness.Steps.EventsHandled.Reader.ReadAsync(TestToken);
        harness.Input.Send("quit");
        await run;

        Assert.Equal(new[] { "command:help", "event:1", "command:quit" }, harness.Steps.Order);
        Assert.Empty(harness.Output.Lines);
    }

    [Fact(Timeout = 30000)]
    public async Task FiveEventsDuringRefresh_OneFollowUpUsesLatestEvent()
    {
        var harness = new Harness();
        var release = Signal();
        harness.Steps.OnEvent = item => item.Detail == "1" ? release.Task : Task.CompletedTask;
        harness.Events.Send(1);
        var run = harness.RunAsync();
        await harness.Steps.EventsHandled.Reader.ReadAsync(TestToken);
        for (var i = 2; i <= 6; i++) harness.Events.Send(i);
        await harness.Events.WaitForReadAsync(7);
        release.SetResult();
        var followUp = await harness.Steps.EventsHandled.Reader.ReadAsync(TestToken);
        harness.Input.Send("quit");
        await run;

        Assert.Equal("6", followUp.Detail);
        Assert.Equal(DateTimeOffset.UnixEpoch.AddSeconds(6), followUp.Timestamp);
        Assert.Equal(2, harness.Steps.EventCount);
    }

    [Fact(Timeout = 30000)]
    public async Task ReadyCommandAfterRefresh_RunsBeforeCoalescedEvent()
    {
        var harness = new Harness();
        var release = Signal();
        harness.Steps.OnEvent = item => item.Detail == "1" ? release.Task : Task.CompletedTask;
        harness.Events.Send(1);
        var run = harness.RunAsync();
        await harness.Steps.EventsHandled.Reader.ReadAsync(TestToken);
        harness.Events.Send(2);
        await harness.Events.WaitForReadAsync(3);
        await harness.Input.SendToPendingReadAsync("help");
        release.SetResult();
        await harness.Steps.EventsHandled.Reader.ReadAsync(TestToken);
        harness.Input.Send("quit");
        await run;

        Assert.Equal(new[] { "event:1", "command:help", "event:2", "command:quit" }, harness.Steps.Order);
    }

    [Fact(Timeout = 30000)]
    public async Task SlowCommand_BusyNoticePrintsOnce()
    {
        var harness = new Harness();
        var release = Signal();
        harness.Steps.OnCommand = async (line, _) =>
        {
            if (line == "slow") await release.Task;
            return line != "quit";
        };
        harness.Input.Send("slow");
        var run = harness.RunAsync();
        await harness.Steps.Commands.Reader.ReadAsync(TestToken);
        harness.Clock.Advance(TimeSpan.FromSeconds(2) + TimeSpan.FromTicks(1));
        await harness.Output.WaitForLineAsync("working: slow ...");
        harness.Clock.Advance(TimeSpan.FromSeconds(2));
        release.SetResult();
        harness.Input.Send("quit");
        await run;

        Assert.Equal(new[] { "working: slow ..." }, harness.Output.Lines);
    }

    [Fact(Timeout = 30000)]
    public async Task SynchronousSlowCommand_BoundReportsAndNextCommandRuns()
    {
        var harness = new Harness();
        var release = Signal();
        harness.Steps.OnCommand = (line, _) =>
        {
            if (line == "slow") release.Task.GetAwaiter().GetResult();
            return Task.FromResult(line != "quit");
        };
        harness.Input.Send("slow");
        var run = harness.RunAsync();
        try
        {
            await harness.Steps.Commands.Reader.ReadAsync(TestToken);
            harness.Clock.Advance(TimeSpan.FromSeconds(2) + TimeSpan.FromTicks(1));
            await harness.Output.WaitForLineAsync("working: slow ...");
            harness.Clock.Advance(TimeSpan.FromSeconds(28));
            await harness.Output.WaitForLineAsync("slow did not finish within 30s; the console is still running");
            harness.Input.Send("help");
            Assert.Equal("help", await harness.Steps.Commands.Reader.ReadAsync(TestToken));
        }
        finally { release.TrySetResult(); }
        harness.Input.Send("quit");
        await run;

        Assert.Single(harness.Output.Lines, line => line == "working: slow ...");
    }

    [Fact(Timeout = 30000)]
    public async Task TimedOutCommand_NextCommandRunsAndLateAnswerPrints()
    {
        var harness = new Harness();
        var release = Signal();
        harness.Steps.OnCommand = async (line, _) =>
        {
            if (line == "slow")
            {
                await release.Task;
                harness.Output.WriteLine("late answer");
            }
            return line != "quit";
        };
        harness.Input.Send("slow");
        var run = harness.RunAsync();
        await harness.Steps.Commands.Reader.ReadAsync(TestToken);
        harness.Clock.Advance(TimeSpan.FromSeconds(30));
        await harness.Output.WaitForLineAsync("slow did not finish within 30s; the console is still running");
        harness.Input.Send("help");
        Assert.Equal("help", await harness.Steps.Commands.Reader.ReadAsync(TestToken));
        release.SetResult();
        await harness.Output.WaitForLineAsync("late answer");
        harness.Input.Send("quit");
        await run;

        Assert.DoesNotContain("slow was abandoned", harness.Output.Lines);
    }

    [Fact(Timeout = 30000)]
    public async Task TimedOutCommand_CancellationPrintsAbandonedAndLoopContinues()
    {
        var harness = new Harness();
        var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Steps.OnCommand = (line, ct) => line == "slow"
            ? cancelled.Task.WaitAsync(ct) : Task.FromResult(line != "quit");
        harness.Input.Send("slow");
        var run = harness.RunAsync();
        await harness.Steps.Commands.Reader.ReadAsync(TestToken);
        harness.Clock.Advance(TimeSpan.FromSeconds(30));
        await harness.Output.WaitForLineAsync("slow did not finish within 30s; the console is still running");
        await harness.Output.WaitForLineAsync("slow was abandoned");
        harness.Input.Send("help");
        Assert.Equal("help", await harness.Steps.Commands.Reader.ReadAsync(TestToken));
        harness.Input.Send("quit");
        await run;

        Assert.Single(harness.Output.Lines, line => line == "slow was abandoned");
    }

    [Fact(Timeout = 30000)]
    public async Task TimedOutRefresh_RemainsSingleWhileNextCommandRuns()
    {
        var harness = new Harness();
        var release = Signal();
        harness.Steps.OnEvent = item => item.Detail == "1" ? release.Task : Task.CompletedTask;
        harness.Events.Send(1);
        var run = harness.RunAsync();
        await harness.Steps.EventsHandled.Reader.ReadAsync(TestToken);
        harness.Events.Send(2);
        await harness.Events.WaitForReadAsync(3);
        harness.Clock.Advance(TimeSpan.FromSeconds(30));
        await harness.Output.WaitForLineAsync(
            "refresh after conductor event did not finish within 30s; the console is still running");
        harness.Input.Send("help");
        Assert.Equal("help", await harness.Steps.Commands.Reader.ReadAsync(TestToken));
        Assert.Equal(1, harness.Steps.EventCount);
        release.SetResult();
        Assert.Equal("2", (await harness.Steps.EventsHandled.Reader.ReadAsync(TestToken)).Detail);
        harness.Input.Send("quit");
        await run;

        Assert.Single(harness.Output.Lines, line => line == "working: refresh after conductor event ...");
    }

    [Theory(Timeout = 30000)]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ThrowingCommand_ErrorPrintsAndNextCommandRuns(bool synchronous)
    {
        var harness = new Harness();
        harness.Steps.OnCommand = (line, _) =>
        {
            if (line != "boom") return Task.FromResult(line != "quit");
            if (synchronous) throw new InvalidOperationException("kaboom");
            return Task.FromException<bool>(new InvalidOperationException("kaboom"));
        };
        harness.Input.Send("boom");
        var run = harness.RunAsync();
        await harness.Output.WaitForLineAsync("error: kaboom");
        Assert.Equal("boom", await harness.Steps.Commands.Reader.ReadAsync(TestToken));
        harness.Input.Send("help");
        Assert.Equal("help", await harness.Steps.Commands.Reader.ReadAsync(TestToken));
        harness.Input.Send("quit");
        await run;

        Assert.Equal(new[] { "error: kaboom" }, harness.Output.Lines);
    }

    [Fact(Timeout = 30000)]
    public async Task ThrowingRefresh_ErrorPrintsAndNextCommandRuns()
    {
        var harness = new Harness();
        harness.Steps.OnEvent = _ => Task.FromException(new IOException("refresh failed"));
        harness.Events.Send(1);
        var run = harness.RunAsync();
        await harness.Output.WaitForLineAsync("error: refresh failed");
        harness.Input.Send("help");
        Assert.Equal("help", await harness.Steps.Commands.Reader.ReadAsync(TestToken));
        harness.Input.Send("quit");
        await run;

        Assert.Equal(new[] { "error: refresh failed" }, harness.Output.Lines);
    }

    [Fact(Timeout = 30000)]
    public async Task ThrowingStartup_ErrorPrintsAndCommandStillRuns()
    {
        var harness = new Harness();
        harness.Steps.OnStart = () => Task.FromException(new InvalidOperationException("startup failed"));
        var run = harness.RunAsync();
        await harness.Output.WaitForLineAsync("error: startup failed");
        harness.Input.Send("help");
        Assert.Equal("help", await harness.Steps.Commands.Reader.ReadAsync(TestToken));
        harness.Input.Send(null);
        await run;

        Assert.Equal(new[] { "error: startup failed" }, harness.Output.Lines);
    }

    [Fact(Timeout = 30000)]
    public async Task TimedOutCommand_LateFailurePrintsErrorAndLoopContinues()
    {
        var harness = new Harness();
        var result = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Steps.OnCommand = (line, _) => line == "slow" ? result.Task : Task.FromResult(line != "quit");
        harness.Input.Send("slow");
        var run = harness.RunAsync();
        await harness.Steps.Commands.Reader.ReadAsync(TestToken);
        harness.Clock.Advance(TimeSpan.FromSeconds(30));
        await harness.Output.WaitForLineAsync("slow did not finish within 30s; the console is still running");
        result.SetException(new IOException("late failure"));
        await harness.Output.WaitForLineAsync("error: late failure");
        harness.Input.Send("help");
        Assert.Equal("help", await harness.Steps.Commands.Reader.ReadAsync(TestToken));
        harness.Input.Send("quit");
        await run;

        Assert.Single(harness.Output.Lines, line => line == "error: late failure");
    }

    [Fact(Timeout = 30000)]
    public async Task CoalescedEvents_BoardTriggerSurvivesLatestNonBoardEvent()
    {
        var harness = new Harness();
        var release = Signal();
        harness.Steps.OnEvent = item => item.Detail == "1" ? release.Task : Task.CompletedTask;
        harness.Events.Send(1);
        var run = harness.RunAsync();
        await harness.Steps.EventsHandled.Reader.ReadAsync(TestToken);
        harness.Events.Send(2, "watch-transition");
        harness.Events.Send(3);
        await harness.Events.WaitForReadAsync(4);
        release.SetResult();
        var followUp = await harness.Steps.EventsHandled.Reader.ReadAsync(TestToken);
        harness.Input.Send("quit");
        await run;

        Assert.Equal("watch-transition", followUp.EventKind);
        Assert.Equal("3", followUp.Detail);
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class Harness
    {
        internal ManualStewardTimeProvider Clock { get; } = new();
        internal FakeInput Input { get; } = new();
        internal FakeEvents Events { get; } = new();
        internal FakeSteps Steps { get; } = new();
        internal SignallingOutput Output { get; } = new();

        internal Task RunAsync() => new OwnerConsoleLoop(Steps, Input, Events, Output, Clock,
            new OwnerConsoleLoopOptions(TimeSpan.FromHours(1), TimeSpan.FromSeconds(30), TimeSpan.Zero))
            .RunAsync(TestToken);
    }

    private sealed class FakeSteps : IOwnerConsoleSteps
    {
        internal ConcurrentQueue<string> Order { get; } = new();
        internal Channel<string> Commands { get; } = Channel.CreateUnbounded<string>();
        internal Channel<OwnerConductEvent> EventsHandled { get; } = Channel.CreateUnbounded<OwnerConductEvent>();
        internal Func<string, CancellationToken, Task<bool>> OnCommand { get; set; } =
            (line, _) => Task.FromResult(line != "quit");
        internal Func<OwnerConductEvent, Task> OnEvent { get; set; } = _ => Task.CompletedTask;
        internal Func<Task> OnStart { get; set; } = () => Task.CompletedTask;
        private int _eventCount;
        internal int EventCount => Volatile.Read(ref _eventCount);

        public Task StartAsync(DateTimeOffset? lastActivity, CancellationToken cancellationToken) => OnStart();
        public Task<bool> HandleCommandAsync(string line, CancellationToken cancellationToken)
        {
            Order.Enqueue($"command:{line}");
            Commands.Writer.TryWrite(line);
            return OnCommand(line, cancellationToken);
        }
        public Task HandleEventAsync(OwnerConductEvent item, CancellationToken cancellationToken)
        {
            Order.Enqueue($"event:{item.Detail}");
            Interlocked.Increment(ref _eventCount);
            EventsHandled.Writer.TryWrite(item);
            return OnEvent(item);
        }
    }

    private sealed class FakeInput : IOwnerConsoleInput
    {
        private readonly Channel<string?> _lines = Channel.CreateUnbounded<string?>();
        private Task<string?>? _pendingRead;
        public bool IsEditingLine => false;
        internal void Send(string? line) => _lines.Writer.TryWrite(line);
        public ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken) =>
            new(_pendingRead = _lines.Reader.ReadAsync(cancellationToken).AsTask());

        internal async Task SendToPendingReadAsync(string line)
        {
            var read = _pendingRead ?? throw new InvalidOperationException("No console line read is pending.");
            Send(line);
            // Await the same Task exposed to the loop before releasing its active refresh.
            // Channel delivery alone does not complete the ValueTask's AsTask continuation.
            await read.WaitAsync(TestToken);
        }
    }

    private sealed class FakeEvents : IConductEventSource
    {
        private readonly Channel<OwnerConductEvent> _items = Channel.CreateUnbounded<OwnerConductEvent>();
        private readonly Channel<int> _reads = Channel.CreateUnbounded<int>();
        private int _readCount;
        public DateTimeOffset? LastActivity => null;
        internal void Send(int number, string kind = "tick") => _items.Writer.TryWrite(
            new OwnerConductEvent(DateTimeOffset.UnixEpoch.AddSeconds(number), kind, null, number.ToString()));
        public ValueTask<OwnerConductEvent> ReadAsync(CancellationToken cancellationToken)
        {
            var read = _items.Reader.ReadAsync(cancellationToken);
            _reads.Writer.TryWrite(++_readCount);
            return read;
        }
        internal async Task WaitForReadAsync(int number)
        {
            while (await _reads.Reader.ReadAsync(TestToken) < number) { }
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class SignallingOutput : IOwnerConsoleOutput
    {
        internal ConcurrentQueue<string> Lines { get; } = new();
        private readonly Channel<string> _lines = Channel.CreateUnbounded<string>();
        public void Write(string text) => WriteLine(text);
        public void WriteLine(string text)
        {
            Lines.Enqueue(text);
            _lines.Writer.TryWrite(text);
        }
        internal async Task WaitForLineAsync(string expected)
        {
            while (await _lines.Reader.ReadAsync(TestToken) != expected) { }
        }
    }
}
