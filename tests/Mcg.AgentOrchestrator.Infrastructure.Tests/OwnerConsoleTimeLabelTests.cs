using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;

// Parallel-safe: these tests use only instance-local fake sessions, clocks and output.
public sealed class OwnerConsoleTimeLabelTests
{
    private static CancellationToken TestToken => TestContext.Current.CancellationToken;
    private const string Prefix = "[05:35:00] ";
    private const string GoalId = "11111111111111111111111111111111";

    [Fact]
    public async Task StartupAnnouncements_UseInjectedLocalTimeAndRowsStayRaw()
    {
        var fixture = new Fixture();
        fixture.Harness.AddGoal(GoalId, "Build search", AgentRole.Developer);
        fixture.Harness.Questions.Items.Add(new OwnerQuestion("wait-1", GoalId,
            OwnerQuestionKind.HumanInput, "Ship it?"));

        await fixture.Session.StartAsync(null, TestToken);

        var lines = Lines(fixture.Harness.Output.Text);
        Assert.Equal(Prefix + "Owner digest: landed=2 pending=1", lines[0]);
        Assert.Equal("\a" + Prefix + "[1] 11111111 Ship it?", lines[1]);
        Assert.Equal(Prefix + "conductor: running | active goals: 1 | owner questions: 1 | last event: unknown", lines[2]);
        Assert.Equal(Prefix + "board | active goals: 1 | owner questions: 1", lines[3]);
        Assert.StartsWith("11111111 | Build search | Active | Developer | ", lines[4]);
        Assert.Equal(Prefix + "Type help for commands.", lines[5]);
        Assert.Equal(6, lines.Length);
    }

    [Fact]
    public async Task HelpAndGoal_PrefixOnlyTheirFirstOutputLine()
    {
        var fixture = new Fixture();
        fixture.Harness.AddGoal(GoalId, "Build search", AgentRole.Developer);
        await fixture.Session.HandleCommandAsync("help", TestToken);
        await fixture.Session.HandleCommandAsync("goal 11111111", TestToken);

        var lines = Lines(fixture.Harness.Output.Text);
        Assert.Equal(Prefix + "board | goal <id-prefix> | answer <n> <text> | accept <n> | bell on|off | help | quit", lines[0]);
        Assert.Equal("conductor start [--clear-stop] | conductor stop [--yes] | conductor status", lines[1]);
        Assert.Equal("digest | metrics | board", lines[2]);
        Assert.Equal(Prefix + GoalId + " | Build search | Active", lines[3]);
        Assert.Equal("recent event", lines[4]);
        Assert.Equal(5, lines.Length);
    }

    [Fact]
    public async Task Digest_ReprintsFreshSummaryHeaderAndBoardWithoutReport()
    {
        var fixture = new Fixture();
        await fixture.Session.StartAsync(null, TestToken);
        var before = fixture.Harness.Output.Text.Length;
        fixture.Harness.AddGoal(GoalId, "New work", AgentRole.Developer);
        fixture.Liveness.Running = false;
        fixture.Digest.Line = "Owner digest: landed=3 pending=2";

        await fixture.Session.HandleCommandAsync("digest", TestToken);

        var lines = Lines(fixture.Harness.Output.Text[before..]);
        Assert.Equal(Prefix + "Owner digest: landed=3 pending=2", lines[0]);
        Assert.Equal(Prefix + "conductor: stopped | active goals: 1 | owner questions: 0 | last event: unknown", lines[1]);
        Assert.Equal(Prefix + "board | active goals: 1 | owner questions: 0", lines[2]);
        Assert.StartsWith("11111111 | New work | Active | Developer | ", lines[3]);
        Assert.Equal(4, lines.Length);
        Assert.Equal(0, fixture.Harness.DigestReport.Calls);
        Assert.Equal(2, fixture.Digest.Calls);
    }

    [Fact]
    public async Task Metrics_UsesReportSeamAndPrefixesOnlyFirstLine()
    {
        var fixture = new Fixture();

        await fixture.Session.HandleCommandAsync("metrics", TestToken);

        Assert.Equal(1, fixture.Harness.DigestReport.Calls);
        Assert.Equal(new[] { Prefix + "digest first", "digest second", "digest third" },
            Lines(fixture.Harness.Output.Text));
    }

    [Fact]
    public async Task CommandUsageAndConductorOutput_UseAnnouncementFormatting()
    {
        var fixture = new Fixture();
        await fixture.Session.HandleCommandAsync("metrics extra", TestToken);
        await fixture.Session.HandleCommandAsync("digest extra", TestToken);
        await fixture.Session.HandleCommandAsync("conductor status", TestToken);

        Assert.Equal(new[]
        {
            Prefix + "usage: metrics", Prefix + "usage: digest",
            Prefix + "conductor output 1", "conductor error 1"
        }, Lines(fixture.Harness.Output.Text));
        Assert.Equal(0, fixture.Harness.DigestReport.Calls);
        Assert.Single(fixture.Harness.Conductor.Calls);
    }

    [Fact(Timeout = 30000)]
    public async Task LoopNotice_UsesInjectedLocalTime()
    {
        var harness = new OwnerConsoleLoopTestHarness();
        harness.Clock.Zone = Zone();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
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
        release.SetResult();
        await harness.QuitAsync(run);

        Assert.Equal(new[] { Prefix + "working: slow ..." }, harness.Output.Lines);
    }

    [Fact]
    public void Formatter_PreservesBlankSeparatorsAndReadsClockForEachAnnouncement()
    {
        var clock = new OwnerConsoleTestClock { Zone = Zone() };
        Assert.Equal("", ConsoleAnnouncementFormatter.Format(clock, ""));
        Assert.Equal("  ", ConsoleAnnouncementFormatter.Format(clock, "  "));
        Assert.Equal(Prefix + "first", ConsoleAnnouncementFormatter.Format(clock, "first"));
        clock.Now = clock.Now.AddSeconds(1);
        Assert.Equal("[05:35:01] second", ConsoleAnnouncementFormatter.Format(clock, "second"));
    }

    private static string[] Lines(string text) => text.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
    private static TimeZoneInfo Zone() => TimeZoneInfo.CreateCustomTimeZone(
        "owner-console-test", TimeSpan.FromMinutes(330), "Owner console test", "Owner console test");

    private sealed class Fixture
    {
        internal OwnerConsoleHarness Harness { get; } = new();
        internal OwnerConsoleTestClock Clock { get; } = new() { Zone = Zone() };
        internal FakeLiveness Liveness { get; } = new();
        internal FakeDigest Digest { get; } = new();
        internal OwnerConsoleSession Session { get; }
        internal Fixture() => Session = new(Harness.State, Harness.Questions, Harness.Answers,
            Liveness, Digest, Harness.Tail, Harness.Output, Clock, Harness.Conductor, Harness.DigestReport);
    }

    private sealed class FakeLiveness : IConductorLiveness
    {
        internal bool Running { get; set; } = true;
        public bool IsRunning() => Running;
    }

    private sealed class FakeDigest : IOwnerDigestSummary
    {
        internal string Line { get; set; } = "Owner digest: landed=2 pending=1";
        internal int Calls { get; private set; }
        public IReadOnlyList<string> ReadSummaryLines()
        {
            Calls++;
            return [Line];
        }
    }
}
