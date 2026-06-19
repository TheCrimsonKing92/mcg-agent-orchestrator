using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal enum RefinementOutcome { AutoRefined, AwaitingClarification }

internal sealed record RefinementResult(RefinementOutcome Outcome, RefinedSpec Spec)
{
    public static RefinementResult AutoRefined(RefinedSpec spec) =>
        new(RefinementOutcome.AutoRefined, spec);

    public static RefinementResult AwaitingClarification(RefinedSpec spec) =>
        new(RefinementOutcome.AwaitingClarification, spec);
}

// Orchestrates the goal refinement stage: calls the spec-refiner model function, classifies each
// fork on three axes, raises Clarification items for high-stakes ambiguities the refiner can't
// resolve, and attaches the resulting RefinedSpec to the goal. Falls back to a minimal spec when
// no spec-refiner binding is configured so refinement never blocks goals silently.
internal sealed class GoalRefinementService
{
    // Correlation key prefix used to look up open clarification items for a goal.
    internal const string CorrelationKeyPrefix = "spec-clarification:";

    private readonly IModelProviderRegistry _providers;
    private readonly ModelFunctionCatalog _catalog;
    private readonly ICollaborationItemStore _collaboration;
    private readonly SpecRefinerPrecedentStore _precedents;

    public GoalRefinementService(
        IModelProviderRegistry providers,
        ModelFunctionCatalog catalog,
        ICollaborationItemStore collaboration,
        SpecRefinerPrecedentStore precedents)
    {
        _providers = providers;
        _catalog = catalog;
        _collaboration = collaboration;
        _precedents = precedents;
    }

    public async Task<RefinementResult> RefineAsync(
        AgentOrchestratorKernel kernel,
        GoalId goalId,
        CancellationToken cancellationToken = default)
    {
        var goal = kernel.GetGoal(goalId);
        var output = await RunRefinerAsync(goal.Objective, cancellationToken);

        if (!output.IsValid)
        {
            var fallback = BuildFallbackSpec(goal.Objective);
            kernel.SetGoalRefinedSpec(goalId, fallback);
            return RefinementResult.AutoRefined(fallback);
        }

        var decisions = new List<RefinedSpecDecision>(output.Decisions);
        var openQuestions = new List<RefinedSpecOpenQuestion>();

        foreach (var fork in output.Forks)
        {
            var disposition = SpecRefinerPlanner.ClassifyFork(fork);
            if (disposition == SpecForkDisposition.Ask)
            {
                var precedent = await _precedents.TryGetPrecedentAsync(fork.Kind, cancellationToken);
                if (precedent is not null)
                {
                    decisions.Add(new RefinedSpecDecision(
                        fork.Question,
                        precedent.Choice,
                        $"Precedent ({fork.Kind}): {precedent.Rationale}"));
                }
                else
                {
                    var correlationKey = BuildCorrelationKey(goalId, fork.Kind);
                    await _collaboration.RaiseAsync(
                        CollaborationItemType.Clarification,
                        goalId.Value,
                        $"Spec clarification needed: {fork.Question}",
                        BuildClarificationBody(goal.Objective, fork),
                        correlationKey,
                        cancellationToken);
                    openQuestions.Add(new RefinedSpecOpenQuestion(
                        correlationKey, fork.Question, fork.Kind, "Open"));
                }
            }
            else if (!decisions.Any(d =>
                string.Equals(d.Question, fork.Question, StringComparison.Ordinal)))
            {
                decisions.Add(new RefinedSpecDecision(fork.Question, fork.Choice, fork.Rationale));
            }
        }

        var spec = new RefinedSpec(
            output.BehavioralContract,
            output.AcceptanceCriteria,
            output.VerificationClass,
            decisions,
            openQuestions);

        kernel.SetGoalRefinedSpec(goalId, spec);

        return spec.HasOpenQuestions
            ? RefinementResult.AwaitingClarification(spec)
            : RefinementResult.AutoRefined(spec);
    }

    // Resolves an open clarification by correlationKey and records the answer as a precedent.
    public async Task<bool> TryResolveOpenClarificationAsync(
        string correlationKey,
        string answer,
        CancellationToken cancellationToken = default)
    {
        var resolved = await _collaboration.TryResolveAsync(correlationKey, answer, cancellationToken);
        if (!resolved)
            return false;

        var forkKind = ExtractForkKind(correlationKey);
        if (!string.IsNullOrWhiteSpace(forkKind))
        {
            await _precedents.RecordPrecedentAsync(
                forkKind,
                answer,
                $"Resolved via operator (key: {correlationKey})",
                cancellationToken);
        }

        return true;
    }

    // Returns true when a goal has at least one open Clarification item in the collaboration store.
    // Used by the App layer to populate GoalLifecycleFacts.HasOpenClarification.
    public static bool HasOpenClarification(IReadOnlyList<CollaborationItem> goalItems) =>
        goalItems.Any(item =>
            item.Type == CollaborationItemType.Clarification &&
            !CollaborationItemLifecycle.IsTerminal(item.Status) &&
            item.CorrelationKey?.StartsWith(CorrelationKeyPrefix, StringComparison.Ordinal) == true);

    private async Task<SpecRefinementOutput> RunRefinerAsync(
        string objective,
        CancellationToken cancellationToken)
    {
        var bindings = _catalog.ForPurpose(ModelFunctionPurposes.SpecRefiner);
        if (bindings.Count == 0)
            return SpecRefinementOutput.Invalid("No spec-refiner model function configured.");

        var binding = bindings[0];
        IModelProvider provider;
        try
        {
            provider = _providers.GetRequired(binding.Model.ProviderName);
        }
        catch (Exception ex)
        {
            return SpecRefinementOutput.Invalid($"Spec-refiner provider '{binding.Model.ProviderName}' not found: {ex.Message}");
        }

        var prompt = SpecRefinerPlanner.BuildPrompt(objective);
        var request = new ModelRequest(
            "You are a specification refiner. Output only a fenced JSON object.",
            [new ModelMessage("user", prompt)],
            new ModelOptions(Temperature: 0.2, MaxOutputTokens: 2000, ModelName: binding.Model.ModelName));

        try
        {
            var response = await provider.CompleteAsync(request, cancellationToken).ConfigureAwait(false);
            return SpecRefinerPlanner.Parse(response.Text);
        }
        catch (Exception ex)
        {
            return SpecRefinementOutput.Invalid($"Spec refiner model call failed: {ex.Message}");
        }
    }

    private static RefinedSpec BuildFallbackSpec(string objective) =>
        new(
            $"Implement: {objective}",
            [$"The objective is achieved: {objective}"],
            VerificationClass.TestVerifiable,
            [],
            []);

    private static string BuildCorrelationKey(GoalId goalId, string forkKind) =>
        $"{CorrelationKeyPrefix}{goalId.Value}:{forkKind}:{Guid.NewGuid():n}";

    private static string BuildClarificationBody(string objective, SpecRefinementFork fork) => $"""
        Goal objective: {objective}

        Question: {fork.Question}
        Fork kind: {fork.Kind}
        Blast radius: {fork.BlastRadius}
        Refiner confidence: {fork.RefinerConfidence}

        Please provide your answer to resolve this ambiguity before the goal can proceed.
        """;

    // Extracts forkKind from correlation key: spec-clarification:{goalId}:{forkKind}:{guid}
    private static string? ExtractForkKind(string correlationKey)
    {
        if (!correlationKey.StartsWith(CorrelationKeyPrefix, StringComparison.Ordinal))
            return null;
        var remainder = correlationKey[CorrelationKeyPrefix.Length..];
        // remainder: {goalId}:{forkKind}:{guid}
        var firstColon = remainder.IndexOf(':', StringComparison.Ordinal);
        if (firstColon < 0)
            return null;
        var afterGoalId = remainder[(firstColon + 1)..];
        var secondColon = afterGoalId.IndexOf(':', StringComparison.Ordinal);
        return secondColon < 0 ? afterGoalId : afterGoalId[..secondColon];
    }
}
