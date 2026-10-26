using System.Collections.Concurrent;
using System.Threading.Channels;
using Mcg.AgentOrchestrator.App.OwnerConsole;

// Parallel-safe: all channels, clocks and session state belong to this fixture instance.
internal sealed class OwnerConsoleLoopTestHarness
{
    private static CancellationToken TestToken => TestContext.Current.CancellationToken;
    internal OwnerConsoleTestClock Clock { get; } = new();
    internal FakeInput Input { get; } = new();
    internal FakeEvents Events { get; } = new();
    internal FakeSteps Steps { get; } = new();
    internal SignallingOutput Output { get; } = new();

    internal Task RunAsync() => new OwnerConsoleLoop(Steps, Input, Events, Output, Clock,
        new OwnerConsoleLoopOptions(TimeSpan.FromHours(1), TimeSpan.FromSeconds(30), TimeSpan.Zero))
        .RunAsync(TestToken);

    internal async Task CommandBarrierAsync()
    {
        Input.Send("help");
        Assert.Equal("help", await Steps.Commands.Reader.ReadAsync(TestToken));
    }

    internal async Task QuitAsync(Task run)
    {
        Input.Send("quit");
        await run.WaitAsync(TimeSpan.FromSeconds(30), TestToken);
    }

    internal sealed class FakeSteps : IOwnerConsoleSteps
    {
        internal Channel<string> Commands { get; } = Channel.CreateUnbounded<string>();
        internal Channel<OwnerConductEvent> EventsHandled { get; } = Channel.CreateUnbounded<OwnerConductEvent>();
        internal Func<OwnerConductEvent, Task> OnEvent { get; set; } = _ => Task.CompletedTask;
        internal Func<string, CancellationToken, Task<bool>> OnCommand { get; set; } =
            (line, _) => Task.FromResult(line != "quit");

        public Task StartAsync(DateTimeOffset? lastActivity, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task HandleEventAsync(OwnerConductEvent item, CancellationToken cancellationToken)
        {
            EventsHandled.Writer.TryWrite(item);
            return OnEvent(item);
        }
        public Task<bool> HandleCommandAsync(string line, CancellationToken cancellationToken)
        {
            Commands.Writer.TryWrite(line);
            return OnCommand(line, cancellationToken);
        }
    }

    internal sealed class FakeInput : IOwnerConsoleInput
    {
        private readonly Channel<string?> _lines = Channel.CreateUnbounded<string?>();
        public bool IsEditingLine => false;
        internal void Send(string? line) => _lines.Writer.TryWrite(line);
        public ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken) =>
            _lines.Reader.ReadAsync(cancellationToken);
    }

    internal sealed class FakeEvents : IConductEventSource
    {
        private readonly Channel<OwnerConductEvent> _items = Channel.CreateUnbounded<OwnerConductEvent>();
        private readonly Channel<int> _reads = Channel.CreateUnbounded<int>();
        private int _readCount;
        internal int ReadCount => Volatile.Read(ref _readCount);
        public DateTimeOffset? LastActivity => null;
        internal void Send(int number) => _items.Writer.TryWrite(
            new OwnerConductEvent(DateTimeOffset.UnixEpoch.AddSeconds(number), "tick", null, number.ToString()));
        public ValueTask<OwnerConductEvent> ReadAsync(CancellationToken cancellationToken)
        {
            _reads.Writer.TryWrite(Interlocked.Increment(ref _readCount));
            return _items.Reader.ReadAsync(cancellationToken);
        }
        internal async Task WaitForReadAsync(int number)
        {
            while (await _reads.Reader.ReadAsync(TestToken) < number) { }
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    internal sealed class SignallingOutput : IOwnerConsoleOutput
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
