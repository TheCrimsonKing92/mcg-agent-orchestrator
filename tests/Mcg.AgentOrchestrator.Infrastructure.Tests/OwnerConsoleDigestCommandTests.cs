public sealed class OwnerConsoleDigestCommandTests
{
    [Fact]
    public async Task DigestEchoesEveryLineInOrder()
    {
        var harness = new OwnerConsoleHarness();

        await harness.Session().HandleCommandAsync("digest", CancellationToken.None);

        Assert.Equal(1, harness.DigestReport.Calls);
        var lines = harness.Output.Text.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(new[] { "digest first", "digest second", "digest third" }, lines);
    }

    [Fact]
    public async Task HelpNamesNewCommands()
    {
        var harness = new OwnerConsoleHarness();

        await harness.Session().HandleCommandAsync("help", CancellationToken.None);

        Assert.Contains("conductor", harness.Output.Text);
        Assert.Contains("digest", harness.Output.Text);
    }

    [Fact]
    public async Task DigestRejectsArgumentsWithoutDelegation()
    {
        var harness = new OwnerConsoleHarness();

        Assert.True(await harness.Session().HandleCommandAsync("digest --since today", CancellationToken.None));

        Assert.Equal(0, harness.DigestReport.Calls);
        Assert.Contains("usage: digest", harness.Output.Text);
    }
}
