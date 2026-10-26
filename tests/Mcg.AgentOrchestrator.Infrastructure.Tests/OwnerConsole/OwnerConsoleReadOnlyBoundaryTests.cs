using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;

public sealed class OwnerConsoleReadOnlyBoundaryTests
{
    [Fact]
    public async Task ObservationCommandsOnlyUseReadSeams()
    {
        var harness = new OwnerConsoleHarness();
        harness.AddGoal("11111111111111111111111111111111", "Build search", AgentRole.Developer);
        var session = harness.Session();
        await session.StartAsync(null, CancellationToken.None);

        await session.HandleCommandAsync("board", CancellationToken.None);
        await session.HandleCommandAsync("goal 11111111", CancellationToken.None);
        await session.HandleCommandAsync("help", CancellationToken.None);
        var keepRunning = await session.HandleCommandAsync("quit", CancellationToken.None);

        Assert.False(keepRunning);
        Assert.Empty(harness.Answers.Calls);
        Assert.All(harness.State.Calls, call => Assert.Contains(call, new[] { "metadata", "goals" }));
        Assert.Equal(15, harness.Tail.RequestedCount);
        Assert.Contains("recent event", harness.Output.Text);
    }

    [Fact]
    public async Task StalledEventSourceCannotHoldShutdown()
    {
        var harness = new OwnerConsoleHarness();
        var source = new NeverCompletingEventSource();
        var loop = new OwnerConsoleLoop(harness.Session(), new SingleLineInput(), source,
            TimeProvider.System, new OwnerConsoleLoopOptions(TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(1), TimeSpan.Zero));

        await loop.RunAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(loop.EventSourceAbandoned);
        Assert.Empty(harness.Answers.Calls);
    }

    [Fact]
    public async Task ProductionEventAndLivenessReadsPreserveConductorFiles()
    {
        var directory = Path.Combine(Path.GetTempPath(), "mcg-owner-console-", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var lockPath = Path.Combine(directory, "conduct-loop.lock");
            var stopPath = Path.Combine(directory, ".conduct-stop");
            var logPath = Path.Combine(directory, "conduct-events.log");
            File.WriteAllText(lockPath, $"{Environment.ProcessId}\n{DateTimeOffset.UtcNow:O}\n{DateTimeOffset.UtcNow:O}\n");
            File.WriteAllText(stopPath, "stop request");
            File.WriteAllText(logPath, "");
            var lockBytes = File.ReadAllBytes(lockPath);
            var stopBytes = File.ReadAllBytes(stopPath);
            var lockTime = File.GetLastWriteTimeUtc(lockPath);
            var stopTime = File.GetLastWriteTimeUtc(stopPath);
            var harness = new OwnerConsoleHarness();
            var source = new ConductEventFileSource(logPath, TimeProvider.System);
            var session = new OwnerConsoleSession(harness.State, harness.Questions, harness.Answers,
                new ConductorLeaseLiveness(directory), harness.Digest, harness.Tail,
                harness.Output, harness.Clock);

            await new OwnerConsoleLoop(session, new SingleLineInput(), source, TimeProvider.System,
                new OwnerConsoleLoopOptions(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.Zero))
                .RunAsync().WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(lockBytes, File.ReadAllBytes(lockPath));
            Assert.Equal(stopBytes, File.ReadAllBytes(stopPath));
            Assert.Equal(lockTime, File.GetLastWriteTimeUtc(lockPath));
            Assert.Equal(stopTime, File.GetLastWriteTimeUtc(stopPath));
            Assert.Equal(3, Directory.GetFiles(directory).Length);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private sealed class NeverCompletingEventSource : IConductEventSource
    {
        public DateTimeOffset? LastActivity => null;
        public ValueTask<OwnerConductEvent> ReadAsync(CancellationToken cancellationToken) =>
            new(new TaskCompletionSource<OwnerConductEvent>().Task);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class SingleLineInput : IOwnerConsoleInput
    {
        public bool IsEditingLine => false;
        public ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<string?>("quit");
    }
}
