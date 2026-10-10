using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: cases own their stores and use AsyncLocal console capture.
public sealed class CliExperimentExtendTests
{
    [Fact]
    public void Extend_ShortReferenceRecordsHistoryAndShowPreservesOriginalTarget()
    {
        WithWorkspace(workspace =>
        {
            var spec = Spec() with { StopRule = new(3, ExperimentStopUnit.Goals) };
            var record = Add(workspace, spec);
            var reason = "Wait for eight goals";
            var output = Execute(
                ["experiment-extend", record.Id[..8], "--count", "8", "--reason", reason], workspace);
            Assert.Equal($"stop rule extended: 3 to 8 (experiment {record.Id}){Environment.NewLine}", output);
            var shown = Execute(["experiment-show", record.Id[..8]], workspace);
            Assert.Contains("stop rule target: 3 goals", shown.Split(Environment.NewLine));
            var extensionLine = Assert.Single(shown.Split(Environment.NewLine).Where(line => line.StartsWith("stop rule extension:", StringComparison.Ordinal)));
            var extension = Assert.Single(new ExperimentStore(workspace.ExperimentStorePath)
                .ResolveAsync(record.Id).GetAwaiter().GetResult()!.Spec.StopRule.Extensions!);
            Assert.Equal($"stop rule extension: 3 to 8 at {extension.ExtendedAt:O}: {reason}", extensionLine);
            var untouched = Add(workspace, spec);
            var original = Execute(["experiment-show", untouched.Id[..8]], workspace);
            Assert.Contains("stop rule target: 3 goals", original.Split(Environment.NewLine));
            Assert.DoesNotContain("stop rule extension:", original);
        });
    }

    [Theory]
    [InlineData("missing-count", "count:")]
    [InlineData("missing-reason", "reason:")]
    [InlineData("non-numeric", "count:")]
    public void Extend_RequiresBothOptionsAndNumericCount(string fault, string option)
    {
        WithWorkspace(workspace =>
        {
            var record = Add(workspace, Spec());
            string[] args = fault switch
            {
                "missing-count" => ["experiment-extend", record.Id[..8], "--reason", "More goals"],
                "missing-reason" => ["experiment-extend", record.Id[..8], "--count", "8"],
                _ => ["experiment-extend", record.Id[..8], "--count", "eight", "--reason", "More goals"]
            };
            var before = File.ReadAllBytes(workspace.ExperimentStorePath);
            var error = Assert.Throws<ArgumentException>(() => Execute(args, workspace));
            Assert.StartsWith(option, error.Message);
            Assert.Equal(before, File.ReadAllBytes(workspace.ExperimentStorePath));
        });
    }

    [Fact]
    public void Extend_CatalogHandlerAndHelpRecognizeVerb()
    {
        Assert.Contains("experiment-extend", CliArgumentParser.RecognizedCommands);
        Assert.True(CliHandledVerbRegistry.IsHandled("experiment-extend"));
        Assert.True(CliExperimentCommands.IsCommand("experiment-extend"));
        Assert.True(CliPersistentStateRunner.SkipsKernelState(["experiment-extend"]));
        WithWorkspace(workspace =>
        {
            var help = Execute(["experiment-extend", "--help"], workspace);
            Assert.Contains(CliCommandHelp.ExperimentExtendUsage, help);
            Assert.Throws<ArgumentException>(() => Execute(["experiment-extend", "--unknown"], workspace));
        });
    }

    [Fact]
    public void Add_RejectsCallerSuppliedExtensionHistory()
    {
        WithWorkspace(workspace =>
        {
            var spec = Spec() with
            {
                StopRule = new(3, ExperimentStopUnit.Goals,
                    [new(3, 8, "Injected", new DateTimeOffset(2026, 10, 3, 0, 0, 0, TimeSpan.Zero))])
            };
            var store = new ExperimentStore(workspace.ExperimentStorePath);
            var path = Path.Combine(workspace.RootDirectory, "spec.json");
            File.WriteAllText(path, JsonSerializer.Serialize(spec, ExperimentStore.JsonOptions));
            var error = Assert.Throws<ArgumentException>(() => Execute(["experiment-add", "--spec", path], workspace));
            Assert.StartsWith("stopRule.extensions:", error.Message);
            Assert.Equal(0, store.CountAsync().GetAwaiter().GetResult());
        });
    }

    private static ExperimentSpec Spec() => new("A shorter brief reduces rounds per landing",
        new(ExperimentInterventionKind.BriefOrPromptChange, "Remove repeated preamble"),
        new(ExperimentBaselineKind.BeforeAfterWindow, new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 10, 2, 0, 0, 0, TimeSpan.Zero)),
        ["rounds-per-landing", "landings-per-hour"],
        new("productive-rounds", new("productive-rounds", "<", -10)),
        new(2, ExperimentStopUnit.Goals),
        new([new("rounds-per-landing", "<", 0)], [new("rounds-per-landing", ">", 0)]));

    private static ExperimentRecord Add(OrchestratorWorkspace workspace, ExperimentSpec spec) =>
        new ExperimentStore(workspace.ExperimentStorePath).AddAsync(spec).GetAwaiter().GetResult();

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
        var root = Path.Combine(Path.GetTempPath(), "cli-experiment-extend-" + Guid.NewGuid().ToString("n"));
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
