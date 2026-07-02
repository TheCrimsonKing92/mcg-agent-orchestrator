using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
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
// resolve, and attaches the resulting RefinedSpec to the goal. Configuration errors are surfaced
// immediately so refinement cannot silently run on the wrong model.
internal sealed class GoalRefinementService
{
    // Correlation key prefix used to look up open clarification items for a goal.
    internal const string CorrelationKeyPrefix = "spec-clarification:";

    private readonly IModelProviderRegistry _providers;
    private readonly ModelFunctionCatalog _catalog;
    private readonly ICollaborationItemStore _collaboration;
    private readonly SpecRefinerPrecedentStore _precedents;
    private readonly WorkerProfileCatalog? _workerProfiles;
    private readonly Func<SubscriptionLaunchProfile, SubscriptionCliCompleter>? _subscriptionCompleterFactory;

    public GoalRefinementService(
        IModelProviderRegistry providers,
        ModelFunctionCatalog catalog,
        ICollaborationItemStore collaboration,
        SpecRefinerPrecedentStore precedents,
        WorkerProfileCatalog? workerProfiles = null,
        Func<SubscriptionLaunchProfile, SubscriptionCliCompleter>? subscriptionCompleterFactory = null)
    {
        _providers = providers;
        _catalog = catalog;
        _collaboration = collaboration;
        _precedents = precedents;
        _workerProfiles = workerProfiles;
        _subscriptionCompleterFactory = subscriptionCompleterFactory;
    }

    public async Task<RefinementResult> RefineAsync(
        AgentOrchestratorKernel kernel,
        GoalId goalId,
        ConductorAutonomyPolicy? policy = null,
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
            var disposition = SpecRefinerPlanner.ClassifyFork(fork, policy ?? ConductorAutonomyPolicy.Conservative);
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

    // Resolves a clarification, writes the answer into the matching RefinedSpec question,
    // and records the answer as a precedent for later goals.
    public async Task<bool> TryResolveOpenClarificationAsync(
        AgentOrchestratorKernel kernel,
        string correlationKey,
        string answer,
        CancellationToken cancellationToken = default)
    {
        var goalIdValue = ExtractGoalId(correlationKey);
        if (goalIdValue is null)
            return await TryResolveOpenClarificationAsync(correlationKey, answer, cancellationToken);

        var goalId = new GoalId(goalIdValue);
        var goal = kernel.Goals.FirstOrDefault(candidate => candidate.Id == goalId);
        if (goal is null)
        {
            // The goal is absent from this kernel (e.g. an empty/stale listener kernel). Degrade to the
            // store-only resolve so the clarification still clears in Discord and the precedent is
            // recorded, rather than rejecting the operator's answer outright.
            Console.Error.WriteLine(
                $"Warning: clarification goal '{goalIdValue}' was not found in the active kernel; resolving '{correlationKey}' through the collaboration store only.");
            return await TryResolveOpenClarificationAsync(correlationKey, answer, cancellationToken);
        }
        var spec = goal.RefinedSpec;
        if (spec is null)
            return await TryResolveOpenClarificationAsync(correlationKey, answer, cancellationToken);

        var matched = false;
        var questions = spec.OpenQuestions
            .Select(question =>
            {
                if (!string.Equals(question.Id, correlationKey, StringComparison.Ordinal))
                    return question;

                matched = true;
                return question with { Status = "Answered", Answer = answer };
            })
            .ToList();

        if (!matched)
            return false;

        var resolved = await TryResolveOpenClarificationAsync(correlationKey, answer, cancellationToken);
        if (!resolved)
            return false;

        var decisions = spec.Decisions
            .Concat(spec.OpenQuestions
                .Where(question => string.Equals(question.Id, correlationKey, StringComparison.Ordinal))
                .Select(question => new RefinedSpecDecision(
                    question.Question,
                    answer,
                    $"Answered by operator (key: {correlationKey}).")))
            .ToList();

        kernel.SetGoalRefinedSpec(goalId, spec with
        {
            Decisions = decisions,
            OpenQuestions = questions
        });
        kernel.RecordGoalPolicyDecision(goalId, $"Spec clarification answered: {correlationKey}");
        return true;
    }

    // Applies operator answers recorded in the collaboration store (resolved clarification items) into
    // the goal's RefinedSpec open questions. Run by the goal-owning process (the conductor, via the
    // refinement gate): an answer submitted through the listener only resolves the store item, so this
    // is where that answer takes effect on the spec and the goal resumes with the operator's decision.
    // Returns the current (possibly updated) RefinedSpec for the goal.
    public RefinedSpec? SyncAnsweredClarifications(AgentOrchestratorKernel kernel, GoalId goalId)
    {
        var goal = kernel.Goals.FirstOrDefault(candidate => candidate.Id == goalId);
        if (goal?.RefinedSpec is not { } spec || !spec.HasOpenQuestions)
            return goal?.RefinedSpec;

        var answers = _collaboration.ListAsync(goalId.Value).GetAwaiter().GetResult()
            .Where(item =>
                item.Type == CollaborationItemType.Clarification &&
                CollaborationItemLifecycle.IsTerminal(item.Status) &&
                !string.IsNullOrWhiteSpace(item.CorrelationKey) &&
                !string.IsNullOrWhiteSpace(item.Resolution))
            .GroupBy(item => item.CorrelationKey!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Resolution!, StringComparer.Ordinal);

        if (answers.Count == 0)
            return spec;

        var changed = false;
        var decisions = new List<RefinedSpecDecision>(spec.Decisions);
        var questions = spec.OpenQuestions
            .Select(question =>
            {
                if (string.Equals(question.Status, "Answered", StringComparison.OrdinalIgnoreCase) ||
                    !answers.TryGetValue(question.Id, out var answer))
                    return question;

                changed = true;
                decisions.Add(new RefinedSpecDecision(
                    question.Question, answer, $"Answered by operator (key: {question.Id})."));
                return question with { Status = "Answered", Answer = answer };
            })
            .ToList();

        if (!changed)
            return spec;

        var updated = spec with { Decisions = decisions, OpenQuestions = questions };
        kernel.SetGoalRefinedSpec(goalId, updated);
        kernel.RecordGoalPolicyDecision(goalId, "Synced operator answers from resolved clarification items into the RefinedSpec.");
        return updated;
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
        var binding = ResolveSpecRefinerBinding(_catalog.Bindings);
        var prompt = SpecRefinerPlanner.BuildPrompt(objective);
        if (binding.Subscription is { } subscription && _workerProfiles is not null)
        {
            return await RunSubscriptionRefinerAsync(subscription, prompt, cancellationToken).ConfigureAwait(false);
        }

        IModelProvider provider;
        try
        {
            provider = _providers.GetRequired(binding.Model.ProviderName);
        }
        catch (Exception ex)
        {
            return SpecRefinementOutput.Invalid($"Spec-refiner provider '{binding.Model.ProviderName}' not found: {ex.Message}");
        }

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

    private static ModelFunctionBinding ResolveSpecRefinerBinding(IReadOnlyList<ModelFunctionBinding> bindings)
    {
        const string identifier = ModelFunctionPurposes.SpecRefiner;
        var matches = bindings
            .Where(binding => string.Equals(BindingIdentifier(binding), identifier, StringComparison.Ordinal))
            .ToList();

        if (matches.Count == 1)
            return matches[0];

        var configured = bindings.Count == 0
            ? "<none>"
            : string.Join(", ", bindings.Select(binding =>
                $"{BindingIdentifier(binding)} ({binding.Model.ProviderName}/{binding.Model.ModelName})"));
        if (matches.Count == 0)
        {
            throw new InvalidOperationException(
                $"No model-function binding found for '{identifier}'. Configured bindings: {configured}.");
        }

        throw new InvalidOperationException(
            $"Multiple model-function bindings found for '{identifier}'. Configured bindings: {configured}.");
    }

    private static string BindingIdentifier(ModelFunctionBinding binding) =>
        string.IsNullOrWhiteSpace(binding.Name) ? binding.Purpose : binding.Name;

    private async Task<SpecRefinementOutput> RunSubscriptionRefinerAsync(
        SubscriptionLaunchProfile subscription,
        string prompt,
        CancellationToken cancellationToken)
    {
        try
        {
            var completer = _subscriptionCompleterFactory?.Invoke(subscription)
                ?? new SubscriptionCliCompleter(
                    _workerProfiles!,
                    subscription.WorkerProfileName,
                    subscription.ModelAlias ?? string.Empty,
                    subscription.ReasoningEffort);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(SubscriptionCliCompleter.DefaultTimeout);
            var stdout = await completer.CompleteAsync(prompt, "spec-refiner-prompt.md", cts.Token).ConfigureAwait(false);
            return SpecRefinerPlanner.Parse(stdout);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return SpecRefinementOutput.Invalid($"Subscription spec refiner '{subscription.WorkerProfileName}' timed out.");
        }
        catch (Exception ex)
        {
            return SpecRefinementOutput.Invalid($"Subscription spec refiner '{subscription.WorkerProfileName}' failed: {ex.Message}");
        }
    }

    private static RefinedSpec BuildFallbackSpec(string objective) =>
        new(
            $"Implement: {objective}",
            [$"The objective is achieved: {objective}"],
            VerificationClass.TestVerifiable,
            [],
            []);

    private static string BuildCorrelationKey(GoalId goalId, string forkKind)
    {
        // Keep the key short enough to fit inside Discord's 100-char custom_id once prefixed
        // for answer buttons/modals, while retaining the full goal id for spec write-back.
        var nonce = Guid.NewGuid().ToString("n")[..16];
        return $"{CorrelationKeyPrefix}{goalId.Value}:{forkKind}:{nonce}";
    }

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

    private static string? ExtractGoalId(string correlationKey)
    {
        if (!correlationKey.StartsWith(CorrelationKeyPrefix, StringComparison.Ordinal))
            return null;
        var remainder = correlationKey[CorrelationKeyPrefix.Length..];
        var firstColon = remainder.IndexOf(':', StringComparison.Ordinal);
        return firstColon < 0 ? null : remainder[..firstColon];
    }
}
