using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;

public sealed class SemanticAcceptanceTests
{
    private static SemanticAcceptanceInputs SampleInputs() => new(
        "Add a GetDiffExcerpt helper and feed it to the judge",
        ["Focused tests pass", "No forbidden paths changed"],
        ["src/A.cs", "tests/ATests.cs"],
        "diff --git a/src/A.cs b/src/A.cs\n+public static string GetDiffExcerpt() => ...;",
        "core tests: Passed!  - Failed: 0, Passed: 5");

    [Xunit.Fact(DisplayName = "SemanticAcceptancePlanner_parses_a_valid_fenced_verdict")]
    public void ParsesValidFencedVerdict()
    {
        var output = """
            Here is my assessment.
            ```json
            {"criteria_met": true, "confidence": "high", "reasons": ["adds GetDiffExcerpt", "wires judge"], "unmet_criteria": []}
            ```
            """;

        var verdict = SemanticAcceptancePlanner.Parse(output);

        Assert.True(verdict.IsValid);
        Assert.True(verdict.CriteriaMet);
        Assert.Equal("high", verdict.Confidence);
        Assert.Equal(2, verdict.Reasons.Count);
        Assert.Equal(0, verdict.UnmetCriteria.Count);
    }

    [Xunit.Fact(DisplayName = "SemanticAcceptancePlanner_parses_not_met_with_unmet_criteria")]
    public void ParsesNotMetWithUnmetCriteria()
    {
        var output = """
            ```json
            {"criteria_met": false, "confidence": "medium", "reasons": ["only touched docs"], "unmet_criteria": ["no code change implements X"]}
            ```
            """;

        var verdict = SemanticAcceptancePlanner.Parse(output);

        Assert.True(verdict.IsValid);
        Assert.False(verdict.CriteriaMet);
        Assert.Equal(1, verdict.UnmetCriteria.Count);
    }

    [Xunit.Fact(DisplayName = "SemanticAcceptancePlanner_rejects_output_without_fenced_json")]
    public void RejectsOutputWithoutFencedJson()
    {
        var verdict = SemanticAcceptancePlanner.Parse("The change looks fine to me, criteria met.");

        Assert.False(verdict.IsValid);
        Assert.True(verdict.ValidationErrors.Count > 0);
    }

    [Xunit.Fact(DisplayName = "SemanticAcceptancePlanner_rejects_verdict_missing_criteria_met")]
    public void RejectsVerdictMissingCriteriaMet()
    {
        var output = """
            ```json
            {"confidence": "high", "reasons": ["looks good"]}
            ```
            """;

        var verdict = SemanticAcceptancePlanner.Parse(output);

        Assert.False(verdict.IsValid);
    }

    [Xunit.Fact(DisplayName = "SemanticAcceptancePlanner_evidence_context_includes_all_sections")]
    public void EvidenceContextIncludesAllSections()
    {
        var context = SemanticAcceptancePlanner.BuildEvidenceContext(SampleInputs());

        Assert.True(context.Contains("Add a GetDiffExcerpt helper", StringComparison.Ordinal));
        Assert.True(context.Contains("Focused tests pass", StringComparison.Ordinal));
        Assert.True(context.Contains("src/A.cs", StringComparison.Ordinal));
        Assert.True(context.Contains("diff --git", StringComparison.Ordinal));
        Assert.True(context.Contains("core tests: Passed", StringComparison.Ordinal));
    }

    [Xunit.Fact(DisplayName = "SemanticAcceptanceEvaluator_aggregates_agreeing_judges_into_consensus")]
    public async Task EvaluatorAggregatesAgreeingJudges()
    {
        var judges = new ISemanticJudge[]
        {
            new FakeJudge("local", _ => new SemanticAcceptanceVerdict(true, "high", ["ok"], [], [])),
            new FakeJudge("paid", _ => new SemanticAcceptanceVerdict(true, "medium", ["also ok"], [], []))
        };

        var report = await SemanticAcceptanceEvaluator.EvaluateAsync(judges, SampleInputs(), TimeSpan.FromSeconds(5));

        Assert.Equal(2, report.Verdicts.Count);
        Assert.True(report.AllValidJudgesAgree);
        Assert.Equal(true, report.Consensus);
    }

    [Xunit.Fact(DisplayName = "SemanticAcceptanceEvaluator_no_consensus_when_valid_judges_disagree")]
    public async Task EvaluatorNoConsensusWhenJudgesDisagree()
    {
        var judges = new ISemanticJudge[]
        {
            new FakeJudge("local", _ => new SemanticAcceptanceVerdict(true, "high", [], [], [])),
            new FakeJudge("paid", _ => new SemanticAcceptanceVerdict(false, "high", [], ["X missing"], []))
        };

        var report = await SemanticAcceptanceEvaluator.EvaluateAsync(judges, SampleInputs(), TimeSpan.FromSeconds(5));

        Assert.True(report.Consensus is null);
        Assert.False(report.AllValidJudgesAgree);
    }

    [Xunit.Fact(DisplayName = "SemanticAcceptanceEvaluator_swallows_a_throwing_judge_as_invalid_verdict")]
    public async Task EvaluatorSwallowsThrowingJudge()
    {
        var judges = new ISemanticJudge[]
        {
            new FakeJudge("local", _ => new SemanticAcceptanceVerdict(true, "high", [], [], [])),
            new ThrowingJudge()
        };

        var report = await SemanticAcceptanceEvaluator.EvaluateAsync(judges, SampleInputs(), TimeSpan.FromSeconds(5));

        Assert.Equal(2, report.Verdicts.Count);
        Assert.Equal(1, report.ValidVerdicts.Count);
        // One valid verdict alone is the consensus; a throwing judge never breaks the gate.
        Assert.Equal(true, report.Consensus);
    }

    [Xunit.Fact(DisplayName = "ModelRegistrySemanticJudge_completes_through_provider_and_parses_verdict")]
    public async Task ModelRegistryJudgeCompletesAndParses()
    {
        var response = """
            ```json
            {"criteria_met": true, "confidence": "high", "reasons": ["diff implements the helper"], "unmet_criteria": []}
            ```
            """;
        var registry = new InMemoryModelProviderRegistry([new FakeJudgeProvider("Ollama", response)]);
        var judge = new ModelRegistrySemanticJudge(registry, "Ollama", "qwen3:8b");

        var verdict = await judge.JudgeAsync(SampleInputs(), default);

        Assert.True(verdict.IsValid);
        Assert.True(verdict.CriteriaMet);
        Assert.Equal("ollama:qwen3:8b", judge.Name);
    }

    [Xunit.Fact(DisplayName = "SemanticAcceptanceEvaluator_BuildJudges_resolves_acceptance_judge_bindings_deduped")]
    public void BuildJudgesResolvesAcceptanceJudgeBindingsDeduped()
    {
        var providers = new InMemoryModelProviderRegistry([]);
        var catalog = new ModelFunctionCatalog(
        [
            JudgeBinding(ModelLane.Local, "Ollama", "qwen3:8b"),
            JudgeBinding(ModelLane.CheapApi, "Anthropic", "claude-haiku-4-5"),
            JudgeBinding(ModelLane.Local, "Ollama", "qwen3:8b"),
            // A binding for a DIFFERENT purpose must be ignored by the acceptance-judge resolution.
            new ModelFunctionBinding("planner-sampler", ModelLane.CheapApi,
                new ModelProfile("Anthropic", "claude-haiku-4-5", ModelCapability.Text, SubscriptionMode.ApiKey))
        ]);

        var judges = SemanticAcceptanceEvaluator.BuildJudges(catalog, providers);

        // Two distinct acceptance-judge lanes; the duplicate Ollama/qwen3:8b is deduped; the
        // planner-sampler binding is ignored.
        Assert.Equal(2, judges.Count);
        Assert.True(judges.Any(judge => judge.Name == "ollama:qwen3:8b"));
        Assert.True(judges.Any(judge => judge.Name == "anthropic:claude-haiku-4-5"));
    }

    [Xunit.Fact(DisplayName = "SemanticAcceptanceEvaluator_BuildJudges_empty_when_no_acceptance_judge_binding")]
    public void BuildJudgesEmptyWhenNoAcceptanceJudgeBinding()
    {
        var providers = new InMemoryModelProviderRegistry([]);
        var catalog = new ModelFunctionCatalog(
        [
            new ModelFunctionBinding("planner-sampler", ModelLane.CheapApi,
                new ModelProfile("Anthropic", "claude-haiku-4-5", ModelCapability.Text, SubscriptionMode.ApiKey))
        ]);

        Assert.Equal(0, SemanticAcceptanceEvaluator.BuildJudges(catalog, providers).Count);
        Assert.Equal(0, SemanticAcceptanceEvaluator.BuildJudges(ModelFunctionCatalog.Empty, providers).Count);
    }

    private static ModelFunctionBinding JudgeBinding(ModelLane lane, string provider, string model) =>
        new(ModelFunctionPurposes.AcceptanceJudge, lane,
            new ModelProfile(provider, model, ModelCapability.Text,
                lane == ModelLane.Local ? SubscriptionMode.LocalBridge : SubscriptionMode.ApiKey));

    private sealed class FakeJudge(string name, Func<SemanticAcceptanceInputs, SemanticAcceptanceVerdict> verdict) : ISemanticJudge
    {
        public string Name { get; } = name;

        public Task<SemanticAcceptanceVerdict> JudgeAsync(SemanticAcceptanceInputs inputs, CancellationToken cancellationToken)
            => Task.FromResult(verdict(inputs));
    }

    private sealed class ThrowingJudge : ISemanticJudge
    {
        public string Name => "throwing";

        public Task<SemanticAcceptanceVerdict> JudgeAsync(SemanticAcceptanceInputs inputs, CancellationToken cancellationToken)
            => throw new InvalidOperationException("judge boom");
    }

    private sealed class FakeJudgeProvider(string providerName, string text) : IModelProvider
    {
        public string ProviderName { get; } = providerName;

        public Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken cancellationToken)
            => Task.FromResult(new ModelResponse(text, null, "stop"));
    }
}
