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

    [Theory]
    [InlineData("rounds-per-landing", "expected-overhead-rounds", "rounds-per-landing")]
    [InlineData("wasted-rounds", "rounds-per-landing", "rounds-per-landing")]
    [InlineData("wasted-rounds", "productive-rounds", "productive-rounds")]
    public void Add_WarnsForSharedMetricsIncludingGuardrailsAndStillStores(string metric, string guardrail, string shared)
    {
        WithWorkspace(workspace =>
        {
            var first = Add(workspace, Spec());
            var second = Add(workspace, WithMetrics(Spec(), metric, guardrail), out var output);
            Assert.Equal($"overlap: {first.Id} shared: {shared} hypothesis: {first.Spec.Hypothesis}{Environment.NewLine}" +
                $"experiment: {second.Id}{Environment.NewLine}", output);
            var store = new ExperimentStore(workspace.ExperimentStorePath);
            Assert.Equal(2, store.CountAsync().GetAwaiter().GetResult());
            Assert.Equal(ExperimentOutcomeState.Open, store.ResolveAsync(second.Id).GetAwaiter().GetResult()!.Outcome);
        });
    }

    [Fact]
    public void Show_ListsBothDirectionsAndDecidedStateWithoutChangingReadingOrStores()
    {
        WithWorkspace(workspace =>
        {
            CliCommandTestsExperimentReading.Seed(workspace);
            var first = Add(workspace, Spec());
            var args = new[] { "experiment-show", first.Id, "--as-of", "2026-10-03T00:00:00Z" };
            var alone = Execute(args, workspace);
            Assert.EndsWith($"overlaps: none{Environment.NewLine}", alone);
            var second = Add(workspace, Spec());
            var store = new ExperimentStore(workspace.ExperimentStorePath);
            store.DecideAsync(second.Id, ExperimentOutcomeState.Confirmed, "receipt:overlap", "Keep").GetAwaiter().GetResult();
            var decided = store.ResolveAsync(second.Id).GetAwaiter().GetResult()!;
            var experimentsBefore = File.ReadAllBytes(workspace.ExperimentStorePath);
            var stateBefore = File.ReadAllBytes(workspace.SqliteStatePath);
            var shown = Execute(args, workspace);
            var sectionAt = shown.IndexOf($"overlaps:{Environment.NewLine}", StringComparison.Ordinal);
            Assert.True(sectionAt >= 0, shown);
            Assert.Equal(alone[..alone.IndexOf("overlaps: none", StringComparison.Ordinal)], shown[..sectionAt]);
            const string shared = "landings-per-hour, productive-rounds, rounds-per-landing";
            Assert.Equal($"overlaps:{Environment.NewLine}  {second.Id} decided at {decided.Decision!.DecidedAt:O} shared: {shared}{Environment.NewLine}",
                shown[sectionAt..]);
            var reverse = Execute(["experiment-show", second.Id, "--as-of", "2026-10-03T00:00:00Z"], workspace);
            Assert.EndsWith($"overlaps:{Environment.NewLine}  {first.Id} open shared: {shared}{Environment.NewLine}", reverse);
            Assert.Equal(experimentsBefore, File.ReadAllBytes(workspace.ExperimentStorePath));
            Assert.Equal(stateBefore, File.ReadAllBytes(workspace.SqliteStatePath));
            Assert.Equal(ExperimentOutcomeState.Open, store.ResolveAsync(first.Id).GetAwaiter().GetResult()!.Outcome);
            Assert.Equal(decided.Decision, store.ResolveAsync(second.Id).GetAwaiter().GetResult()!.Decision);
        });
    }

    [Fact]
    public void AddAndShow_DoNotWarnForOtherwiseIdenticalSpecWithDisjointMetricsAndGuardrail()
    {
        WithWorkspace(workspace =>
        {
            var first = Add(workspace, Spec());
            var second = Add(workspace, WithMetrics(Spec(), "wasted-rounds", "expected-overhead-rounds"), out var output);
            Assert.Equal($"experiment: {second.Id}{Environment.NewLine}", output);
            foreach (var id in new[] { first.Id, second.Id })
                Assert.EndsWith($"overlaps: none{Environment.NewLine}", Execute(["experiment-show", id], workspace));
        });
    }

    [Theory]
    [InlineData(-1, true)]
    [InlineData(0, false)]
    [InlineData(1, false)]
    public void AddAndShow_UseDecidedWindowWithStrictBoundary(int startOffsetSeconds, bool overlaps)
    {
        WithWorkspace(workspace =>
        {
            var first = Add(workspace, Spec());
            var store = new ExperimentStore(workspace.ExperimentStorePath);
            store.DecideAsync(first.Id, ExperimentOutcomeState.Refuted, "receipt:boundary", "Revert").GetAwaiter().GetResult();
            var decided = store.ResolveAsync(first.Id).GetAwaiter().GetResult()!;
            var start = decided.Decision!.DecidedAt.AddSeconds(startOffsetSeconds);
            var secondSpec = Spec() with { Baseline = new(ExperimentBaselineKind.BeforeAfterWindow, start.AddDays(-1), start) };
            var second = Add(workspace, secondSpec, out var output);
            Assert.Equal(overlaps, output.Contains($"overlap: {first.Id}", StringComparison.Ordinal));
            Assert.Equal(2, store.CountAsync().GetAwaiter().GetResult());
            foreach (var (id, otherId) in new[] { (first.Id, second.Id), (second.Id, first.Id) })
            {
                var shown = Execute(["experiment-show", id], workspace);
                if (overlaps) Assert.Contains($"  {otherId} ", shown);
                else Assert.EndsWith($"overlaps: none{Environment.NewLine}", shown);
            }
        });
    }

    [Theory]
    [InlineData(0, null, 1, null, true)]
    [InlineData(0, 2, 1, null, true)]
    [InlineData(1, null, 0, 2, true)]
    [InlineData(0, 3, 1, 2, true)]
    [InlineData(0, 1, 1, null, false)]
    [InlineData(1, null, 0, 1, false)]
    [InlineData(0, 1, 2, 3, false)]
    [InlineData(0, 0, -1, null, false)]
    [InlineData(0, -1, -2, null, false)]
    [InlineData(-1, null, 0, 0, false)]
    [InlineData(-2, null, 0, -1, false)]
    public void Overlap_IntersectsOnlyNonemptyActiveWindows(int start, int? end, int otherStart, int? otherEnd, bool expected)
    {
        var epoch = DateTimeOffset.Parse("2026-10-01T00:00:00Z");
        ExperimentRecord Record(string id, int from, int? until) => new(id,
            Spec() with { Baseline = new(ExperimentBaselineKind.TwinGoal, TwinGoalId: "twin") }, epoch.AddHours(from),
            until is null ? ExperimentOutcomeState.Open : ExperimentOutcomeState.Confirmed,
            until is null ? null : new(ExperimentOutcomeState.Confirmed, "receipt", "Keep", epoch.AddHours(until.Value)));
        var subject = Record("subject", start, end);
        var other = Record("other", otherStart, otherEnd);
        Assert.Equal(expected, ExperimentOverlap.Find(subject, [other]).Count == 1);
        Assert.Equal(expected, ExperimentOverlap.Find(other, [subject]).Count == 1);
    }

    [Fact]
    public void Overlap_PrefersBaselineEndExcludesSelfAndOrdersResultsAndMetrics()
    {
        var epoch = DateTimeOffset.Parse("2026-10-01T00:00:00Z");
        var spec = Spec() with { Baseline = new(ExperimentBaselineKind.BeforeAfterWindow, epoch.AddDays(-1), epoch) };
        var subject = new ExperimentRecord("subject", spec, epoch.AddDays(10), ExperimentOutcomeState.Open, null);
        var decision = new ExperimentDecision(ExperimentOutcomeState.Confirmed, "receipt", "Keep", epoch.AddHours(1));
        var a = new ExperimentRecord("a", spec, epoch.AddDays(11), decision.Outcome, decision);
        var b = a with { Id = "b" };
        var earlier = a with { Id = "z", CreatedAt = epoch.AddDays(9) };
        var results = ExperimentOverlap.Find(subject, [b, subject, a, earlier]);
        Assert.Equal(new[] { "z", "a", "b" }, results.Select(result => result.Other.Id));
        foreach (var result in results)
            Assert.Equal(new[] { "landings-per-hour", "productive-rounds", "rounds-per-landing" }, result.SharedMetrics);
        var caseMismatch = b with { Spec = spec with { Metrics = spec.Metrics.Select(m => m.ToUpperInvariant()).ToArray(),
            Guardrail = spec.Guardrail with { Metric = spec.Guardrail.Metric.ToUpperInvariant() } } };
        Assert.Empty(ExperimentOverlap.Find(subject, [caseMismatch]));
    }

    private static ExperimentSpec WithMetrics(ExperimentSpec spec, string metric, string guardrail) => spec with
    {
        Metrics = [metric], Guardrail = new(guardrail, new(guardrail, "<", -10)),
        DecisionRule = new([new(metric, "<", 0)], [new(metric, ">", 0)])
    };

    internal static ExperimentSpec Spec() => new("A shorter brief reduces rounds per landing",
        new(ExperimentInterventionKind.BriefOrPromptChange, "Remove repeated preamble"),
        new(ExperimentBaselineKind.BeforeAfterWindow, DateTimeOffset.Parse("2026-10-01T00:00:00Z"),
            DateTimeOffset.Parse("2026-10-02T00:00:00Z")),
        ["rounds-per-landing", "landings-per-hour"],
        new("productive-rounds", new("productive-rounds", "<", -10)),
        new(2, ExperimentStopUnit.Goals),
        new([new("rounds-per-landing", "<", 0)], [new("rounds-per-landing", ">", 0)]));

    internal static ExperimentRecord Add(OrchestratorWorkspace workspace, ExperimentSpec spec) => Add(workspace, spec, out _);

    private static ExperimentRecord Add(OrchestratorWorkspace workspace, ExperimentSpec spec, out string output)
    {
        var path = Path.Combine(workspace.RootDirectory, "spec.json");
        File.WriteAllText(path, JsonSerializer.Serialize(spec, ExperimentStore.JsonOptions));
        output = Execute(["experiment-add", "--spec", path], workspace);
        var idLine = output.TrimEnd().Split(Environment.NewLine)[^1];
        Assert.StartsWith("experiment: ", idLine);
        var id = idLine["experiment: ".Length..];
        Assert.Equal(32, id.Length);
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
