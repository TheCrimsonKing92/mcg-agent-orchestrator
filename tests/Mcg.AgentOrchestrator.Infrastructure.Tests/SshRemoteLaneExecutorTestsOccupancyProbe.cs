using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel safe: each fake owns its files, transport calls and clock.
public sealed class SshRemoteLaneExecutorTestsOccupancyProbe
{
    [Xunit.Theory]
    [Xunit.InlineData(2, 0, 2, 0, true)]
    [Xunit.InlineData(1, 1, 2, 0, true)]
    [Xunit.InlineData(1, 0, 2, 0, false)]
    [Xunit.InlineData(2, 0, 2, -121, false)]
    [Xunit.InlineData(2, 0, 2, 121, false)]
    [Xunit.InlineData(2, 0, 0, 0, false)]
    [Xunit.InlineData(2, 0, 2, -120, true)]
    [Xunit.InlineData(2, 0, 2, 120, true)]
    public void QueueState_CapacityAndFreshness_DetermineBusy(
        int running, int queued, int slots, int offset, bool busy)
    {
        var now = new ManualRemoteLaneClock().GetUtcNow();
        Assert.True(RemoteLaneExecutorQueueState.TryParse(State(now.AddSeconds(offset), running, queued, slots), out var state));
        Assert.NotNull(state);
        Assert.Equal(busy, state.IsBusy(now));
    }

    [Xunit.Theory]
    [Xunit.InlineData("{")]
    [Xunit.InlineData("[]")]
    [Xunit.InlineData("{}")]
    [Xunit.InlineData("{\"utc\":0,\"slots\":2}")]
    [Xunit.InlineData("{\"utc\":\"bad\",\"slots\":2}")]
    [Xunit.InlineData("{\"utc\":\"2026-10-16T00:00:00Z\",\"slots\":\"2\"}")]
    [Xunit.InlineData("{\"utc\":\"2026-10-16T00:00:00Z\",\"slots\":2,\"running\":[1]}")]
    [Xunit.InlineData("{\"utc\":\"2026-10-16T00:00:00Z\",\"slots\":2,\"queued\":null}")]
    public void QueueState_MalformedShape_DoesNotParse(string text)
    {
        Assert.False(RemoteLaneExecutorQueueState.TryParse(text, out var state));
        Assert.Null(state);
    }

    [Xunit.Fact]
    public void QueueState_MixedCaseAndMissingArrays_ParsesAsEmpty()
    {
        Assert.True(RemoteLaneExecutorQueueState.TryParse(
            "{\"UTC\":\"2026-10-16T00:00:00Z\",\"Slots\":2}", out var state));
        Assert.NotNull(state);
        Assert.Empty(state.Running);
        Assert.Empty(state.Queued);
        Assert.False(state.IsBusy(state.Utc));
    }

    [Xunit.Theory]
    [Xunit.InlineData("true", true, null)]
    [Xunit.InlineData(null, false, null)]
    [Xunit.InlineData("false", false, null)]
    [Xunit.InlineData("\"yes\"", false, "invalid")]
    public async Task Configuration_ProbeField_ValidatesBoolean(string? field, bool enabled, string? reason)
    {
        await using var fake = new FakeSshRemoteLaneTransport();
        var path = Path.Combine(fake.Root, "executors.json");
        var probe = field is null ? "" : ",\"occupancyProbe\":" + field;
        File.WriteAllText(path, "{\"executors\":[{\"id\":\"one\",\"transport\":\"ssh\",\"runnerAlias\":\"runner\"," +
            "\"adminAlias\":\"admin\",\"remoteRepository\":\"C:/repo/bare.git\"" + probe + "}],\"lanes\":[\"lane\"]}");
        var configuration = RemoteLaneExecutorConfiguration.Load(path);
        Assert.Equal(reason, configuration.DisabledReason);
        if (reason is null) Assert.Equal(enabled, Assert.Single(configuration.Executors).OccupancyProbe);
        else Assert.False(configuration.Enabled);
    }

    [Xunit.Fact]
    public async Task Submit_FreshFullState_ReturnsBusyBeforePush()
    {
        await using var fake = new FakeSshRemoteLaneTransport();
        var submission = await Executor(fake, true, State(fake.Clock.GetUtcNow(), 1, 1)).SubmitAsync(
            FakeSshRemoteLaneTransport.Request, default);
        Assert.Null(submission.Handle);
        Assert.Equal("executor-busy", submission.FailureReason);
        Assert.Equal("occupancy", Assert.Single(submission.Steps!).Name);
        var call = Assert.Single(fake.Calls);
        Assert.Equal(new[] { SshRemoteLaneExecutor.ScpPath, "-o", "BatchMode=yes",
            "runner:C:/mcg-executor/runner-state.json", "runner-state.json" }, call.Arguments);
        Assert.Equal(TimeSpan.FromSeconds(30), call.Bound);
    }

    [Xunit.Theory]
    [Xunit.InlineData("free")]
    [Xunit.InlineData("failed")]
    [Xunit.InlineData("missing")]
    [Xunit.InlineData("malformed")]
    [Xunit.InlineData("stale")]
    [Xunit.InlineData("future")]
    [Xunit.InlineData("zero-slots")]
    [Xunit.InlineData("throw")]
    [Xunit.InlineData("timeout")]
    public async Task Submit_UnavailableOrNotBusyProbe_ContinuesInOrder(string scenario)
    {
        await using var fake = new FakeSshRemoteLaneTransport();
        var text = scenario switch
        {
            "missing" => null,
            "malformed" => "{",
            "stale" => State(fake.Clock.GetUtcNow().AddSeconds(-121), 2, 0),
            "future" => State(fake.Clock.GetUtcNow().AddSeconds(121), 2, 0),
            "zero-slots" => State(fake.Clock.GetUtcNow(), 2, 0, 0),
            _ => State(fake.Clock.GetUtcNow(), 1, 0)
        };
        var submission = await Executor(fake, true, text, scenario).SubmitAsync(FakeSshRemoteLaneTransport.Request, default);
        fake.Handle = Assert.IsType<SshRemoteLaneHandle>(submission.Handle);
        Assert.Null(submission.FailureReason);
        Assert.Equal(new[] { "occupancy", "push", "job-copy", "trigger" }, submission.Steps!.Select(step => step.Name));
        var calls = fake.Calls.ToArray();
        Assert.Equal(4, calls.Length);
        Assert.EndsWith("/runner-state.json", calls[0].Arguments[3]);
        Assert.Contains("push", calls[1].Arguments);
        Assert.Equal("job.json", calls[2].Arguments[3]);
        Assert.Equal(SshRemoteLaneExecutor.SshPath, calls[3].Arguments[0]);
    }

    [Xunit.Fact]
    public async Task Submit_ProbeDisabled_StartsWithPush()
    {
        await using var fake = new FakeSshRemoteLaneTransport();
        var submission = await Executor(fake, false, State(fake.Clock.GetUtcNow(), 2, 0)).SubmitAsync(
            FakeSshRemoteLaneTransport.Request, default);
        fake.Handle = Assert.IsType<SshRemoteLaneHandle>(submission.Handle);
        Assert.Equal(new[] { "push", "job-copy", "trigger" }, submission.Steps!.Select(step => step.Name));
        Assert.Equal(3, fake.Calls.Count);
        Assert.Contains("push", fake.Calls.First().Arguments);
        Assert.DoesNotContain(fake.Calls, call => call.Arguments.Any(arg => arg.Contains("runner-state.json", StringComparison.Ordinal)));
    }

    [Xunit.Fact]
    public async Task Submit_CallerCancellation_StopsBeforePush()
    {
        await using var fake = new FakeSshRemoteLaneTransport();
        using var stop = new CancellationTokenSource();
        var executor = Executor(fake, true, null, "cancel", stop);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => executor.SubmitAsync(FakeSshRemoteLaneTransport.Request, stop.Token));
        Assert.Single(fake.Calls);
    }

    private static SshRemoteLaneExecutor Executor(FakeSshRemoteLaneTransport fake, bool enabled, string? text,
        string scenario = "", CancellationTokenSource? stop = null) => new(
        new([new("one", 60, "ssh", "runner", "admin", "C:/repo/bare.git", "C:/mcg-executor", OccupancyProbe: enabled)],
            [FakeSshRemoteLaneTransport.Request.Lane], null), fake.Root, Path.Combine(fake.Root, "attempt", "result"),
        fake.Clock, async (args, directory, bound, token) =>
        {
            if (args[3].EndsWith("/runner-state.json", StringComparison.Ordinal))
            {
                fake.Calls.Enqueue(new(args, directory, bound));
                if (scenario == "throw") throw new IOException("probe-unavailable");
                if (scenario == "cancel") { stop!.Cancel(); token.ThrowIfCancellationRequested(); }
                if (text is not null) File.WriteAllText(Path.Combine(directory, args[^1]), text);
                return new(scenario == "failed" ? 1 : 0, "", TimedOut: scenario == "timeout");
            }
            return await fake.Transport(args, directory, bound, token);
        }, fake.Git, TimeSpan.Zero);

    private static string State(DateTimeOffset utc, int running, int queued, int slots = 2) =>
        JsonSerializer.Serialize(new { utc, slots, running = Enumerable.Range(0, running).Select(i => "r" + i).ToArray(),
            queued = Enumerable.Range(0, queued).Select(i => "q" + i).ToArray() });
}
