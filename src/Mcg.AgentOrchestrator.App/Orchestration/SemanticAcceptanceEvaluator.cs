using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// A semantic-acceptance judge: given the change inputs, returns a verdict on whether the diff
// satisfies the objective. Implementations wrap a model (local Ollama via the provider registry
// now; a parallel subscription judge is the next increment, for local-vs-paid comparison).
internal interface ISemanticJudge
{
    string Name { get; }

    Task<SemanticAcceptanceVerdict> JudgeAsync(SemanticAcceptanceInputs inputs, CancellationToken cancellationToken);
}

internal sealed record JudgeVerdict(string Judge, SemanticAcceptanceVerdict Verdict);

internal sealed record SemanticAcceptanceReport(IReadOnlyList<JudgeVerdict> Verdicts)
{
    public bool RanAnyJudge => Verdicts.Count > 0;

    public IReadOnlyList<JudgeVerdict> ValidVerdicts =>
        Verdicts.Where(verdict => verdict.Verdict.IsValid).ToList();

    // Null when no judge returned a valid verdict, or when valid judges disagree on the outcome.
    // Used by the (future) blocking flip and the local-vs-subscription agreement comparison.
    public bool? Consensus
    {
        get
        {
            var valid = ValidVerdicts;
            if (valid.Count == 0)
            {
                return null;
            }

            var first = valid[0].Verdict.CriteriaMet;
            return valid.All(verdict => verdict.Verdict.CriteriaMet == first) ? first : null;
        }
    }

    public bool AllValidJudgesAgree => ValidVerdicts.Count > 1 && Consensus is not null;
}

// Local-model semantic judge over the provider registry (e.g. Ollama). Text-only completion — the
// judge needs no file access, just the prompt-rendered evidence.
internal sealed class ModelRegistrySemanticJudge : ISemanticJudge
{
    private readonly IModelProviderRegistry _providers;
    private readonly string _providerName;
    private readonly string _modelName;

    public ModelRegistrySemanticJudge(IModelProviderRegistry providers, string providerName, string modelName)
    {
        _providers = providers;
        _providerName = providerName;
        _modelName = modelName;
    }

    public string Name => $"{_providerName.ToLowerInvariant()}:{_modelName}";

    public async Task<SemanticAcceptanceVerdict> JudgeAsync(
        SemanticAcceptanceInputs inputs,
        CancellationToken cancellationToken)
    {
        var prompt = SemanticAcceptancePlanner.BuildPrompt(SemanticAcceptancePlanner.BuildEvidenceContext(inputs));
        var provider = _providers.GetRequired(_providerName);
        var request = new ModelRequest(
            "You are a strict, evidence-grounded software acceptance reviewer.",
            [new ModelMessage("user", prompt)],
            new ModelOptions(Temperature: 0.0, MaxOutputTokens: 800, ModelName: _modelName));

        var response = await provider.CompleteAsync(request, cancellationToken).ConfigureAwait(false);
        return SemanticAcceptancePlanner.Parse(response.Text);
    }
}

internal static class SemanticAcceptanceEvaluator
{
    // Resolves the configured judge lanes from the agent catalog: one `ModelRegistrySemanticJudge`
    // per Judge-role agent, deduped by provider/model so the same lane isn't judged twice. Returns
    // empty when no Judge agent is configured (semantic acceptance then stays dormant).
    public static IReadOnlyList<ISemanticJudge> BuildJudges(
        IReadOnlyList<AgentDefinition> agents,
        IModelProviderRegistry providers)
    {
        return agents
            .Where(agent => agent.Role == AgentRole.Judge)
            .DistinctBy(
                agent => $"{agent.Model.ProviderName}/{agent.Model.ModelName}",
                StringComparer.OrdinalIgnoreCase)
            .Select(agent => (ISemanticJudge)new ModelRegistrySemanticJudge(
                providers,
                agent.Model.ProviderName,
                agent.Model.ModelName))
            .ToList();
    }

    // Runs all judges IN PARALLEL, each with its own timeout. ADVISORY: a judge that throws, times
    // out, or returns an unparseable verdict is recorded as an invalid verdict and NEVER propagates
    // — semantic acceptance must not be able to break the deterministic merge gate while advisory.
    // Verdict order matches judge order (Task.WhenAll preserves it); aggregation is set-based so it
    // is order-independent regardless.
    public static async Task<SemanticAcceptanceReport> EvaluateAsync(
        IReadOnlyList<ISemanticJudge> judges,
        SemanticAcceptanceInputs inputs,
        TimeSpan perJudgeTimeout,
        CancellationToken cancellationToken = default)
    {
        var verdicts = await Task.WhenAll(
            judges.Select(judge => RunJudgeAsync(judge, inputs, perJudgeTimeout, cancellationToken)))
            .ConfigureAwait(false);

        return new SemanticAcceptanceReport(verdicts);
    }

    private static async Task<JudgeVerdict> RunJudgeAsync(
        ISemanticJudge judge,
        SemanticAcceptanceInputs inputs,
        TimeSpan perJudgeTimeout,
        CancellationToken cancellationToken)
    {
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(perJudgeTimeout);
            var verdict = await judge.JudgeAsync(inputs, timeoutCts.Token).ConfigureAwait(false);
            return new JudgeVerdict(judge.Name, verdict);
        }
        catch (OperationCanceledException)
        {
            return new JudgeVerdict(judge.Name, SemanticAcceptanceVerdict.Invalid(
                $"Judge '{judge.Name}' timed out after {perJudgeTimeout.TotalSeconds:F0}s."));
        }
        catch (Exception ex)
        {
            return new JudgeVerdict(judge.Name, SemanticAcceptanceVerdict.Invalid(
                $"Judge '{judge.Name}' failed: {ex.Message}"));
        }
    }
}
