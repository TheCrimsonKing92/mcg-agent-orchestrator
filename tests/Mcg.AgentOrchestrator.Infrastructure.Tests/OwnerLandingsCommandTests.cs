using Mcg.AgentOrchestrator.App.OwnerConsole;
using Fixture = OwnerLandingsQueryTests.Fixture;

// Parallel-safe: per-session fakes and private attempt fixtures; no actual terminal.
public sealed class OwnerLandingsCommandTests
{
    [Theory]
    [InlineData("landings", "")]
    [InlineData(":landings today", "today")]
    [InlineData("landings 30", "30")]
    public async Task RunCommand_SingleArgument_ShowsSeamOutput(string command, string argument)
    {
        var harness = new OwnerConsoleHarness();
        var dialogs = new Dialogs();
        var calls = new List<string>();
        using var controller = Controller(harness, dialogs, value => { calls.Add(value); return "delivered rows"; });

        await controller.RunCommandAsync(command);

        Assert.Equal(new[] { argument }, calls);
        Assert.Equal(("Landings", "delivered rows"), Assert.Single(dialogs.Texts));
    }

    [Fact]
    public async Task RunCommand_NoSeam_ReportsUnavailable()
    {
        var harness = new OwnerConsoleHarness();
        var dialogs = new Dialogs();
        using var controller = Controller(harness, dialogs);

        await controller.RunCommandAsync("landings");

        var shown = Assert.Single(dialogs.Texts);
        Assert.Contains("landings view unavailable in this session", shown.Text);
        Assert.DoesNotContain("usage:", shown.Text);
        Assert.DoesNotContain("unknown command", shown.Text);
    }

    [Fact]
    public async Task RunCommand_ExtraArguments_ReturnsUsageWithoutLoading()
    {
        var harness = new OwnerConsoleHarness();
        var dialogs = new Dialogs();
        using var controller = Controller(harness, dialogs, _ => throw new InvalidOperationException("must not load"));

        await controller.RunCommandAsync("landings a b");

        Assert.Equal("usage: landings [today|30]", Assert.Single(dialogs.Texts).Text.Trim());
    }

    [Fact]
    public async Task RunCommand_QuerySeam_ReadsEvidenceWithoutChangingFiles()
    {
        using var fixture = new Fixture();
        var goal = fixture.Add("10000001", fixture.Clock.Now);
        fixture.WriteAttempt(goal, "accepted-attempt", fixture.Clock.Now.AddMinutes(-1));
        var files = Directory.GetFiles(fixture.DirectoryPath, "*", SearchOption.AllDirectories).Order().ToArray();
        Assert.NotEmpty(files);
        var bytes = files.ToDictionary(path => path, File.ReadAllBytes);
        var directories = Directory.GetDirectories(fixture.DirectoryPath, "*", SearchOption.AllDirectories).Order().ToArray();
        var harness = new OwnerConsoleHarness();
        var dialogs = new Dialogs();
        using var controller = Controller(harness, dialogs,
            arg => OwnerLandingsQuery.Text(fixture, fixture.DirectoryPath, fixture.Clock, arg));

        await controller.RunCommandAsync("landings");

        Assert.Equal(1, fixture.Reads);
        var shown = Assert.Single(dialogs.Texts);
        Assert.Equal("Landings", shown.Title);
        Assert.Contains("10000001  2026-10-16 12:00 local  ccccccccc  solo", shown.Text);
        Assert.Contains("acceptance accepted passed 2026-10-16 11:59 local", shown.Text);
        Assert.Equal(files, Directory.GetFiles(fixture.DirectoryPath, "*", SearchOption.AllDirectories).Order().ToArray());
        Assert.Equal(directories, Directory.GetDirectories(fixture.DirectoryPath, "*", SearchOption.AllDirectories).Order().ToArray());
        foreach (var path in files) Assert.Equal(bytes[path], File.ReadAllBytes(path));
        Assert.Empty(harness.Answers.Calls);
        Assert.Empty(harness.Conductor.Calls);
        Assert.Empty(harness.State.Calls);
    }

    private static OwnerConsoleScreenController Controller(OwnerConsoleHarness harness,
        Dialogs dialogs, Func<string, string>? seam = null) => new(harness.Questions, harness.Answers, dialogs,
        harness.State, harness.Tail, harness.Conductor, harness.DigestReport, harness.Digest, harness.Clock, landings: seam);

    private sealed class Dialogs : IOwnerConsoleDialogs
    {
        internal List<(string Title, string Text)> Texts { get; } = [];
        public Task<bool> ConfirmAsync(string title, string text) => throw new InvalidOperationException("unexpected confirmation");
        public Task<string?> PromptTextAsync(string title, string text) => throw new InvalidOperationException("unexpected prompt");
        public Task ShowTextAsync(string title, string text)
        { Texts.Add((title, text)); return Task.CompletedTask; }
    }
}
