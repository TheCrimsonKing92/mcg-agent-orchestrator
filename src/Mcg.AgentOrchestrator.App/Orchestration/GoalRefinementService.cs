using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using System.Security.Cryptography;
using System.Text;

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
    private static readonly HashSet<string> QuestionStopWords = new(StringComparer.Ordinal)
    {
        "a", "an", "and", "are", "as", "be", "by", "for", "from", "how", "in", "is", "it", "of", "on",
        "or", "should", "that", "the", "this", "to", "what", "when", "where", "which", "with"
    };

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
        var resolvedClarifications = await LoadResolvedClarificationsAsync(goal, cancellationToken);
        var existingOpenQuestions = goal.RefinedSpec?.OpenQuestions
            .Where(question => string.Equals(question.Status, "Open", StringComparison.OrdinalIgnoreCase))
            .ToList() ?? [];
        var output = await RunRefinerAsync(goal.Objective, resolvedClarifications, cancellationToken);

        if (!output.IsValid)
        {
            var fallback = BuildFallbackSpec(goal.Objective);
            kernel.SetGoalRefinedSpec(goalId, fallback);
            return RefinementResult.AutoRefined(fallback);
        }

        var decisions = new List<RefinedSpecDecision>(output.Decisions);
        foreach (var resolved in resolvedClarifications)
        {
            AddDecisionIfMissing(
                decisions,
                resolved.Question,
                resolved.WasDismissed ? "dismissed by operator" : resolved.Resolution,
                resolved.WasDismissed
                    ? $"Dismissed by operator; topic already resolved (topic: {resolved.TopicKey})."
                    : $"Answered by operator; topic already resolved (topic: {resolved.TopicKey}).");
        }

        var acceptanceCriteria = output.AcceptanceCriteria.ToList();
        var operatorOwnedCriteria = goal.RefinedSpec?.OperatorOwnedAcceptanceCriteria.ToList() ?? [];
        var scenarioBackedCriteria = ApplyFeasibilityResolutions(
            acceptanceCriteria,
            operatorOwnedCriteria,
            resolvedClarifications);
        var feasibilityFindings = AcceptanceCriterionFeasibility
            .Evaluate(acceptanceCriteria, AgentRole.Developer)
            .Where(finding => !scenarioBackedCriteria.Contains(finding.Criterion))
            .ToList();

        var openQuestions = new List<RefinedSpecOpenQuestion>();
        var surfacedTopicKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var surfacedNormalizedQuestionKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var question in existingOpenQuestions)
        {
            surfacedTopicKeys.Add(ResolveQuestionTopicKey(question));
            surfacedNormalizedQuestionKeys.Add(ResolveQuestionNormalizedKey(question));
        }

        foreach (var finding in feasibilityFindings)
        {
            var topicKey = finding.TopicKey;
            var questionText = AcceptanceCriterionFeasibility.BuildQuestion(finding);
            var normalizedQuestionKey = BuildNormalizedQuestionKey(
                AcceptanceCriterionFeasibility.ForkKind,
                questionText);
            var matchingResolution = resolvedClarifications.FirstOrDefault(item =>
                string.Equals(item.TopicKey, topicKey, StringComparison.OrdinalIgnoreCase));
            var existingOpen = existingOpenQuestions.FirstOrDefault(question =>
                string.Equals(ResolveQuestionTopicKey(question), topicKey, StringComparison.OrdinalIgnoreCase));
            if (existingOpen is not null && matchingResolution is null)
            {
                AddOpenQuestionIfMissing(openQuestions, existingOpen);
                continue;
            }

            surfacedTopicKeys.Add(topicKey);
            surfacedNormalizedQuestionKeys.Add(normalizedQuestionKey);
            var correlationKey = BuildCorrelationKey(goalId, topicKey);
            await _collaboration.RaiseAsync(
                CollaborationItemType.Clarification,
                goalId.Value,
                $"Spec feasibility clarification needed: {finding.Criterion}",
                BuildFeasibilityClarificationBody(goal.Objective, finding),
                correlationKey,
                cancellationToken);
            openQuestions.Add(new RefinedSpecOpenQuestion(
                correlationKey,
                questionText,
                AcceptanceCriterionFeasibility.ForkKind,
                "Open",
                TopicKey: topicKey,
                NormalizedQuestionKey: normalizedQuestionKey,
                Criterion: finding.Criterion,
                BlastRadius: AcceptanceCriterionFeasibility.FixedBlastRadius));
        }

        foreach (var fork in output.Forks)
        {
            var withheldFor = AcceptanceCriterionFeasibility.FindMeasurementOrAssertionForkCriterion(
                fork,
                feasibilityFindings);
            if (withheldFor is not null)
            {
                var withheldTopicKey = NormalizeTopicKey(fork.TopicKey, fork.Kind, fork.Question);
                var withheldNormalizedQuestionKey = BuildNormalizedQuestionKey(fork.Kind, fork.Question);
                var withheldResolution = resolvedClarifications.FirstOrDefault(item =>
                    string.Equals(item.TopicKey, withheldTopicKey, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(item.NormalizedQuestionKey, withheldNormalizedQuestionKey, StringComparison.Ordinal));
                if (withheldResolution is not null)
                {
                    AddDecisionIfMissing(
                        decisions,
                        fork.Question,
                        withheldResolution.WasDismissed ? "dismissed by operator" : withheldResolution.Resolution,
                        $"Answered by operator; feasibility-dependent clarification already resolved (topic: {withheldResolution.TopicKey}).");
                    continue;
                }

                if (SpecRefinerPlanner.ClassifyFork(fork, policy ?? ConductorAutonomyPolicy.Conservative) == SpecForkDisposition.Ask)
                {
                    openQuestions.Add(new RefinedSpecOpenQuestion(
                        BuildCorrelationKey(goalId, withheldTopicKey),
                        fork.Question,
                        fork.Kind,
                        "Withheld",
                        TopicKey: withheldTopicKey,
                        NormalizedQuestionKey: withheldNormalizedQuestionKey,
                        Criterion: withheldFor.Criterion,
                        BlastRadius: fork.BlastRadius));
                }

                continue;
            }

            var topicKey = NormalizeTopicKey(fork.TopicKey, fork.Kind, fork.Question);
            var normalizedQuestionKey = BuildNormalizedQuestionKey(fork.Kind, fork.Question);
            var resolved = resolvedClarifications.FirstOrDefault(item =>
                string.Equals(item.TopicKey, topicKey, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(item.NormalizedQuestionKey, normalizedQuestionKey, StringComparison.Ordinal));
            if (resolved is not null)
            {
                AddDecisionIfMissing(
                    decisions,
                    fork.Question,
                    resolved.WasDismissed ? "dismissed by operator" : resolved.Resolution,
                    resolved.WasDismissed
                        ? $"Dismissed by operator; stale regenerated clarification suppressed (topic: {resolved.TopicKey})."
                        : $"Answered by operator; stale regenerated clarification suppressed (topic: {resolved.TopicKey}).");
                continue;
            }

            var existingOpen = existingOpenQuestions.FirstOrDefault(question =>
                string.Equals(ResolveQuestionTopicKey(question), topicKey, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(ResolveQuestionNormalizedKey(question), normalizedQuestionKey, StringComparison.Ordinal));
            if (existingOpen is not null)
            {
                AddOpenQuestionIfMissing(openQuestions, existingOpen);
                continue;
            }

            var disposition = SpecRefinerPlanner.ClassifyFork(fork, policy ?? ConductorAutonomyPolicy.Conservative);
            if (disposition == SpecForkDisposition.Ask)
            {
                var precedent = await _precedents.TryGetPrecedentAsync(topicKey, cancellationToken)
                    ?? await _precedents.TryGetPrecedentAsync(fork.Kind, cancellationToken);
                if (precedent is not null)
                {
                    AddDecisionIfMissing(
                        decisions,
                        fork.Question,
                        precedent.Choice,
                        $"Precedent ({topicKey}): {precedent.Rationale}");
                }
                else if (!surfacedTopicKeys.Add(topicKey) ||
                    !surfacedNormalizedQuestionKeys.Add(normalizedQuestionKey))
                {
                    continue;
                }
                else
                {
                    var correlationKey = BuildCorrelationKey(goalId, topicKey);
                    await _collaboration.RaiseAsync(
                        CollaborationItemType.Clarification,
                        goalId.Value,
                        $"Spec clarification needed: {fork.Question}",
                        BuildClarificationBody(goal.Objective, fork),
                        correlationKey,
                        cancellationToken);
                    openQuestions.Add(new RefinedSpecOpenQuestion(
                        correlationKey,
                        fork.Question,
                        fork.Kind,
                        "Open",
                        TopicKey: topicKey,
                        NormalizedQuestionKey: normalizedQuestionKey));
                }
            }
            else
            {
                AddDecisionIfMissing(decisions, fork.Question, fork.Choice, fork.Rationale);
            }
        }

        var spec = new RefinedSpec(
            output.BehavioralContract,
            acceptanceCriteria,
            output.VerificationClass,
            decisions,
            openQuestions)
        {
            OperatorOwnedAcceptanceCriteria = operatorOwnedCriteria
        };

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
        var goalId = ExtractGoalId(correlationKey);
        var matchingItem = (await _collaboration.ListAsync(goalId, cancellationToken))
            .FirstOrDefault(item =>
                string.Equals(item.CorrelationKey, correlationKey, StringComparison.Ordinal) &&
                !CollaborationItemLifecycle.IsTerminal(item.Status));
        if (matchingItem is not null &&
            IsFeasibilityClarification(matchingItem) &&
            !AcceptanceCriterionFeasibility.TryParseDisposition(answer, out _))
        {
            return false;
        }

        var resolved = await _collaboration.TryResolveAsync(correlationKey, answer, cancellationToken);
        if (!resolved)
            return false;

        var topicKey = ExtractTopicKey(correlationKey);
        if (!string.IsNullOrWhiteSpace(topicKey))
        {
            await _precedents.RecordPrecedentAsync(
                topicKey,
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

        var matchedQuestion = spec.OpenQuestions.FirstOrDefault(question =>
            string.Equals(question.Id, correlationKey, StringComparison.Ordinal));
        if (matchedQuestion is not null &&
            string.Equals(matchedQuestion.ForkKind, AcceptanceCriterionFeasibility.ForkKind, StringComparison.OrdinalIgnoreCase) &&
            !AcceptanceCriterionFeasibility.TryParseDisposition(answer, out _))
        {
            return false;
        }

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

        if (matchedQuestion is not null &&
            string.Equals(matchedQuestion.ForkKind, AcceptanceCriterionFeasibility.ForkKind, StringComparison.OrdinalIgnoreCase))
        {
            await ApplyFeasibilityResolutionAsync(
                kernel,
                goalId,
                spec,
                matchedQuestion,
                answer,
                cancellationToken);
            kernel.RecordGoalPolicyDecision(goalId, $"Spec feasibility clarification answered and re-checked: {correlationKey}");
            return true;
        }

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
                !string.IsNullOrWhiteSpace(item.CorrelationKey))
            .GroupBy(item => item.CorrelationKey!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => BuildResolutionText(group.First()), StringComparer.Ordinal);

        if (answers.Count == 0)
            return spec;

        var changed = false;
        var decisions = new List<RefinedSpecDecision>(spec.Decisions);
        var questions = spec.OpenQuestions
            .Select(question =>
            {
                if (string.Equals(question.ForkKind, AcceptanceCriterionFeasibility.ForkKind, StringComparison.OrdinalIgnoreCase))
                    return question;

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

    public RefinedSpec? SyncAnsweredFeasibilityClarifications(
        AgentOrchestratorKernel kernel,
        GoalId goalId)
    {
        var goal = kernel.Goals.FirstOrDefault(candidate => candidate.Id == goalId);
        if (goal?.RefinedSpec is not { } spec || !spec.HasOpenQuestions)
            return goal?.RefinedSpec;

        var answers = _collaboration.ListAsync(goalId.Value).GetAwaiter().GetResult()
            .Where(item =>
                item.Type == CollaborationItemType.Clarification &&
                CollaborationItemLifecycle.IsTerminal(item.Status) &&
                !string.IsNullOrWhiteSpace(item.CorrelationKey))
            .GroupBy(item => item.CorrelationKey!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => BuildResolutionText(group.Last()), StringComparer.Ordinal);

        foreach (var question in spec.OpenQuestions.Where(question =>
                     string.Equals(question.Status, "Open", StringComparison.OrdinalIgnoreCase) &&
                     string.Equals(question.ForkKind, AcceptanceCriterionFeasibility.ForkKind, StringComparison.OrdinalIgnoreCase)))
        {
            if (!answers.TryGetValue(question.Id, out var answer))
                continue;
            if (string.Equals(question.Answer, answer, StringComparison.Ordinal))
                continue;

            spec = ApplyFeasibilityResolutionAsync(kernel, goalId, spec, question, answer)
                .GetAwaiter().GetResult();
        }

        return spec;
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
        IReadOnlyList<ResolvedSpecClarification> resolvedClarifications,
        CancellationToken cancellationToken)
    {
        var binding = ResolveSpecRefinerBinding(_catalog.Bindings);
        var prompt = SpecRefinerPlanner.BuildPrompt(objective, resolvedClarifications);
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

    private static string BuildCorrelationKey(GoalId goalId, string topicKey)
    {
        // Stable by topic so a regenerated equivalent fork refreshes the same collaboration item.
        return $"{CorrelationKeyPrefix}{goalId.Value}:{NormalizeTopicKey(topicKey, null, topicKey)}";
    }

    private static string BuildClarificationBody(string objective, SpecRefinementFork fork) => $"""
        Goal objective: {objective}

        Question: {fork.Question}
        Fork kind: {fork.Kind}
        Topic key: {NormalizeTopicKey(fork.TopicKey, fork.Kind, fork.Question)}
        Blast radius: {fork.BlastRadius}
        Refiner confidence: {fork.RefinerConfidence}

        Please provide your answer to resolve this ambiguity before the goal can proceed.
        """;

    private static string BuildFeasibilityClarificationBody(
        string objective,
        CriterionFeasibilityFinding finding) => $"""
        Goal objective: {objective}

        Question: {AcceptanceCriterionFeasibility.BuildQuestion(finding)}
        Fork kind: {AcceptanceCriterionFeasibility.ForkKind}
        Topic key: {finding.TopicKey}
        Criterion: {finding.Criterion}
        Executing role: {finding.Role}
        Missing capabilities: {string.Join(", ", finding.MissingCapabilities)}
        Trigger categories: {string.Join(", ", finding.TriggerCategories)}
        Blast radius: {AcceptanceCriterionFeasibility.FixedBlastRadius}
        Refiner confidence: deterministic

        Dispatch remains blocked until one of the three listed dispositions is supplied.
        """;

    private async Task<RefinedSpec> ApplyFeasibilityResolutionAsync(
        AgentOrchestratorKernel kernel,
        GoalId goalId,
        RefinedSpec spec,
        RefinedSpecOpenQuestion question,
        string answer,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(question.Criterion) ||
            !AcceptanceCriterionFeasibility.TryParseDisposition(answer, out var disposition))
        {
            var unresolvedFinding = AcceptanceCriterionFeasibility
                .Evaluate([question.Criterion ?? question.Question], AgentRole.Developer)
                .FirstOrDefault();
            if (unresolvedFinding is not null)
            {
                await _collaboration.RaiseAsync(
                    CollaborationItemType.Clarification,
                    goalId.Value,
                    $"Spec feasibility clarification needed: {unresolvedFinding.Criterion}",
                    BuildFeasibilityClarificationBody(kernel.GetGoal(goalId).Objective, unresolvedFinding),
                    question.Id,
                    cancellationToken);
            }

            var rejected = spec with
            {
                OpenQuestions = spec.OpenQuestions
                    .Select(candidate => string.Equals(candidate.Id, question.Id, StringComparison.Ordinal)
                        ? candidate with { Answer = answer }
                        : candidate)
                    .ToList()
            };
            kernel.SetGoalRefinedSpec(goalId, rejected);
            kernel.RecordGoalPolicyDecision(
                goalId,
                $"Rejected feasibility clarification answer for '{question.Id}': expected re-scope, OPERATOR-OWNED, or supply-reproducing-scenario syntax; clarification remains open.");
            return rejected;
        }

        var workerCriteria = spec.AcceptanceCriteria.ToList();
        var operatorOwnedCriteria = spec.OperatorOwnedAcceptanceCriteria.ToList();
        var criterionIndex = workerCriteria.FindIndex(criterion =>
            string.Equals(criterion.Trim(), question.Criterion.Trim(), StringComparison.OrdinalIgnoreCase));
        if (criterionIndex < 0 && disposition.Kind is not FeasibilityDisposition.ReproducingScenario)
        {
            throw new InvalidOperationException(
                $"Cannot apply feasibility disposition '{disposition.Kind}' because criterion '{question.Criterion}' is absent from the worker acceptance set.");
        }

        string? replacement = null;
        if (disposition.Kind == FeasibilityDisposition.ReScope && criterionIndex >= 0)
        {
            replacement = disposition.Value!;
            workerCriteria[criterionIndex] = replacement;
        }
        else if (disposition.Kind == FeasibilityDisposition.OperatorOwned && criterionIndex >= 0)
        {
            var operatorCriterion = workerCriteria[criterionIndex];
            workerCriteria.RemoveAt(criterionIndex);
            if (!operatorOwnedCriteria.Contains(operatorCriterion, StringComparer.OrdinalIgnoreCase))
                operatorOwnedCriteria.Add(operatorCriterion);
        }

        var questions = spec.OpenQuestions
            .Where(candidate => !string.Equals(candidate.Id, question.Id, StringComparison.Ordinal))
            .ToList();
        CriterionFeasibilityFinding? replacementFinding = null;
        if (replacement is not null)
        {
            replacementFinding = AcceptanceCriterionFeasibility.Evaluate([replacement], AgentRole.Developer).FirstOrDefault();
            if (replacementFinding is not null)
            {
                var questionText = AcceptanceCriterionFeasibility.BuildQuestion(replacementFinding);
                var correlationKey = BuildCorrelationKey(goalId, replacementFinding.TopicKey);
                await _collaboration.RaiseAsync(
                    CollaborationItemType.Clarification,
                    goalId.Value,
                    $"Spec feasibility clarification needed: {replacementFinding.Criterion}",
                    BuildFeasibilityClarificationBody(kernel.GetGoal(goalId).Objective, replacementFinding),
                    correlationKey,
                    cancellationToken);
                questions.Add(new RefinedSpecOpenQuestion(
                    correlationKey,
                    questionText,
                    AcceptanceCriterionFeasibility.ForkKind,
                    "Open",
                    TopicKey: replacementFinding.TopicKey,
                    NormalizedQuestionKey: BuildNormalizedQuestionKey(
                        AcceptanceCriterionFeasibility.ForkKind,
                        questionText),
                    Criterion: replacementFinding.Criterion,
                    BlastRadius: AcceptanceCriterionFeasibility.FixedBlastRadius));
            }
        }

        var withheldQuestions = questions
            .Where(candidate =>
                string.Equals(candidate.Status, "Withheld", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(candidate.Criterion?.Trim(), question.Criterion.Trim(), StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (disposition.Kind == FeasibilityDisposition.OperatorOwned)
        {
            questions.RemoveAll(candidate => withheldQuestions.Contains(candidate));
        }
        else if (replacementFinding is not null)
        {
            questions = questions
                .Select(candidate => withheldQuestions.Contains(candidate)
                    ? candidate with { Criterion = replacement }
                    : candidate)
                .ToList();
        }
        else
        {
            foreach (var withheld in withheldQuestions)
            {
                var released = withheld with
                {
                    Status = "Open",
                    Criterion = replacement ?? withheld.Criterion
                };
                questions[questions.IndexOf(withheld)] = released;
                await _collaboration.RaiseAsync(
                    CollaborationItemType.Clarification,
                    goalId.Value,
                    $"Spec clarification needed: {released.Question}",
                    BuildReleasedClarificationBody(kernel.GetGoal(goalId).Objective, released),
                    released.Id,
                    cancellationToken);
            }
        }

        var decisions = spec.Decisions.ToList();
        AddDecisionIfMissing(
            decisions,
            question.Question,
            answer,
            $"Feasibility disposition applied and re-checked (topic: {ResolveQuestionTopicKey(question)}).");
        var updated = spec with
        {
            AcceptanceCriteria = workerCriteria,
            Decisions = decisions,
            OpenQuestions = questions,
            OperatorOwnedAcceptanceCriteria = operatorOwnedCriteria
        };
        kernel.SetGoalRefinedSpec(goalId, updated);
        return updated;
    }

    // Extracts topicKey from correlation key. Legacy keys had an additional nonce segment:
    // spec-clarification:{goalId}:{forkKind}:{guid}
    internal static string? ExtractTopicKey(string correlationKey)
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

    internal static string NormalizeTopicKey(string? topicKey, string? forkKind, string question)
    {
        var raw = FirstNonEmpty(topicKey, forkKind, question) ?? "other";
        var chars = raw
            .Trim()
            .ToLowerInvariant()
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : '-')
            .ToArray();
        var slug = string.Join(
            '-',
            new string(chars)
                .Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (string.IsNullOrWhiteSpace(slug))
            slug = "other";
        return slug.Length <= 35 ? slug : slug[..35].TrimEnd('-');
    }

    internal static string BuildNormalizedQuestionKey(string forkKind, string question)
    {
        var scope = NormalizeTopicKey(null, forkKind, forkKind);
        var normalized = NormalizeQuestionText(question);
        var hashInput = $"{scope}|global|{normalized}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(hashInput))).ToLowerInvariant()[..16];
        return $"{scope}:global:{hash}";
    }

    private static string NormalizeQuestionText(string question)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        foreach (var ch in question.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
            {
                current.Append(ch);
                continue;
            }

            AddToken(tokens, current);
        }

        AddToken(tokens, current);
        return string.Join(' ', tokens);
    }

    private static void AddToken(List<string> tokens, StringBuilder current)
    {
        if (current.Length == 0)
            return;

        var token = current.ToString();
        current.Clear();
        if (!QuestionStopWords.Contains(token))
            tokens.Add(token);
    }

    private async Task<IReadOnlyList<ResolvedSpecClarification>> LoadResolvedClarificationsAsync(
        Goal goal,
        CancellationToken cancellationToken)
    {
        var resolved = new List<ResolvedSpecClarification>();
        foreach (var question in goal.RefinedSpec?.OpenQuestions ?? [])
        {
            if (string.Equals(question.Status, "Open", StringComparison.OrdinalIgnoreCase) &&
                string.IsNullOrWhiteSpace(question.Answer))
            {
                continue;
            }

            resolved.Add(new ResolvedSpecClarification(
                ResolveQuestionTopicKey(question),
                ResolveQuestionNormalizedKey(question),
                question.Question,
                string.IsNullOrWhiteSpace(question.Answer) ? "dismissed by operator" : question.Answer!,
                IsDismissedResolution(question.Answer),
                question.ForkKind,
                question.Criterion));
        }

        foreach (var item in await _collaboration.ListAsync(goal.Id.Value, cancellationToken))
        {
            if (item.Type != CollaborationItemType.Clarification ||
                !CollaborationItemLifecycle.IsTerminal(item.Status) ||
                item.CorrelationKey?.StartsWith(CorrelationKeyPrefix, StringComparison.Ordinal) != true)
            {
                continue;
            }

            var question = ExtractQuestion(item);
            var forkKind = ExtractForkKindFromBody(item.Body) ?? ExtractTopicKey(item.CorrelationKey!) ?? "other";
            var topicKey = NormalizeTopicKey(ExtractTopicKey(item.CorrelationKey!), forkKind, question);
            resolved.Add(new ResolvedSpecClarification(
                topicKey,
                BuildNormalizedQuestionKey(forkKind, question),
                question,
                BuildResolutionText(item),
                IsDismissedResolution(item.Resolution),
                forkKind,
                ExtractCriterionFromBody(item.Body)));
        }

        return resolved
            .GroupBy(item => $"{item.TopicKey}|{item.NormalizedQuestionKey}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
    }

    internal static string ResolveQuestionTopicKey(RefinedSpecOpenQuestion question) =>
        NormalizeTopicKey(question.TopicKey ?? ExtractTopicKey(question.Id), question.ForkKind, question.Question);

    internal static string ResolveQuestionNormalizedKey(RefinedSpecOpenQuestion question) =>
        string.IsNullOrWhiteSpace(question.NormalizedQuestionKey)
            ? BuildNormalizedQuestionKey(question.ForkKind, question.Question)
            : question.NormalizedQuestionKey!;

    internal static bool MatchesResolvedIdentity(
        RefinedSpecOpenQuestion question,
        IReadOnlyCollection<ResolvedSpecClarification> resolvedClarifications)
    {
        var topicKey = ResolveQuestionTopicKey(question);
        var normalizedQuestionKey = ResolveQuestionNormalizedKey(question);
        return resolvedClarifications.Any(item =>
            string.Equals(item.TopicKey, topicKey, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(item.NormalizedQuestionKey, normalizedQuestionKey, StringComparison.Ordinal));
    }

    internal static string ExtractQuestion(CollaborationItem item)
    {
        const string subjectPrefix = "Spec clarification needed: ";
        if (item.Subject.StartsWith(subjectPrefix, StringComparison.Ordinal))
            return item.Subject[subjectPrefix.Length..].Trim();

        foreach (var line in item.Body.Split(["\r\n", "\n"], StringSplitOptions.None))
        {
            if (line.StartsWith("Question:", StringComparison.OrdinalIgnoreCase))
                return line["Question:".Length..].Trim();
        }

        return item.Subject.Trim();
    }

    internal static string? ExtractForkKindFromBody(string body)
    {
        foreach (var line in body.Split(["\r\n", "\n"], StringSplitOptions.None))
        {
            if (line.StartsWith("Fork kind:", StringComparison.OrdinalIgnoreCase))
                return line["Fork kind:".Length..].Trim();
        }

        return null;
    }

    internal static string? ExtractCriterionFromBody(string body)
    {
        foreach (var line in body.Split(["\r\n", "\n"], StringSplitOptions.None))
        {
            if (line.StartsWith("Criterion:", StringComparison.OrdinalIgnoreCase))
                return line["Criterion:".Length..].Trim();
        }

        return null;
    }

    private static HashSet<string> ApplyFeasibilityResolutions(
        List<string> workerCriteria,
        List<string> operatorOwnedCriteria,
        IReadOnlyList<ResolvedSpecClarification> resolvedClarifications)
    {
        var scenarioBacked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var resolved in resolvedClarifications.Where(item =>
                     string.Equals(item.ForkKind, AcceptanceCriterionFeasibility.ForkKind, StringComparison.OrdinalIgnoreCase) &&
                     !string.IsNullOrWhiteSpace(item.Criterion) &&
                     AcceptanceCriterionFeasibility.TryParseDisposition(item.Resolution, out _)))
        {
            AcceptanceCriterionFeasibility.TryParseDisposition(resolved.Resolution, out var disposition);
            var criterionIndex = workerCriteria.FindIndex(criterion =>
                string.Equals(criterion.Trim(), resolved.Criterion!.Trim(), StringComparison.OrdinalIgnoreCase));
            switch (disposition.Kind)
            {
                case FeasibilityDisposition.ReScope when criterionIndex >= 0:
                    workerCriteria[criterionIndex] = disposition.Value!;
                    break;
                case FeasibilityDisposition.OperatorOwned when criterionIndex >= 0:
                    var operatorCriterion = workerCriteria[criterionIndex];
                    workerCriteria.RemoveAt(criterionIndex);
                    if (!operatorOwnedCriteria.Contains(operatorCriterion, StringComparer.OrdinalIgnoreCase))
                        operatorOwnedCriteria.Add(operatorCriterion);
                    break;
                case FeasibilityDisposition.ReproducingScenario:
                    scenarioBacked.Add(resolved.Criterion!);
                    break;
            }
        }

        return scenarioBacked;
    }

    private static string BuildResolutionText(CollaborationItem item) =>
        string.IsNullOrWhiteSpace(item.Resolution) ? "dismissed by operator" : item.Resolution!;

    private static string BuildReleasedClarificationBody(
        string objective,
        RefinedSpecOpenQuestion question) => $"""
        Goal objective: {objective}

        Question: {question.Question}
        Fork kind: {question.ForkKind}
        Topic key: {ResolveQuestionTopicKey(question)}
        Blast radius: {question.BlastRadius ?? "high"}

        The criterion's feasibility disposition is resolved. Please answer this previously withheld measurement/assertion clarification.
        """;

    private static bool IsFeasibilityClarification(CollaborationItem item) =>
        item.Type == CollaborationItemType.Clarification &&
        item.Body.Contains($"Fork kind: {AcceptanceCriterionFeasibility.ForkKind}", StringComparison.OrdinalIgnoreCase);

    private static bool IsDismissedResolution(string? resolution) =>
        string.IsNullOrWhiteSpace(resolution) ||
        resolution.Contains("dismiss", StringComparison.OrdinalIgnoreCase);

    private static void AddDecisionIfMissing(
        List<RefinedSpecDecision> decisions,
        string question,
        string choice,
        string rationale)
    {
        if (!decisions.Any(decision => string.Equals(decision.Question, question, StringComparison.OrdinalIgnoreCase)))
            decisions.Add(new RefinedSpecDecision(question, choice, rationale));
    }

    private static void AddOpenQuestionIfMissing(
        List<RefinedSpecOpenQuestion> questions,
        RefinedSpecOpenQuestion question)
    {
        if (!questions.Any(candidate =>
            string.Equals(candidate.Id, question.Id, StringComparison.Ordinal) ||
            string.Equals(ResolveQuestionTopicKey(candidate), ResolveQuestionTopicKey(question), StringComparison.OrdinalIgnoreCase) ||
            string.Equals(ResolveQuestionNormalizedKey(candidate), ResolveQuestionNormalizedKey(question), StringComparison.Ordinal)))
        {
            questions.Add(question);
        }
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
}
