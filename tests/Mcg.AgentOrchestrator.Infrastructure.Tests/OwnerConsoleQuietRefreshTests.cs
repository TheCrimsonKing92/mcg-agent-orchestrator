public sealed class OwnerConsoleQuietRefreshTests
{
    private static CancellationToken TestToken => TestContext.Current.CancellationToken;
    private const string Prefix = "[00:05:00] ";
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Fact(Timeout = 30000)]
    public async Task TenSlowRefreshes_NeverPrintBusy()
    {
        var harness = new OwnerConsoleLoopTestHarness();
        var release = Signal();
        var slowCount = 0;
        harness.Steps.OnEvent = item =>
        {
            if (int.Parse(item.Detail!) % 2 == 0) return Task.CompletedTask;
            Interlocked.Increment(ref slowCount);
            return release.Task;
        };
        var run = harness.RunAsync();
        for (var i = 1; i <= 10; i++)
        {
            release = Signal();
            harness.Events.Send(i * 2 - 1);
            Assert.Equal((i * 2 - 1).ToString(),
                (await harness.Steps.EventsHandled.Reader.ReadAsync(TestToken)).Detail);
            await harness.Events.WaitForReadAsync(i * 2);
            harness.Clock.Advance(TimeSpan.FromSeconds(3));
            // The next read follows the busy check on the still-blocked operation.
            // This makes the negative assertion fail on the original loop.
            harness.Events.Send(i * 2);
            await harness.Events.WaitForReadAsync(i * 2 + 1);
            release.SetResult();
            Assert.Equal((i * 2).ToString(),
                (await harness.Steps.EventsHandled.Reader.ReadAsync(TestToken)).Detail);
            await harness.CommandBarrierAsync();
        }
        await harness.QuitAsync(run);

        Assert.Equal(10, slowCount);
        Assert.DoesNotContain(harness.Output.Lines, line => line.Contains("working:", StringComparison.Ordinal));
        Assert.Empty(harness.Output.Lines);
    }

    [Fact(Timeout = 30000)]
    public async Task TimeoutAndFurtherFailures_ReportOnceThenRecoverAndRearm()
    {
        var harness = new OwnerConsoleLoopTestHarness();
        var release = Signal();
        harness.Steps.OnEvent = item => item.Detail switch
        {
            "1" => release.Task,
            "2" or "3" or "5" => Task.FromException(new IOException("refresh failed")),
            _ => Task.CompletedTask
        };
        harness.Events.Send(1);
        var run = harness.RunAsync();
        await harness.Steps.EventsHandled.Reader.ReadAsync(TestToken);
        await harness.Events.WaitForReadAsync(2);
        harness.Clock.Advance(TimeSpan.FromSeconds(30));
        await harness.Output.WaitForLineAsync(Prefix + "refresh did not finish within 30s");
        await harness.CommandBarrierAsync();
        release.SetException(new IOException("late refresh failed"));

        for (var i = 2; i <= 3; i++)
        {
            harness.Events.Send(i);
            Assert.Equal(i.ToString(), (await harness.Steps.EventsHandled.Reader.ReadAsync(TestToken)).Detail);
            await harness.CommandBarrierAsync();
            Assert.Equal(new[] { Prefix + "refresh did not finish within 30s" }, harness.Output.Lines);
        }
        harness.Events.Send(4);
        await harness.Steps.EventsHandled.Reader.ReadAsync(TestToken);
        await harness.Output.WaitForLineAsync(Prefix + "refresh recovered");
        await harness.CommandBarrierAsync();
        harness.Events.Send(5);
        await harness.Steps.EventsHandled.Reader.ReadAsync(TestToken);
        await harness.Output.WaitForLineAsync(Prefix + "refresh error: refresh failed");
        await harness.CommandBarrierAsync();
        harness.Events.Send(6);
        await harness.Steps.EventsHandled.Reader.ReadAsync(TestToken);
        await harness.Output.WaitForLineAsync(Prefix + "refresh recovered");
        harness.Events.Send(7);
        await harness.Steps.EventsHandled.Reader.ReadAsync(TestToken);
        await harness.CommandBarrierAsync();
        await harness.QuitAsync(run);

        Assert.Equal(new[]
        {
            Prefix + "refresh did not finish within 30s", Prefix + "refresh recovered",
            Prefix + "refresh error: refresh failed", Prefix + "refresh recovered"
        }, harness.Output.Lines);
    }

    [Fact(Timeout = 30000)]
    public async Task LateSuccessfulRefresh_RecoversOnlyOnce()
    {
        var harness = new OwnerConsoleLoopTestHarness();
        var release = Signal();
        harness.Steps.OnEvent = item => item.Detail == "1" ? release.Task : Task.CompletedTask;
        harness.Events.Send(1);
        var run = harness.RunAsync();
        await harness.Steps.EventsHandled.Reader.ReadAsync(TestToken);
        await harness.Events.WaitForReadAsync(2);
        harness.Clock.Advance(TimeSpan.FromSeconds(30));
        await harness.Output.WaitForLineAsync(Prefix + "refresh did not finish within 30s");
        release.SetResult();
        await harness.Output.WaitForLineAsync(Prefix + "refresh recovered");
        harness.Events.Send(2);
        await harness.Steps.EventsHandled.Reader.ReadAsync(TestToken);
        await harness.CommandBarrierAsync();
        await harness.QuitAsync(run);

        Assert.Equal(new[] { Prefix + "refresh did not finish within 30s", Prefix + "refresh recovered" },
            harness.Output.Lines);
    }

    [Fact(Timeout = 30000)]
    public async Task SlowTypedCommand_StillPrintsBusyOnce()
    {
        var harness = new OwnerConsoleLoopTestHarness();
        var release = Signal();
        harness.Steps.OnCommand = async (line, _) =>
        {
            if (line == "slow") await release.Task;
            return line != "quit";
        };
        harness.Input.Send("slow");
        var run = harness.RunAsync();
        Assert.Equal("slow", await harness.Steps.Commands.Reader.ReadAsync(TestToken));
        harness.Clock.Advance(TimeSpan.FromSeconds(3));
        await harness.Output.WaitForLineAsync(Prefix + "working: slow ...");
        harness.Clock.Advance(TimeSpan.FromSeconds(3));
        release.SetResult();
        await harness.CommandBarrierAsync();
        await harness.QuitAsync(run);

        Assert.Equal(new[] { Prefix + "working: slow ..." }, harness.Output.Lines);
    }
}
