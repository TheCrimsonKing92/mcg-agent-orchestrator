using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;

public sealed class OwnerConsoleBoardTests
{
    [Fact]
    public async Task WatchTransitionPrintsHeaderAndBothActiveGoals()
    {
        var harness = new OwnerConsoleHarness();
        harness.AddGoal("11111111111111111111111111111111", "Build search", AgentRole.Developer);
        harness.AddGoal("22222222222222222222222222222222", "Review search", AgentRole.Reviewer);
        var input = new ReleasedInput();
        var events = new QueuedEvents(new OwnerConductEvent(
            harness.Clock.GetUtcNow(), "watch-transition", "11111111", "running"), input);
        await new OwnerConsoleLoop(harness.Session(), input, events, harness.Clock).RunAsync();

        Assert.Contains("console", CliArgumentParser.RecognizedCommands);
        Assert.Contains(harness.Output.Text.Split(Environment.NewLine),
            line => line.StartsWith("conductor: running | active goals: 2 | owner questions: 0", StringComparison.Ordinal));
        Assert.Contains("11111111 | Build search | Active | Developer", harness.Output.Text);
        Assert.Contains("22222222 | Review search | Active | Reviewer", harness.Output.Text);
    }

    private sealed class ReleasedInput : IOwnerConsoleInput
    {
        private readonly TaskCompletionSource<string?> _line = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IsEditingLine => false;
        public ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken) => new(_line.Task);
        internal void Release() => _line.TrySetResult("quit");
    }

    private sealed class QueuedEvents(OwnerConductEvent first, ReleasedInput input) : IConductEventSource
    {
        private readonly TaskCompletionSource<OwnerConductEvent> _pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _sent;
        public DateTimeOffset? LastActivity => null;
        public ValueTask<OwnerConductEvent> ReadAsync(CancellationToken cancellationToken)
        {
            if (!_sent) { _sent = true; return ValueTask.FromResult(first); }
            input.Release();
            cancellationToken.Register(() => _pending.TrySetCanceled(cancellationToken));
            return new(_pending.Task);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
