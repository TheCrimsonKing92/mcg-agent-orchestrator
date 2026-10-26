public sealed class OwnerConsoleConductorCommandTests
{
    [Fact]
    public async Task StatusAndStartDelegateCliArgumentsAndEchoOutput()
    {
        var harness = new OwnerConsoleHarness();
        var session = harness.Session();

        await session.HandleCommandAsync("conductor status", CancellationToken.None);
        await session.HandleCommandAsync("conductor start", CancellationToken.None);
        await session.HandleCommandAsync("conductor start --clear-stop", CancellationToken.None);

        Assert.Equal(3, harness.Conductor.Calls.Count);
        Assert.Equal(new[] { "conductor", "status" }, harness.Conductor.Calls[0]);
        Assert.Equal(new[] { "conductor", "start" }, harness.Conductor.Calls[1]);
        Assert.Equal(new[] { "conductor", "start", "--clear-stop" }, harness.Conductor.Calls[2]);
        for (var number = 1; number <= 3; number++)
        {
            Assert.Contains($"conductor output {number}", harness.Output.Text);
            Assert.Contains($"conductor error {number}", harness.Output.Text);
        }
    }

    [Fact]
    public async Task StopRequiresYesOnEveryInvocation()
    {
        var harness = new OwnerConsoleHarness();
        var session = harness.Session();

        await session.HandleCommandAsync("conductor stop", CancellationToken.None);
        Assert.Empty(harness.Conductor.Calls);
        Assert.Contains("This is a detach, not a drain", harness.Output.Text);
        Assert.Contains("repeat as: conductor stop --yes", harness.Output.Text);

        await session.HandleCommandAsync("conductor stop --yes", CancellationToken.None);
        Assert.Single(harness.Conductor.Calls);
        Assert.Equal(new[] { "conductor", "stop" }, harness.Conductor.Calls[0]);

        await session.HandleCommandAsync("conductor stop", CancellationToken.None);
        Assert.Single(harness.Conductor.Calls);
    }

    [Theory]
    [InlineData("conductor")]
    [InlineData("conductor restart")]
    [InlineData("conductor start --clear-stop extra")]
    [InlineData("conductor status --yes")]
    [InlineData("conductor stop --clear-stop")]
    public async Task InvalidArgumentsShowUsageWithoutDelegation(string command)
    {
        var harness = new OwnerConsoleHarness();

        Assert.True(await harness.Session().HandleCommandAsync(command, CancellationToken.None));

        Assert.Empty(harness.Conductor.Calls);
        Assert.Contains("usage: conductor start", harness.Output.Text);
    }
}
