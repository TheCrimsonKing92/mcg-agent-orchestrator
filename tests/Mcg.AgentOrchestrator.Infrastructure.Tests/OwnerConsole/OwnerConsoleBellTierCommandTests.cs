using Mcg.AgentOrchestrator.App.OwnerConsole;

// Parallel-safe: fake dialogs, notifier, model and manual clock are owned by each test.
public sealed class OwnerConsoleBellTierCommandTests
{
    [Fact]
    public async Task AllAndPerTierCommandsControlPlaybackAndCanonicalOutput()
    {
        var harness = new OwnerConsoleHarness();
        var clock = new ManualClock();
        var dialogs = new Dialogs();
        var notifier = new RecordingNotifier();
        using var controller = Controller(harness, dialogs, clock, notifier);
        var model = Model(3);
        controller.Apply(model);
        await controller.RunCommandAsync("bell off");
        Assert.False(controller.BellEnabled);
        Assert.Equal("off", controller.BellStatusText);
        Assert.Equal("bell off", dialogs.Texts[^1].Trim());
        model = Model(4);
        controller.Apply(model);
        Assert.Empty(notifier.Calls);
        await controller.RunCommandAsync("bell on");
        Assert.True(controller.BellEnabled);
        Assert.Equal("on", controller.BellStatusText);
        Assert.Equal("bell on", dialogs.Texts[^1].Trim());
        controller.Apply(model);
        Assert.Empty(notifier.Calls);

        await controller.RunCommandAsync("bell attention off");
        Assert.True(controller.BellEnabled);
        Assert.Equal("calm on, attention off, alarm on", controller.BellStatusText);
        Assert.Equal("bell attention off", dialogs.Texts[^1].Trim());
        var question = new OwnerConsoleDecision("q", 1, "goal", "goal", OwnerQuestionKind.HumanInput,
            "Answer?", "Answer?", null, null, null, clock.Now.AddMinutes(-29));
        model = model with { Decisions = [question] };
        controller.Apply(model);
        clock.Now = clock.Now.AddMinutes(2);
        controller.Apply(model);
        Assert.Empty(notifier.Calls);
        controller.Apply(model with { Status = model.Status with { LandedToday = 5 } });
        Assert.Equal([OwnerConsoleNotificationTier.Calm], notifier.Calls);
        await controller.RunCommandAsync("bell attention on");
        controller.Apply(controller.Model!);
        Assert.Equal([OwnerConsoleNotificationTier.Calm], notifier.Calls);

        await controller.RunCommandAsync("bell alarm off");
        Assert.Equal("calm on, attention on, alarm off", controller.BellStatusText);
        await controller.RunCommandAsync("bell alarm on");
        Assert.Equal("on", controller.BellStatusText);
        await controller.RunCommandAsync("bell calm off");
        Assert.Equal("calm off, attention on, alarm on", controller.BellStatusText);
        await controller.RunCommandAsync(" :BeLl CaLm ON ");
        Assert.Equal("on", controller.BellStatusText);
        Assert.Equal("bell calm on", dialogs.Texts[^1].Trim());
    }

    [Theory]
    [InlineData("bell loud on")]
    [InlineData("bell calm maybe")]
    [InlineData("bell")]
    [InlineData("bell calm")]
    [InlineData("bell on extra")]
    public async Task InvalidCommandPrintsUsageAndKeepsState(string command)
    {
        var harness = new OwnerConsoleHarness();
        var dialogs = new Dialogs();
        using var controller = Controller(harness, dialogs, new ManualClock());
        await controller.RunCommandAsync(command);
        Assert.Equal("usage: bell on|off | bell <calm|attention|alarm> on|off", Assert.Single(dialogs.Texts).Trim());
        Assert.Equal("on", controller.BellStatusText);
    }

    [Fact]
    public void MissingOrThrowingNotifierDoesNotBreakModelApplication()
    {
        var harness = new OwnerConsoleHarness();
        using var silent = Controller(harness, new Dialogs(), new ManualClock());
        silent.Apply(Model(0));
        var landed = Model(1);
        silent.Apply(landed);
        Assert.Same(landed, silent.Model);
        var broken = new RecordingNotifier { Throw = true };
        using var controller = Controller(harness, new Dialogs(), new ManualClock(), broken);
        controller.Apply(Model(0));
        controller.Apply(landed);
        Assert.Equal([OwnerConsoleNotificationTier.Calm], broken.Calls);
        Assert.Same(landed, controller.Model);
    }

    private static OwnerConsoleScreenController Controller(OwnerConsoleHarness harness, Dialogs dialogs,
        TimeProvider clock, IOwnerConsoleNotifier? notifier = null) => OwnerConsoleControllerFactory.Create(harness.Questions, harness.Answers,
        dialogs, harness.State, harness.Tail, harness.Conductor, harness.DigestReport, harness.Digest, clock, notifier: notifier);
    private static OwnerConsoleViewModel Model(int landed) => new(new(false, 0, 0, 0, null, landed), [], [], []);

    private sealed class ManualClock : TimeProvider
    {
        internal DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class RecordingNotifier : IOwnerConsoleNotifier
    {
        internal readonly List<OwnerConsoleNotificationTier> Calls = [];
        internal bool Throw;
        public void Notify(OwnerConsoleNotificationTier tier)
        {
            Calls.Add(tier);
            if (Throw) throw new IOException("sound failed");
        }
    }
    private sealed class Dialogs : IOwnerConsoleDialogs
    {
        internal readonly List<string> Texts = [];
        public Task<bool> ConfirmAsync(string title, string text) => Task.FromResult(false);
        public Task<string?> PromptTextAsync(string title, string text) => Task.FromResult<string?>(null);
        public Task ShowTextAsync(string title, string text) { Texts.Add(text); return Task.CompletedTask; }
    }
}
