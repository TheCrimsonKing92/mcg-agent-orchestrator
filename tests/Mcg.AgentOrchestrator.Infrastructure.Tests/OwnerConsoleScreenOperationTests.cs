using System.Threading.Channels;
using Mcg.AgentOrchestrator.App.OwnerConsole;

public sealed class OwnerConsoleScreenOperationTests
{
    [Fact]
    public async Task QuestionReadsHoldTheCacheUntilTheirUnderlyingReadFinishes()
    {
        var source = new HeldQuestions();
        var serialized = new SerializedOwnerQuestionSource(source);
        var first = serialized.ReadAsync(TestContext.Current.CancellationToken);
        using var cancelled = new CancellationTokenSource();
        var waiting = serialized.ReadAsync(cancelled.Token);
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.Equal(1, source.Reads);
        source.Release.SetResult(new([], []));
        await first;

        await serialized.ReadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, source.Reads);
    }

    [Fact]
    public async Task BoundReportsBusyAndRetainsOwnershipUntilLateWorkEnds()
    {
        var clock = new ManualStewardTimeProvider();
        var working = Channel.CreateUnbounded<string?>();
        var reports = new List<string>();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = new OwnerConsoleScreenOperation(clock, label => working.Writer.TryWrite(label), reports.Add);
        var calls = 0;
        var run = operation.RunAsync("refresh", _ =>
        {
            Interlocked.Increment(ref calls);
            entered.SetResult();
            return release.Task; // Deliberately ignores cancellation, as an uncooperative seam can.
        });
        try
        {
            await entered.Task;
            clock.Advance(OwnerConsoleLoopOptions.Default.BusyNoticeAfter);
            Assert.Equal("refresh", await working.Reader.ReadAsync(TestContext.Current.CancellationToken));
            clock.Advance(OwnerConsoleLoopOptions.Default.OperationBound);
            Assert.False(await run);
            Assert.Contains("refresh did not finish within 30s", Assert.Single(reports));
            Assert.True(operation.IsRunning);

            Assert.False(await operation.RunAsync("second refresh", _ =>
            { Interlocked.Increment(ref calls); return Task.CompletedTask; }));
            Assert.Equal(1, calls);
            Assert.Null(await working.Reader.ReadAsync(TestContext.Current.CancellationToken));
        }
        finally { release.TrySetResult(); await operation.Completion; }

        Assert.True(await operation.RunAsync("next refresh", _ =>
        { Interlocked.Increment(ref calls); return Task.CompletedTask; }));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task UnexpectedDependencyFailureIsReportedAndNextStepCanRun()
    {
        var messages = new List<string>();
        var operation = new OwnerConsoleScreenOperation(new ManualStewardTimeProvider(), _ => { }, messages.Add);

        Assert.False(await operation.RunAsync("command", _ => throw new NotSupportedException("missing dependency")));

        Assert.Equal("command failed: missing dependency", Assert.Single(messages));
        Assert.False(operation.IsRunning);
        Assert.True(await operation.RunAsync("command", _ => Task.CompletedTask));
    }

    [Fact]
    public async Task NotificationUsesItsChannelAndDependencyFailureUsesReport()
    {
        var reports = new List<string>();
        var notices = new List<string>();
        var operation = new OwnerConsoleScreenOperation(new ManualStewardTimeProvider(), _ => { },
            reports.Add, notify: notices.Add);

        operation.Notify("Answer queued");
        Assert.Equal("Answer queued", Assert.Single(notices));
        Assert.Empty(reports);
        Assert.False(await operation.RunAsync("command", _ => throw new IOException("unavailable")));
        Assert.Equal("command failed: unavailable", Assert.Single(reports));
        Assert.Single(notices);
    }

    private sealed class HeldQuestions : IOwnerQuestionSource
    {
        internal int Reads;
        internal readonly TaskCompletionSource<OwnerQuestionSnapshot> Release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<IReadOnlyList<OwnerQuestion>> ListOpenAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<OwnerQuestionSnapshot> ReadAsync(CancellationToken cancellationToken)
        {
            Reads++;
            return Release.Task;
        }
    }
}
