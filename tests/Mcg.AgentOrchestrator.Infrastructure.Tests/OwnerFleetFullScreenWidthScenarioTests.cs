using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Terminal.Gui.Text;

// Unique filesystem roots and per-controller fakes; safe to run in parallel.
public sealed class OwnerFleetFullScreenWidthScenarioTests
{
    [Theory]
    [InlineData(118, 118)]
    [InlineData(null, 60)]
    public async Task FleetUsesDialogBodyWidthAndRealLedger(int? available, int expected)
    {
        using var fixture = new FleetFixture();
        var harness = new OwnerConsoleHarness();
        var dialogs = new Dialogs(available);
        var loads = 0;
        using var controller = OwnerConsoleControllerFactory.Create(harness.Questions, harness.Answers, dialogs,
            harness.State, harness.Tail, harness.Conductor, harness.DigestReport, harness.Digest, harness.Clock,
            fleet: () => { loads++; return OwnerFleetViewModelBuilder.Load(fixture.Workspace, harness.Liveness, new FleetFixture.Clock()); });

        await controller.RunCommandAsync(":fleet");

        Assert.Equal(1, loads);
        var shown = Assert.Single(dialogs.Texts);
        Assert.Equal("Fleet", shown.Title);
        FleetFixture.AssertDiagram(shown.Text, expected);
    }

    private sealed class Dialogs(int? width) : IOwnerConsoleDialogs
    {
        public int? TextWidth => width;
        internal List<(string Title, string Text)> Texts { get; } = [];
        public Task<bool> ConfirmAsync(string title, string text) => throw new InvalidOperationException("unexpected confirmation");
        public Task<string?> PromptTextAsync(string title, string text) => throw new InvalidOperationException("unexpected prompt");
        public Task ShowTextAsync(string title, string text)
        { Texts.Add((title, text)); return Task.CompletedTask; }
    }

    internal sealed class FleetFixture : IDisposable
    {
        internal const string Lane = "infrastructure tests: Goal lifecycle commands";
        private readonly string _root = Path.Combine(Path.GetTempPath(), "fleet-width-" + Guid.NewGuid().ToString("N"));
        internal OrchestratorWorkspace Workspace { get; }

        internal FleetFixture(bool skipped = true)
        {
            var directory = Path.Combine(_root, ".orchestrator");
            Directory.CreateDirectory(directory);
            Workspace = OrchestratorWorkspace.ForDirectory(_root);
            File.WriteAllText(Path.Combine(directory, "remote-lane-executors.json"),
                """{"executors":[{"id":"mcg-exec-1","slots":2},{"id":"mcg-exec-2","slots":1}],"lanes":[]}""");
            var rows = new List<string>
            {
                """{"observed_at":"2026-10-17T12:00:00Z","executor_id":"mcg-exec-1","gate_attempt_id":"real","lane":"infrastructure tests: Goal lifecycle commands","outcome":"accepted","reason":null,"attempt":{"last_status":{"seconds":47.3}}}"""
            };
            if (skipped) rows.Add(
                """{"observed_at":"2026-10-17T12:01:00Z","executor_id":"","gate_attempt_id":"skip","lane":"lane","outcome":"shadow-skipped-no-executor","reason":null}""");
            File.WriteAllLines(Path.Combine(directory, RemoteExecutorHealthLedger.FileName), rows);
        }

        internal RemoteExecutorReport Read() => RemoteExecutorReportReader.Read(Workspace, null, 10);

        internal sealed class Clock : TimeProvider
        {
            public override DateTimeOffset GetUtcNow() => DateTimeOffset.Parse("2026-10-17T18:00:00Z");
        }

        internal static void AssertDiagram(string text, int width)
        {
            var rows = text.Split('\n');
            Assert.All(rows, row => Assert.Equal(width, row.GetColumns()));
            Assert.Equal(3, text.Count(ch => ch is '┌' or '╔'));
            Assert.Contains("executors: 2", text);
            Assert.Contains("shadow skipped, no free slot: 1", text);
            Assert.DoesNotContain(rows, row => row.Trim('│', '║', ' ') == "Executor");
            var first = Array.FindIndex(rows, row => row.Contains("infrastructure", StringComparison.Ordinal));
            Assert.True(first >= 0);
            var laneRows = rows.Skip(first).TakeWhile(row => !row.StartsWith("│   solo", StringComparison.Ordinal)).ToArray();
            Assert.NotEmpty(laneRows);
            Assert.DoesNotContain('…', string.Join('\n', laneRows));
            var laneText = string.Join(" ", laneRows.Select(row => row[2..^2].Trim()));
            Assert.Equal(Lane + " median 47.3s n=1", laneText);
            if (width >= 100)
            {
                Assert.Contains(Lane, rows[first]);
                Assert.DoesNotContain('…', text);
            }
        }

        public void Dispose() => Directory.Delete(_root, true);
    }
}
