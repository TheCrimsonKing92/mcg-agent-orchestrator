using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Private filesystem roots and per-session fakes; no shared terminal or process state.
public sealed class OwnerConsoleFleetCommandTests
{
    [Fact]
    public async Task FleetReadsFixtureLedgersWithoutChangingWorkspace()
    {
        var root = Path.Combine(Path.GetTempPath(), "owner-fleet-" + Guid.NewGuid().ToString("N"));
        var directory = Path.Combine(root, ".orchestrator");
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "remote-lane-executors.json"),
                """{"executors":[{"id":"alpha","slots":2},{"id":"beta","slots":1}],"lanes":[]}""");
            File.WriteAllText(Path.Combine(directory, RemoteExecutorHealthLedger.FileName),
                """{"observed_at":"2026-10-16T12:00:00Z","executor_id":"alpha","gate_attempt_id":"a","lane":"lane","outcome":"accepted","reason":null,"attempt":{"last_status":{"seconds":12.3},"steps":[{"started_at":"2026-10-16T11:59:00Z"}]}}""" + "\n");
            var at = new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);
            var probePath = Path.Combine(directory, RemoteExecutorProbeLedger.FileName);
            RemoteExecutorProbeLedger.Append(probePath, new(at, "alpha", true, 0, false, "Ready", true, [], ""));
            RemoteExecutorProbeLedger.Append(probePath, new(at, "beta", false, 1, true, null, null, ["ssh-timeout"], ""));
            // Snapshot after fixture setup; the command must create neither files nor directories.
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            var files = Directory.GetFiles(root, "*", SearchOption.AllDirectories).Order().ToArray();
            var before = files.ToDictionary(path => path, path => (Bytes: File.ReadAllBytes(path), Written: File.GetLastWriteTimeUtc(path)));
            var directories = Directory.GetDirectories(root, "*", SearchOption.AllDirectories).Order().ToArray();
            var harness = new OwnerConsoleHarness();
            using var session = Session(harness, () => OwnerFleetViewModelBuilder.Load(workspace, harness.Liveness));

            Assert.True(await session.HandleCommandAsync("fleet", CancellationToken.None));

            Assert.Contains("┌", harness.Output.Text);
            Assert.Contains("Executor alpha", harness.Output.Text);
            Assert.Contains("Executor beta", harness.Output.Text);
            Assert.Contains("UNREACHABLE", harness.Output.Text);
            Assert.Contains("lane median 12.3s", harness.Output.Text);
            Assert.StartsWith("┌", harness.Output.Text);
            Assert.Equal(files, Directory.GetFiles(root, "*", SearchOption.AllDirectories).Order().ToArray());
            Assert.Equal(directories, Directory.GetDirectories(root, "*", SearchOption.AllDirectories).Order().ToArray());
            foreach (var path in files)
            {
                Assert.Equal(before[path].Bytes, File.ReadAllBytes(path));
                Assert.Equal(before[path].Written, File.GetLastWriteTimeUtc(path));
            }
            Assert.Empty(harness.Answers.Calls);
            Assert.Empty(harness.Conductor.Calls);
            Assert.Empty(harness.State.Calls);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task FullScreenFleetOpensDiagramInTextDialog()
    {
        var harness = new OwnerConsoleHarness();
        var dialogs = new Dialogs();
        var loads = 0;
        using var controller = OwnerConsoleControllerFactory.Create(harness.Questions, harness.Answers, dialogs,
            harness.State, harness.Tail, harness.Conductor, harness.DigestReport, harness.Digest, harness.Clock,
            fleet: () =>
            {
                loads++;
                return OwnerFleetViewModelBuilder.Build(RemoteExecutorReport.Build(["laptop"], [], []), true, TimeZoneInfo.Utc);
            });

        await controller.RunCommandAsync(":fleet");

        Assert.Equal(1, loads);
        var shown = Assert.Single(dialogs.Texts);
        Assert.Equal("Fleet", shown.Title);
        Assert.Contains("Orchestrator host", shown.Text);
        Assert.Contains("Executor laptop", shown.Text);
        Assert.StartsWith("┌", shown.Text);
        Assert.Empty(harness.Answers.Calls);
        Assert.Empty(harness.Conductor.Calls);
        Assert.Empty(harness.State.Calls);
    }

    [Fact]
    public async Task FleetRejectsArgumentsWithoutLoadingReport()
    {
        var harness = new OwnerConsoleHarness();
        using var session = Session(harness, () => throw new InvalidOperationException("must not load"));
        Assert.True(await session.HandleCommandAsync("fleet extra", CancellationToken.None));
        Assert.Contains("usage: fleet", harness.Output.Text);
    }

    [Fact]
    public async Task FleetWithoutSourceReportsUnavailable()
    {
        var harness = new OwnerConsoleHarness();
        using var session = harness.Session();
        Assert.True(await session.HandleCommandAsync("fleet", CancellationToken.None));
        Assert.Contains("fleet view unavailable in this session", harness.Output.Text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FleetReadFailureKeepsSessionUsable(bool denied)
    {
        var harness = new OwnerConsoleHarness();
        using var session = Session(harness, () => throw (denied ? new UnauthorizedAccessException("denied") : new IOException("unreadable")));
        Assert.True(await session.HandleCommandAsync("fleet", CancellationToken.None));
        Assert.Contains("fleet unavailable:", harness.Output.Text);
        Assert.True(await session.HandleCommandAsync("help", CancellationToken.None));
        Assert.Contains("fleet: Show the fleet diagram", harness.Output.Text);
    }

    private static OwnerConsoleSession Session(OwnerConsoleHarness harness, Func<OwnerFleetViewModel> fleet) => new(
        harness.State, harness.Questions, harness.Answers, harness.Liveness, harness.Digest, harness.Tail,
        harness.Output, harness.Clock, harness.Conductor, harness.DigestReport, fleet: fleet);

    private sealed class Dialogs : IOwnerConsoleDialogs
    {
        internal readonly List<(string Title, string Text)> Texts = [];
        public Task<bool> ConfirmAsync(string title, string text) => throw new InvalidOperationException("unexpected confirmation");
        public Task<string?> PromptTextAsync(string title, string text) => throw new InvalidOperationException("unexpected prompt");
        public Task ShowTextAsync(string title, string text)
        { Texts.Add((title, text)); return Task.CompletedTask; }
    }
}
