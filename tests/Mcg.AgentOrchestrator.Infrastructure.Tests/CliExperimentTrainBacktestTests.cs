using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: private workspaces and AsyncLocal console capture.
public sealed class CliExperimentTrainBacktestTests
{
    [Fact]
    public void Execute_StoredFixture_PrintsConfirmedReportWithoutWritingDatabase()
    {
        WithWorkspace(workspace =>
        {
            var receipts = MergeTrainDropBacktestTests.Fixture.Confirmed();
            var path = Path.Combine(workspace.OrchestratorDirectory, "merge-train-acceptance.db");
            var store = new MergeTrainAcceptanceStore(path);
            var older = receipts[1] with
            {
                ReceiptId = "older",
                CompletedAt = receipts[0].CompletedAt.AddDays(-1),
                Identity = MergeTrainIdentity.Create(receipts[1].Identity.Members, new string('a', 40), new string('f', 40), "manifest")
            };
            store.SaveGateReceipt(older);
            foreach (var receipt in receipts) MergeTrainDropBacktestTests.Fixture.Save(store, workspace.RootDirectory, receipt);
            var before = File.ReadAllBytes(path);
            var since = receipts[0].CompletedAt.ToString("O");
            var output = Execute(["experiment-train-backtest", "--since", since, "--training-count", "4"], workspace);
            var lines = output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal($"merge train drop backtest since {since}", lines[0]);
            Assert.Equal(MergeTrainDropBacktest.Run(receipts, 4), lines.Skip(1));
            Assert.Equal("verdict: confirmed", lines[^1]);
            Assert.Equal(before, File.ReadAllBytes(path));
        });
    }

    [Theory]
    [InlineData("missing-since", "since:")]
    [InlineData("invalid-since", "since:")]
    [InlineData("no-offset", "since:")]
    [InlineData("zero", "training-count:")]
    [InlineData("negative", "training-count:")]
    [InlineData("text", "training-count:")]
    public void Execute_InvalidOptions_ThrowsFieldSpecificError(string fault, string prefix)
    {
        WithWorkspace(workspace =>
        {
            string[] args = fault switch
            {
                "missing-since" => ["experiment-train-backtest"],
                "invalid-since" => ["experiment-train-backtest", "--since", "garbage"],
                "no-offset" => ["experiment-train-backtest", "--since", "2026-10-01T00:00:00"],
                _ => ["experiment-train-backtest", "--since", "2026-10-01T00:00:00Z", "--training-count",
                    fault == "zero" ? "zero" : fault == "negative" ? "-1" : "nonsense"]
            };
            Assert.StartsWith(prefix, Assert.Throws<ArgumentException>(() => Execute(args, workspace)).Message);
            if (fault == "zero")
                Assert.StartsWith(prefix, Assert.Throws<ArgumentException>(() => Execute(
                    ["experiment-train-backtest", "--since", "2026-10-01T00:00:00Z", "--training-count", "0"], workspace)).Message);
            Assert.False(File.Exists(Path.Combine(workspace.OrchestratorDirectory, "merge-train-acceptance.db")));
        });
    }

    [Fact]
    public void Execute_CatalogAndHelp_RegisterVerbAndRejectUnknownFlags()
    {
        const string verb = "experiment-train-backtest";
        Assert.Contains(verb, CliArgumentParser.RecognizedCommands);
        Assert.True(CliHandledVerbRegistry.IsHandled(verb));
        Assert.True(CliExperimentCommands.IsCommand(verb));
        Assert.True(CliPersistentStateRunner.SkipsKernelState([verb]));
        WithWorkspace(workspace =>
        {
            Assert.Contains(CliCommandHelp.ExperimentTrainBacktestUsage, Execute([verb, "--help"], workspace));
            Assert.Contains(CliCommandHelp.ExperimentTrainBacktestUsage, Execute([verb, "-h"], workspace));
            Assert.Throws<ArgumentException>(() => Execute([verb, "--unknown"], workspace));
            var error = Assert.Throws<InvalidOperationException>(() => Execute([verb, "--since", "2026-10-01T00:00:00Z"], workspace));
            Assert.Contains("merge-train-acceptance.db", error.Message);
            Assert.False(File.Exists(Path.Combine(workspace.OrchestratorDirectory, "merge-train-acceptance.db")));
        });
    }

    private static string Execute(string[] args, OrchestratorWorkspace workspace)
    {
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var profiles = WorkerProfileCatalog.Default();
        Goal? current = null;
        var repository = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath);
        return CaptureConsole(() => Assert.False(CliPersistentStateRunner.ExecuteCommand(args, repository, workspace,
            ref agents, new InMemoryModelProviderRegistry([]), ref profiles, ref current)));
    }

    private static void WithWorkspace(Action<OrchestratorWorkspace> action)
    {
        var root = Path.Combine(Path.GetTempPath(), "cli-train-backtest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        try
        {
            StateDbMigrations.EnsureUpToDate(workspace.SqliteStatePath);
            action(workspace);
        }
        finally { Directory.Delete(root, true); }
    }
}
