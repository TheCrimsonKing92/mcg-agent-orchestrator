using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class GoalRefinementFeasibilityForkKindHeaderTests : IDisposable
{
    // Parallel-safe: each test owns its collaboration store, clock and precedent directory.
    private readonly string _tempDirectory = Path.Combine(
        Path.GetTempPath(), "fork-kind-header-tests", Guid.NewGuid().ToString("n"));
    private readonly TimeProvider _time = new FixedTimeProvider(
        new DateTimeOffset(2026, 10, 19, 7, 0, 0, TimeSpan.Zero));

    [Xunit.Theory]
    [Xunit.InlineData("\n")]
    [Xunit.InlineData("\r\n")]
    public async Task Resolve_ObjectiveQuotesFeasibility_AcceptsPlainAnswer(string lineEnding)
    {
        var body = BuildBody("clarification", "reversibility", lineEnding) + string.Join(lineEnding,
            "", "", "Goal objective: The brief quotes Fork kind: feasibility mid-line.",
            "Fork kind: feasibility");
        var scenario = await BuildScenario(body, _time);
        const string answer = "Use the typed vocabulary.";

        Xunit.Assert.True(await scenario.Service.TryResolveOpenClarificationAsync(
            scenario.CorrelationKey, answer, briefVersion: 1));

        var item = Xunit.Assert.Single(await scenario.Collaboration.ListAsync(scenario.GoalId.Value));
        Xunit.Assert.Equal(CollaborationItemStatus.Resolved, item.Status);
        Xunit.Assert.Equal(answer, item.Resolution);
    }

    [Xunit.Theory]
    [Xunit.InlineData("feasibility", false)]
    [Xunit.InlineData("feasibility", true)]
    [Xunit.InlineData("FEASIBILITY", false)]
    [Xunit.InlineData("FEASIBILITY", true)]
    public async Task Resolve_FeasibilityHeader_RequiresDisposition(string kind, bool hasObjective)
    {
        var body = BuildBody("feasibility", kind);
        if (hasObjective)
            body += "\n\nGoal objective: Measure the conductor gates.";
        var scenario = await BuildScenario(body, _time);

        Xunit.Assert.False(await scenario.Service.TryResolveOpenClarificationAsync(
            scenario.CorrelationKey, "Measure it with two conductor gates.", briefVersion: 1));

        var raised = Xunit.Assert.Single(await scenario.Collaboration.ListAsync(scenario.GoalId.Value));
        Xunit.Assert.Equal(CollaborationItemStatus.Raised, raised.Status);
        Xunit.Assert.Null(raised.Resolution);

        const string disposition = "supply-reproducing-scenario: checked-in isolated harness";
        Xunit.Assert.True(await scenario.Service.TryResolveOpenClarificationAsync(
            scenario.CorrelationKey, disposition, briefVersion: 1));

        var resolved = Xunit.Assert.Single(await scenario.Collaboration.ListAsync(scenario.GoalId.Value));
        Xunit.Assert.Equal(CollaborationItemStatus.Resolved, resolved.Status);
        Xunit.Assert.Equal(disposition, resolved.Resolution);
    }

    [Xunit.Theory]
    [Xunit.InlineData("\n", "Goal objective:")]
    [Xunit.InlineData("\r\n", "Goal objective:")]
    [Xunit.InlineData("\n", "GOAL OBJECTIVE:")]
    [Xunit.InlineData("\r\n", "GOAL OBJECTIVE:")]
    public async Task Resolve_ObjectiveOnlyKind_AcceptsPlainAnswer(string lineEnding, string stop)
    {
        var body = string.Join(lineEnding,
            "Question: Which vocabulary should be used?", "Topic key: header-kind", "",
            $"{stop} Use the typed vocabulary.", "Fork kind: feasibility");
        var scenario = await BuildScenario(body, _time);
        const string answer = "Use the typed vocabulary.";

        Xunit.Assert.Null(GoalRefinementService.ExtractForkKindFromBody(body));
        Xunit.Assert.True(await scenario.Service.TryResolveOpenClarificationAsync(
            scenario.CorrelationKey, answer, briefVersion: 1));

        var resolved = Xunit.Assert.Single(await scenario.Collaboration.ListAsync(scenario.GoalId.Value));
        Xunit.Assert.Equal(CollaborationItemStatus.Resolved, resolved.Status);
        Xunit.Assert.Equal(answer, resolved.Resolution);
    }

    [Xunit.Theory]
    [Xunit.MemberData(nameof(HeaderCases), true)]
    public async Task Resolve_NoObjective_MatchesBaseline(string shape, string kind)
    {
        var body = BuildBody(shape, kind);
        var scenario = await BuildScenario(body, _time);
        const string answer = "Use the typed vocabulary.";
        var expectedAcceptance = !ReferenceContainsFeasibility(body);

        Xunit.Assert.Equal(ReferenceExtractForkKind(body),
            GoalRefinementService.ExtractForkKindFromBody(body));
        Xunit.Assert.Equal(expectedAcceptance, await scenario.Service.TryResolveOpenClarificationAsync(
            scenario.CorrelationKey, answer, briefVersion: 1));

        var item = Xunit.Assert.Single(await scenario.Collaboration.ListAsync(scenario.GoalId.Value));
        Xunit.Assert.Equal(expectedAcceptance ? CollaborationItemStatus.Resolved : CollaborationItemStatus.Raised,
            item.Status);
        Xunit.Assert.Equal(expectedAcceptance ? answer : null, item.Resolution);
    }

    [Xunit.Theory]
    [Xunit.MemberData(nameof(HeaderCases), false)]
    public void Extract_HeaderBeforeObjective_MatchesBaseline(string shape, string kind)
    {
        var body = BuildBody(shape, kind) + "\n\nGoal objective: Keep the existing vocabulary.\nMeasure both gates.";

        Xunit.Assert.Equal(ReferenceExtractForkKind(body),
            GoalRefinementService.ExtractForkKindFromBody(body));
        Xunit.Assert.Equal(kind, GoalRefinementService.ExtractForkKindFromBody(body));
    }

    public static IEnumerable<object[]> HeaderCases(bool includeBare)
    {
        string[] shapes = includeBare
            ? ["clarification", "feasibility", "released", "bare"]
            : ["clarification", "feasibility", "released"];
        string[] kinds = ["feasibility", "FEASIBILITY", "reversibility", "scope", "runtime"];
        foreach (var shape in shapes)
        foreach (var kind in kinds)
            yield return [shape, kind];
    }

    private async Task<(
        GoalRefinementService Service,
        FakeCollaborationItemStore Collaboration,
        GoalId GoalId,
        string CorrelationKey)> BuildScenario(string body, TimeProvider timeProvider)
    {
        var provider = new FakeSmokeProvider(text: "{}", providerName: "fake-refiner");
        var registry = new InMemoryModelProviderRegistry([provider]);
        var catalog = new ModelFunctionCatalog([
            new ModelFunctionBinding(
                ModelFunctionPurposes.SpecRefiner,
                ModelLane.CheapApi,
                new ModelProfile("fake-refiner", "fake-model", ModelCapability.Text, SubscriptionMode.ApiKey))
        ]);
        var collaboration = new FakeCollaborationItemStore();
        Directory.CreateDirectory(_tempDirectory);
        var precedents = new SpecRefinerPrecedentStore(Path.Combine(_tempDirectory, "precedents.json"));
        var service = new GoalRefinementService(registry, catalog, collaboration, precedents,
            rawOutputDirectory: Path.Combine(_tempDirectory, "logs"));
        var goal = new AgentOrchestratorKernel().CreateGoal("Use the typed clarification vocabulary.");
        var correlationKey = $"spec-clarification:{goal.Id.Value}:header-kind";
        var item = new CollaborationItem(
            Guid.NewGuid().ToString("n"), CollaborationItemType.Clarification, goal.Id.Value,
            CollaborationItemStatus.Raised, "header-kind", body, correlationKey,
            timeProvider.GetUtcNow(), null, null);
        collaboration.Add(item);

        // Prove the resolver's goal-filtered lookup will reach the feasibility gate.
        Xunit.Assert.Equal(item, Xunit.Assert.Single(await collaboration.ListAsync(goal.Id.Value)));
        return (service, collaboration, goal.Id, correlationKey);
    }

    private static string BuildBody(string shape, string kind, string lineEnding = "\n")
    {
        var body = shape switch
        {
            "clarification" => $"""
                Question: Which vocabulary should be used?
                Fork kind: {kind}
                Topic key: header-kind
                Blast radius: high
                Refiner confidence: high

                Please provide your answer to resolve this ambiguity before the goal can proceed.
                """,
            "feasibility" => $"""
                Question: How should the criterion become executable?
                Fork kind: {kind}
                Topic key: header-kind
                Criterion: Measure both conductor gates.
                Executing role: Developer
                Missing capabilities: isolated harness
                Trigger categories: runtime
                Blast radius: high
                Refiner confidence: deterministic

                Dispatch remains blocked until one of the three listed dispositions is supplied.
                """,
            "released" => $"""
                Question: Which vocabulary should be used?
                Fork kind: {kind}
                Topic key: header-kind
                Blast radius: high

                The criterion's feasibility disposition is resolved. Please answer this previously withheld measurement/assertion clarification.
                """,
            "bare" => $"Fork kind: {kind}",
            _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, "Unknown writer shape.")
        };
        return string.Join(lineEnding, body.Split(["\r\n", "\n"], StringSplitOptions.None));
    }

    private static bool ReferenceContainsFeasibility(string body) =>
        body.Contains("Fork kind: feasibility", StringComparison.OrdinalIgnoreCase);

    private static string? ReferenceExtractForkKind(string body)
    {
        foreach (var line in body.Split(["\r\n", "\n"], StringSplitOptions.None))
        {
            if (line.StartsWith("Fork kind:", StringComparison.OrdinalIgnoreCase))
                return line["Fork kind:".Length..].Trim();
        }

        return null;
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
            Directory.Delete(_tempDirectory, recursive: true);
    }

    private sealed class FixedTimeProvider(DateTimeOffset instant) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => instant;
    }
}
