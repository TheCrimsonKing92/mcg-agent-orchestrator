using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: cases own their stores and use AsyncLocal console capture.
public sealed class CliCommandTestsExperiments : CliTaskQueryTestSupport
{
    [Theory]
    [InlineData("minutes", "stopRule", "unit", "stopRule.unit")]
    [InlineData("hours", "stopRule", "unit", "stopRule.unit")]
    [InlineData("host-cpu", "metrics", "", "metrics")]
    [InlineData("", "hypothesis", "", "hypothesis")]
    [InlineData("", "decisionRule", "", "decisionRule")]
    [InlineData("absent-epic", "epicId", "", "epicId")]
    public void Add_RejectsInvalidFieldWithoutPersistingThenAcceptsCorrection(string value, string field, string nested, string diagnostic)
    {
        WithWorkspace(workspace =>
        {
            var store = new ExperimentStore(workspace.ExperimentStorePath);
            var spec = JsonSerializer.SerializeToNode(Spec(), ExperimentStore.JsonOptions)!;
            if (field == "metrics") spec[field] = new JsonArray(value);
            else if (field == "decisionRule") spec.AsObject().Remove(field);
            else if (nested.Length > 0) spec[field]![nested] = value;
            else spec[field] = value;
            var path = Path.Combine(workspace.RootDirectory, "spec.json");
            File.WriteAllText(path, spec.ToJsonString());
            var probe = new ProbeStateRepository(new AgentOrchestratorKernel());
            var error = Assert.ThrowsAny<Exception>(() => Execute(["experiment-add", "--spec", path], workspace, probe));
            Assert.True(error is ArgumentException or InvalidOperationException, error.ToString());
            Assert.Contains(diagnostic, error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, store.CountAsync().GetAwaiter().GetResult());

            File.WriteAllText(path, JsonSerializer.Serialize(Spec(), ExperimentStore.JsonOptions));
            var output = Execute(["experiment-add", "--spec", path], workspace, probe);
            var id = output.Trim()["experiment: ".Length..];
            Assert.Equal(32, id.Length);
            Assert.Equal(ExperimentOutcomeState.Open, store.ResolveAsync(id).GetAwaiter().GetResult()!.Outcome);
            Assert.Equal(1, store.CountAsync().GetAwaiter().GetResult());
            AssertNoStateWrites(probe);
            Assert.False(File.Exists(workspace.SqliteStatePath));
            Assert.False(File.Exists(workspace.PortfolioStorePath));
        });
    }

    [Fact]
    public void Add_ResolvesEpicReadOnlyAndPreservesPortfolioBytes()
    {
        WithWorkspace(workspace =>
        {
            var portfolio = new PortfolioStore(workspace.PortfolioStorePath);
            var epic = portfolio.AddEpicAsync("Experimentation").GetAwaiter().GetResult();
            var before = File.ReadAllBytes(workspace.PortfolioStorePath);
            var record = Add(workspace, Spec() with { EpicId = epic.Id[..8] });
            Assert.Equal(epic.Id, record.Spec.EpicId);
            Assert.Equal(before, File.ReadAllBytes(workspace.PortfolioStorePath));
        });
    }

    [Fact]
    public void Decide_IsSingleShotAndShowNeverChangesStoredOutcome()
    {
        WithWorkspace(workspace =>
        {
            CliCommandTestsExperimentReading.Seed(workspace);
            var record = Add(workspace, Spec());
            var probe = new ProbeStateRepository(new AgentOrchestratorKernel());
            var stateBefore = File.ReadAllBytes(workspace.SqliteStatePath);
            var output = Execute(["experiment-show", record.Id[..8], "--as-of", "2026-10-03T00:00:00Z"], workspace, probe);
            Assert.Contains($"reading: keep", output);
            Assert.Contains($"{Environment.NewLine}outcome: open{Environment.NewLine}", output);
            var store = new ExperimentStore(workspace.ExperimentStorePath);
            Assert.Equal(ExperimentOutcomeState.Open, store.ResolveAsync(record.Id).GetAwaiter().GetResult()!.Outcome);
            Execute(["experiment-decide", record.Id, "--outcome", "refuted", "--evidence", "receipt:operator", "--action", "Revert by hand"], workspace, probe);
            var decided = store.ResolveAsync(record.Id).GetAwaiter().GetResult()!;
            Assert.Equal(ExperimentOutcomeState.Refuted, decided.Outcome);
            Assert.Equal("receipt:operator", decided.Decision!.Evidence);
            Assert.Equal("Revert by hand", decided.Decision.Action);
            var experimentBefore = File.ReadAllBytes(workspace.ExperimentStorePath);
            output = Execute(["experiment-show", record.Id, "--as-of", "2026-10-03T00:00:00Z"], workspace, probe);
            Assert.Contains("reading: keep", output);
            Assert.Contains($"{Environment.NewLine}outcome: refuted{Environment.NewLine}", output);
            Assert.Contains("decision evidence: receipt:operator", output);
            Assert.Contains("decision action: Revert by hand", output);
            Assert.Equal(experimentBefore, File.ReadAllBytes(workspace.ExperimentStorePath));
            var error = Assert.Throws<InvalidOperationException>(() => Execute(
                ["experiment-decide", record.Id, "--outcome", "confirmed", "--evidence", "replacement", "--action", "Keep"], workspace, probe));
            Assert.Contains("already decided", error.Message);
            Assert.Equal(experimentBefore, File.ReadAllBytes(workspace.ExperimentStorePath));
            Assert.Equal(stateBefore, File.ReadAllBytes(workspace.SqliteStatePath));
            AssertNoStateWrites(probe);
        });
    }

    [Theory]
    [InlineData("open", "evidence", "action")]
    [InlineData("confirmed", "", "action")]
    [InlineData("confirmed", "evidence", "")]
    public void Decide_RejectsMissingEvidenceActionAndOpenOutcome(string outcome, string evidence, string action)
    {
        WithWorkspace(workspace =>
        {
            var record = Add(workspace, Spec());
            var before = File.ReadAllBytes(workspace.ExperimentStorePath);
            Assert.Throws<ArgumentException>(() => Execute(["experiment-decide", record.Id, "--outcome", outcome,
                "--evidence", evidence, "--action", action], workspace));
            Assert.Equal(before, File.ReadAllBytes(workspace.ExperimentStorePath));
        });
    }

    [Theory]
    [InlineData("experiment-add", "--spec")]
    [InlineData("experiment-show", "--as-of")]
    [InlineData("experiment-decide", "--outcome")]
    public void Verbs_HaveCatalogHelpAndFastRouting(string command, string flag)
    {
        Assert.Contains(command, CliArgumentParser.RecognizedCommands);
        Assert.True(CliPersistentStateRunner.SkipsKernelState([command]));
        Assert.Throws<ArgumentException>(() => CliCommandHelp.ThrowIfInvalidFlags([command, "--unknown-experiment-flag"]));
        WithWorkspace(workspace =>
        {
            var probe = new ProbeStateRepository(new AgentOrchestratorKernel());
            var output = Execute([command, "--help"], workspace, probe);
            Assert.Contains($"Usage: {command}", output);
            Assert.Contains(flag, output);
            AssertNoStateWrites(probe);
            Assert.False(File.Exists(workspace.ExperimentStorePath));
        });
        var runbook = File.ReadAllText(Path.Combine(RepositoryRoot(), "docs", "operator-runbook.md"));
        Assert.Contains("### Experiments", runbook);
        Assert.Contains(command, runbook);
    }

    internal static ExperimentSpec Spec() => new("A shorter brief reduces rounds per landing",
        new(ExperimentInterventionKind.BriefOrPromptChange, "Remove repeated preamble"),
        new(ExperimentBaselineKind.BeforeAfterWindow, DateTimeOffset.Parse("2026-10-01T00:00:00Z"),
            DateTimeOffset.Parse("2026-10-02T00:00:00Z")),
        ["rounds-per-landing", "landings-per-hour"],
        new("productive-rounds", new("productive-rounds", "<", -10)),
        new(2, ExperimentStopUnit.Goals),
        new([new("rounds-per-landing", "<", 0)], [new("rounds-per-landing", ">", 0)]));

    internal static ExperimentRecord Add(OrchestratorWorkspace workspace, ExperimentSpec spec)
    {
        var path = Path.Combine(workspace.RootDirectory, "spec.json");
        File.WriteAllText(path, JsonSerializer.Serialize(spec, ExperimentStore.JsonOptions));
        var output = Execute(["experiment-add", "--spec", path], workspace);
        Assert.StartsWith("experiment: ", output);
        var id = output.Trim()["experiment: ".Length..];
        return new ExperimentStore(workspace.ExperimentStorePath).ResolveAsync(id).GetAwaiter().GetResult()!;
    }

    internal static void WithWorkspace(Action<OrchestratorWorkspace> action)
    {
        var root = CreateTempDirectory();
        try { action(OrchestratorWorkspace.ForDirectory(root)); }
        finally { Directory.Delete(root, true); }
    }

    internal static string Execute(string[] args, OrchestratorWorkspace workspace) =>
        Execute(args, workspace, new ProbeStateRepository(new AgentOrchestratorKernel()));

    private static string Execute(string[] args, OrchestratorWorkspace workspace, ProbeStateRepository probe)
    {
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var profiles = WorkerProfileCatalog.Default();
        Goal? current = null;
        return CaptureConsole(() => Assert.False(CliPersistentStateRunner.ExecuteCommand(args, probe, workspace,
            ref agents, new InMemoryModelProviderRegistry([]), ref profiles, ref current)));
    }

    private static void AssertNoStateWrites(ProbeStateRepository probe)
    {
        Assert.Equal(0, probe.FullLoadAttempts);
        Assert.Equal(0, probe.MutationAttempts);
        Assert.Equal(0, probe.SaveAttempts);
        Assert.Equal(0, probe.MergeSaveAttempts);
    }

    private static string RepositoryRoot([CallerFilePath] string sourcePath = "") => VerifiedRepositoryRoot.Find(sourcePath);
}
