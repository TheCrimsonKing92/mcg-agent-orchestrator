using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class GoalRefinementReScopeVersioningTests
{
    internal const string Original =
        "Benchmark wall-clock performance on this host while the machine is idle.";
    internal const string Replacement = "Assert the pure duration mapper output.";

    [Fact]
    public async Task Resolve_ReScope_PreservesPriorVersionAndRecordsLaterReplacement()
    {
        using var scenario = new Scenario(Original);
        await scenario.Service.RefineAsync(scenario.Kernel, scenario.Goal.Id);
        var first = Assert.Single(scenario.Goal.RefinedSpecVersions);
        var question = Assert.Single(scenario.Goal.RefinedSpec!.OpenQuestions);
        var originalTime = first.RecordedAt;
        var rounds = scenario.Goal.ClarificationRoundCount;
        scenario.Clock.UtcNow = originalTime.AddHours(1);

        Assert.True(await scenario.Service.TryResolveOpenClarificationAsync(
            scenario.Kernel, question.Id, $"re-scope: {Replacement}"));

        Assert.Equal(2, scenario.Goal.RefinedSpecVersions.Count);
        var prior = scenario.Goal.RefinedSpecVersions[0];
        var current = scenario.Goal.RefinedSpecVersions[1];
        Assert.Equal(1, prior.Version);
        Assert.Equal(2, prior.SupersededByVersion);
        Assert.Equal([Original], prior.Spec.AcceptanceCriteria);
        Assert.Equal(originalTime, prior.RecordedAt);
        Assert.Equal(2, current.Version);
        Assert.Null(current.SupersededByVersion);
        Assert.Equal([Replacement], current.Spec.AcceptanceCriteria);
        Assert.Equal(scenario.Clock.UtcNow, current.RecordedAt);
        Assert.Equal(rounds, scenario.Goal.ClarificationRoundCount);
        Assert.Empty(current.Spec.OpenQuestions);
        Assert.Empty(current.Spec.OperatorOwnedAcceptanceCriteria);
        Assert.Empty(current.Spec.AcceptanceGateOwnedAcceptanceCriteria);
        Assert.Contains(current.Spec.Decisions, decision => decision.Rationale ==
            $"Feasibility re-scope recorded as refined-spec version 2 and re-checked (topic: {question.TopicKey}).");
    }

    [Fact]
    public async Task Resolve_GateOwnedReScope_CreatesOnlyCurrentVersionTwoObligations()
    {
        var original = $"{Original} ACCEPTANCE-GATE-OWNED";
        const string unchanged = "Assert deterministic mapper coverage. ACCEPTANCE-GATE-OWNED";
        using var scenario = new Scenario(original, unchanged);
        await scenario.Service.RefineAsync(scenario.Kernel, scenario.Goal.Id);
        var question = Assert.Single(scenario.Goal.RefinedSpec!.OpenQuestions);
        Assert.Equal(original, question.Criterion);
        Assert.Equal(2, scenario.Goal.CriterionEvidenceObligations.Count);
        scenario.Clock.UtcNow = scenario.Clock.UtcNow.AddHours(1);

        Assert.True(await scenario.Service.TryResolveOpenClarificationAsync(
            scenario.Kernel, question.Id, $"re-scope: {Replacement}"));

        var current = scenario.Goal.OutstandingCriterionEvidenceObligations;
        Assert.Equal(2, current.Count);
        Assert.All(current, obligation =>
        {
            Assert.Equal(2, obligation.CriterionVersion);
            Assert.Equal(CriterionEvidenceOwner.Acceptance, obligation.Owner);
            Assert.Equal(CriterionEvidenceState.Pending, obligation.State);
        });
        Assert.Equal(Replacement, Assert.Single(current, item => item.Id == "criterion-v2-0").Criterion);
        Assert.Equal(unchanged, Assert.Single(current, item => item.Id == "criterion-v2-1").Criterion);
        Assert.Equal([Replacement, unchanged], scenario.Goal.RefinedSpec!.AcceptanceGateOwnedAcceptanceCriteria);
        Assert.Equal(2, scenario.Goal.CriterionEvidenceObligations.Count(item => item.CriterionVersion == 1));
        Assert.DoesNotContain(current, item => item.CriterionVersion == 1);
    }

    [Fact]
    public async Task Resolve_RepeatedReScope_RecordsSuccessiveVersionNumbers()
    {
        const string intermediate = "Drive two concurrent goals through dispatch and compare their results.";
        using var scenario = new Scenario(Original);
        await scenario.Service.RefineAsync(scenario.Kernel, scenario.Goal.Id);
        var firstQuestion = Assert.Single(scenario.Goal.RefinedSpec!.OpenQuestions);
        scenario.Clock.UtcNow = scenario.Clock.UtcNow.AddHours(1);
        Assert.True(await scenario.Service.TryResolveOpenClarificationAsync(
            scenario.Kernel, firstQuestion.Id, $"re-scope: {intermediate}"));
        Assert.Equal(2, scenario.Goal.AuthoritativeRefinedSpecVersion!.Version);
        var secondQuestion = Assert.Single(scenario.Goal.RefinedSpec!.OpenQuestions);
        scenario.Clock.UtcNow = scenario.Clock.UtcNow.AddHours(1);

        Assert.True(await scenario.Service.TryResolveOpenClarificationAsync(
            scenario.Kernel, secondQuestion.Id, $"re-scope: {Replacement}"));

        Assert.Equal(3, scenario.Goal.RefinedSpecVersions.Count);
        Assert.Equal(3, scenario.Goal.RefinedSpecVersions[1].SupersededByVersion);
        Assert.Equal([intermediate], scenario.Goal.RefinedSpecVersions[1].Spec.AcceptanceCriteria);
        var current = scenario.Goal.AuthoritativeRefinedSpecVersion!;
        Assert.Equal([Replacement], current.Spec.AcceptanceCriteria);
        Assert.Contains(current.Spec.Decisions, decision => decision.Rationale ==
            $"Feasibility re-scope recorded as refined-spec version 3 and re-checked (topic: {secondQuestion.TopicKey}).");
    }

    [Theory]
    [InlineData("operator-owned")]
    [InlineData("supply-reproducing-scenario: checked-in isolated benchmark harness")]
    public async Task Resolve_OtherDispositions_KeepInPlaceRecording(string answer)
    {
        using var scenario = new Scenario(Original);
        await scenario.Service.RefineAsync(scenario.Kernel, scenario.Goal.Id);
        var originalTime = Assert.Single(scenario.Goal.RefinedSpecVersions).RecordedAt;
        var question = Assert.Single(scenario.Goal.RefinedSpec!.OpenQuestions);
        scenario.Clock.UtcNow = originalTime.AddHours(1);

        Assert.True(await scenario.Service.TryResolveOpenClarificationAsync(
            scenario.Kernel, question.Id, answer));

        var version = Assert.Single(scenario.Goal.RefinedSpecVersions);
        Assert.Equal(originalTime, version.RecordedAt);
        Assert.Equal([Original], version.Spec.AcceptanceCriteria);
        Assert.Contains(version.Spec.Decisions, decision => decision.Rationale ==
            $"Feasibility disposition applied and re-checked (topic: {question.TopicKey}).");
    }

    // Parallel-safe: each scenario owns its directories, collaboration store, and clock.
    internal sealed class Scenario : IDisposable
    {
        internal Scenario(params string[] criteria)
        {
            Workspace = OrchestratorWorkspace.ForDirectory(CreateTempDirectory());
            Providers = new InMemoryModelProviderRegistry([new FakeSmokeProvider(
                JsonSerializer.Serialize(new
                {
                    behavioralContract = "Exercise deterministic mapper behavior.",
                    acceptanceCriteria = criteria.Select((text, index) => new { text, declared_index = index + 1 }),
                    verificationClass = "TestVerifiable",
                    decisions = Array.Empty<object>(),
                    forks = Array.Empty<object>()
                }), providerName: "fake-refiner")]);
            var catalog = new ModelFunctionCatalog([
                new ModelFunctionBinding(ModelFunctionPurposes.SpecRefiner, ModelLane.CheapApi,
                    new ModelProfile("fake-refiner", "fake-model", ModelCapability.Text, SubscriptionMode.ApiKey))
            ]);
            ModelFunctionCatalogStore.Save(Workspace.ModelFunctionCatalogPath, catalog);
            Store = CollaborationItemStore.ForDirectory(Workspace.OrchestratorDirectory);
            Service = new GoalRefinementService(Providers, catalog, Store,
                new SpecRefinerPrecedentStore(Workspace.SpecRefinerPrecedentsPath),
                WorkerProfileCatalog.Default(), rawOutputDirectory: Workspace.LogDirectory);
            Clock = new MutableClock(new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero));
            Kernel = new AgentOrchestratorKernel(Clock);
            Goal = Kernel.CreateGoal("Implement deterministic mapper behavior.\n\n## Acceptance criteria\n\n" +
                string.Join("\n", criteria.Select((text, index) => $"{index + 1}. {text}")));
        }

        internal OrchestratorWorkspace Workspace { get; }
        internal InMemoryModelProviderRegistry Providers { get; }
        internal CollaborationItemStore Store { get; }
        internal GoalRefinementService Service { get; }
        internal MutableClock Clock { get; }
        internal AgentOrchestratorKernel Kernel { get; }
        internal Goal Goal { get; }

        public void Dispose() => Directory.Delete(Workspace.RootDirectory, recursive: true);
    }

    internal sealed class MutableClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;
    }
}
