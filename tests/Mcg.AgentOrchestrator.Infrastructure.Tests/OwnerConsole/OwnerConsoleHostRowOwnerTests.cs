using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Terminal.Gui.App;

// Parallel-safe: instance-local headless UI, fake stores and paths; no shared state or processes.
public sealed class OwnerConsoleHostRowOwnerTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "owner-row-fixture");
    private static readonly string Home = Path.Combine(Root, "home", ".orchestrator");
    private static readonly string Alpha = Path.Combine(Root, "projects", "alpha");
    private static readonly string Beta = Path.Combine(Root, "projects", "beta");
    private const string Attempt = "12345678-gate-20261028120000000-11111111111111111111111111111111";
    private static OwnerConsoleHostRowOwner Owners() => new(Home, [("alpha", Alpha), ("beta", Beta)]);

    [Theory]
    [InlineData(false, "home")]
    [InlineData(true, "alpha")]
    public async Task WorkspaceLabel_AppearsInPlainHeaderAndWindowTitle(bool scoped, string expected)
    {
        var label = OwnerConsoleProjectLabel.ForWorkspace(Workspace(scoped));
        Assert.Equal(expected, label);
        var harness = new OwnerConsoleHarness();
        using var session = Session(harness, label);
        await session.StartAsync(null, TestContext.Current.CancellationToken);
        Assert.Contains($"project: {expected} | conductor:", harness.Output.Text);

        using IApplication app = Application.Create();
        var controller = OwnerConsoleControllerFactory.Create(harness.Questions, harness.Answers, new Dialogs(),
            harness.State, harness.Tail, harness.Conductor, harness.DigestReport, harness.Digest, harness.Clock);
        using var view = new OwnerConsoleFullScreenView(app, controller, () => Task.CompletedTask, projectLabel: label);
        Assert.Equal($"Owner console - {expected}", view.Window.Title);
    }

    [Theory]
    [InlineData(false, "beta")]
    [InlineData(true, "home")]
    public void HeartbeatFromKnownWorkspace_RendersItsOwner(bool home, string expected)
    {
        var lane = Lane(Path.Combine(home ? Home : Beta, "acceptance-gate-attempts", Attempt + ".heartbeat.json"));
        var text = Where(Owners(), lane);
        Assert.Contains($"executor 1/2 [#####-----] owner: {expected}", text);
        Assert.Contains("capacity owner: host", text);
        Assert.Contains("local 0/4 [----------] owner: host", text);
        Assert.DoesNotContain("owner: alpha", text);
    }

    [Fact]
    public void IdleCapacity_RendersHostForEveryGroup()
    {
        var text = Where(Owners());
        Assert.Contains("capacity owner: host", text);
        Assert.Contains("executor 0/2 [----------] owner: host", text);
        Assert.Contains("local 0/4 [----------] owner: host", text);
        Assert.DoesNotContain("owner: unknown", text);
    }

    [Fact]
    public async Task RowWithoutProjectEvidence_WhileConsoleReadsNamedProject_RendersUnknownNotCurrentProject()
    {
        var harness = new OwnerConsoleHarness();
        using var session = Session(harness, OwnerConsoleProjectLabel.ForWorkspace(Workspace(true)));
        await session.StartAsync(null, TestContext.Current.CancellationToken);
        Assert.Contains("project: alpha | conductor:", harness.Output.Text);
        foreach (var path in new string?[] { null, Path.Combine(Root, "unregistered", Attempt + ".heartbeat.json") })
        {
            var text = Where(Owners(), Lane(path));
            Assert.Contains("executor 1/2 [#####-----] owner: unknown", text);
            Assert.DoesNotContain("owner: alpha", text);
        }
    }

    [Theory]
    [InlineData(false, "host")]
    [InlineData(true, "unknown")]
    public void FleetAttemptOnlyEvidence_RendersUnknownAndIdleCapacityRendersHost(bool active, string expected)
    {
        RemoteExecutorOutcomeRow[] attempts = active ?
            [new(new DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.Zero), "executor", Attempt, "lane", "accepted", null)] : [];
        var report = RemoteExecutorReport.Build(["executor"], attempts, [], configuredSlots: new Dictionary<string, int> { ["executor"] = 2 });
        var model = OwnerFleetViewModelBuilder.Build(report, false, TimeZoneInfo.Utc, owners: Owners());
        Assert.Equal(expected, Assert.Single(model.Executors).Owner);
        var text = OwnerFleetDiagram.Render(model);
        Assert.Contains($"owner: {expected}", text);
        Assert.DoesNotContain("owner: alpha", text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NestedDirectories_UseMostSpecificMapping(bool homeInsideProject)
    {
        var nested = Path.Combine(Alpha, "nested");
        var owners = homeInsideProject ? new OwnerConsoleHostRowOwner(nested, [("alpha", Alpha)]) :
            new OwnerConsoleHostRowOwner(Alpha, [("beta", nested)]);
        Assert.Equal(homeInsideProject ? "home" : "beta", owners.ForPath(Path.Combine(nested, "tenants", "test", "heartbeat.json")));
        Assert.Equal(homeInsideProject ? "alpha" : "home", owners.ForPath(Path.Combine(Alpha, "heartbeat.json")));
    }

    [Fact]
    public void ConflictingOrMissingEvidence_StaysUnknown()
    {
        var owners = Owners();
        foreach (var path in new string?[] { null, "", "relative/heartbeat.json", "bad\0path", Attempt,
                     Path.Combine(Beta + "-other", "heartbeat.json"), Path.Combine(Beta, "..", "outside", "heartbeat.json") })
            Assert.Equal("unknown", owners.ForPath(path));
        var conflicting = new OwnerConsoleHostRowOwner(null, [("alpha", Alpha), ("beta", Alpha)]);
        Assert.Equal("unknown", conflicting.ForPath(Path.Combine(Alpha, "heartbeat.json")));
        Assert.Equal("unknown", owners.ForAttempts([Attempt]));
        Assert.Equal("unknown", owners.ForRunningLanes([Lane(Path.Combine(Alpha, "heartbeat.json")), Lane(Path.Combine(Beta, "heartbeat.json"))]));
        Assert.Equal("unknown", owners.ForRunningLanes([Lane(Path.Combine(Alpha, "heartbeat.json")), Lane(null)]));
    }

    [Theory]
    [InlineData("remote-other")]
    [InlineData("local slot 1")]
    public void UnconfiguredAndLocalOccupancy_ResolveHeartbeatOwners(string location)
    {
        var text = Where(Owners(), Lane(Path.Combine(Beta, "heartbeat.json")) with { Location = location });
        Assert.Contains(location.StartsWith("local", StringComparison.Ordinal) ?
            "local 1/4 [##--------] owner: beta" : "remote-other 1 running owner: beta", text);
    }

    private static OwnerConsoleSession Session(OwnerConsoleHarness harness, string label) =>
        new(harness.State, harness.Questions, harness.Answers, harness.Liveness, harness.Digest, harness.Tail,
            harness.Output, harness.Clock, projectLabel: label);

    private static OrchestratorWorkspace Workspace(bool scoped) =>
        new(Root, Root, scoped ? Alpha : Home, scoped ? "alpha" : "default", scoped, "default", false,
            "", "", "", "", "", "", "");

    private static OwnerConsoleGateLane Lane(string? path) =>
        new("lane", OwnerConsoleGateLaneState.Running, "unknown", "executor", TimeSpan.Zero, null, false, HeartbeatPath: path);

    private static string Where(OwnerConsoleHostRowOwner owners, params OwnerConsoleGateLane[] lanes)
    {
        var gate = new OwnerConsoleGateProgress(true, "solo", [], null, TimeSpan.Zero, lanes, 0, 0, 0, lanes.Length,
            Capacity: new([new("executor", 2)], 4, owners));
        return string.Join("\n", OwnerConsoleGateWhereLine.Lines(gate, lanes, 120));
    }

    private sealed class Dialogs : IOwnerConsoleDialogs
    {
        public Task<bool> ConfirmAsync(string title, string text) => Task.FromResult(false);
        public Task<string?> PromptTextAsync(string title, string text) => Task.FromResult<string?>(null);
        public Task ShowTextAsync(string title, string text) => Task.CompletedTask;
    }
}
