using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using System.Security.Cryptography;
using System.Text;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal enum RefinementOutcome { AutoRefined, AwaitingClarification }

internal enum RefinementDisposition { Completed, Fallback }

internal sealed record RefinementInvocationMetadata(
    int PromptCharacterCount,
    int PromptUtf8ByteCount,
    string ProviderKind,
    string WorkerProfile,
    string Model,
    string ReasoningEffort);

internal sealed record RefinementFailure(string ReasonCode, string Detail);

internal sealed record RefinementResult(
    RefinementOutcome Outcome,
    RefinedSpec Spec,
    RefinementDisposition Disposition,
    RefinementInvocationMetadata Invocation,
    RefinementFailure? Failure)
{
    public static RefinementResult Completed(
        RefinementOutcome outcome,
        RefinedSpec spec,
        RefinementInvocationMetadata invocation) =>
        new(outcome, spec, RefinementDisposition.Completed, invocation, null);

    public static RefinementResult Fallback(
        RefinedSpec spec,
        RefinementInvocationMetadata invocation,
        RefinementFailure failure) =>
        new(RefinementOutcome.AutoRefined, spec, RefinementDisposition.Fallback, invocation, failure);
}

internal sealed record RefinerAttempt(
    SpecRefinementOutput Output,
    RefinementInvocationMetadata Invocation,
    RefinementFailure? Failure = null,
    string? RawOutputDecision = null);

internal delegate Task<CollaborationItem> CollaborationItemRaise(
    CollaborationItemType type,
    string? goalId,
    string subject,
    string body,
    string? correlationKey = null,
    CancellationToken cancellationToken = default);

// Orchestrates the goal refinement stage: calls the spec-refiner model function, classifies each
// fork on three axes, raises Clarification items for high-stakes ambiguities the refiner can't
// resolve, and attaches the resulting RefinedSpec to the goal. Configuration errors are surfaced
// immediately so refinement cannot silently run on the wrong model.
internal sealed partial class GoalRefinementService
{
    // Correlation key prefix used to look up open clarification items for a goal.
    internal const string CorrelationKeyPrefix = "spec-clarification:";

    private readonly IModelProviderRegistry _providers;
    private readonly ModelFunctionCatalog _catalog;
    private readonly ICollaborationItemStore _collaboration;
    private readonly CollaborationItemRaise _raiseCollaborationItem;
    private readonly SpecRefinerPrecedentStore _precedents;
    private readonly WorkerProfileCatalog? _workerProfiles;
    private readonly Func<SubscriptionLaunchProfile, SubscriptionCliCompleter>? _subscriptionCompleterFactory;
    private readonly TimeSpan _subscriptionTimeout;
    private readonly string _rawOutputDirectory;
    private readonly string? _rawOutputStamp;
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
        Func<SubscriptionLaunchProfile, SubscriptionCliCompleter>? subscriptionCompleterFactory = null,
        CollaborationItemRaise? raiseCollaborationItem = null,
        TimeSpan? subscriptionTimeout = null,
        string? rawOutputDirectory = null,
        string? rawOutputStamp = null)
    {
        _providers = providers;
        _catalog = catalog;
        _collaboration = collaboration;
        _raiseCollaborationItem = raiseCollaborationItem ?? collaboration.RaiseAsync;
        _precedents = precedents;
        _workerProfiles = workerProfiles;
        _subscriptionCompleterFactory = subscriptionCompleterFactory;
        _subscriptionTimeout = subscriptionTimeout ?? SubscriptionCliCompleter.DefaultTimeout;
        _rawOutputDirectory = Path.GetFullPath(rawOutputDirectory ??
            Path.Combine(OrchestratorTempRoot.GetPurposeDirectory("spec-refiner"), "logs"));
        if (rawOutputStamp is not null &&
            (rawOutputStamp.Length != 17 || rawOutputStamp.Any(character => !char.IsAsciiDigit(character))))
        {
            throw new ArgumentException("Raw-output stamp must contain exactly 17 ASCII digits.", nameof(rawOutputStamp));
        }
        _rawOutputStamp = rawOutputStamp;
    }

    public async Task<RefinementResult> RefineAsync(
        AgentOrchestratorKernel kernel,
        GoalId goalId,
        ConductorAutonomyPolicy? policy = null,
        CancellationToken cancellationToken = default)
    {
        var goal = kernel.GetGoal(goalId);
        var includeObjectiveInNextClarification = !(await _collaboration.ListAsync(goalId.Value, cancellationToken))
            .Any(item =>
                item.Type == CollaborationItemType.Clarification &&
                !CollaborationItemLifecycle.IsTerminal(item.Status) &&
                item.Body.Contains("Goal objective:", StringComparison.OrdinalIgnoreCase));
        var resolvedClarifications = await LoadResolvedClarificationsAsync(goal, cancellationToken);
        var existingOpenQuestions = goal.RefinedSpec?.OpenQuestions
            .Where(question => string.Equals(question.Status, "Open", StringComparison.OrdinalIgnoreCase))
            .ToList() ?? [];
        var clarificationAnswerHistory = goal.RefinedSpec?.ClarificationAnswerHistory ?? [];
        var attempt = await RunRefinerAsync(goalId, goal.Objective, resolvedClarifications, cancellationToken);
        var output = attempt.Output;
        if (!string.IsNullOrWhiteSpace(attempt.RawOutputDecision))
            kernel.RecordGoalPolicyDecision(goalId, attempt.RawOutputDecision);

        if (!output.IsValid)
        {
            var fallback = BuildFallbackSpec(goal.Objective) with
            {
                ClarificationAnswerHistory = clarificationAnswerHistory
            };
            kernel.RecordGoalRefinement(goalId, fallback);
            kernel.RecordGoalPolicyDecision(goalId, FallbackVerificationClassPolicy.BuildOwnershipReceipt(fallback));
            return RefinementResult.Fallback(
                fallback,
                attempt.Invocation,
                attempt.Failure ?? new RefinementFailure(
                    "invalid-output",
                    output.ValidationErrors.FirstOrDefault() ?? "Spec refiner output was invalid."));
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

        var rawDeclaredCriteria = AcceptanceCriteriaParser.ParseDeclared(goal.Objective);
        var declaredCriteria = rawDeclaredCriteria
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var acceptanceCriteria = ResolveAcceptanceCriteria(goal.Objective, output.AcceptanceCriteria);
        var operatorOwnedCriteria = goal.RefinedSpec?.OperatorOwnedAcceptanceCriteria.ToList() ?? [];
        var parseDiagnostics = output.ParseDiagnostics
            .Where(diagnostic => declaredCriteria.Count > 0 ||
                !diagnostic.Contains("declared_index", StringComparison.Ordinal))
            .ToList();
        AddDuplicateDeclaredCriterionDiagnostics(rawDeclaredCriteria, parseDiagnostics);
        List<string> acceptanceGateOwnedCriteria;
        if (declaredCriteria.Count > 0)
        {
            acceptanceGateOwnedCriteria = ResolveDeclaredOwnedCriteria(
                acceptanceCriteria,
                output.CriterionOwnerships,
                operatorOwnedCriteria,
                parseDiagnostics);
        }
        else
        {
            AddOwnedCriteria(operatorOwnedCriteria, acceptanceCriteria, output.OperatorOwnedAcceptanceCriteria);
            acceptanceGateOwnedCriteria = ResolveOwnedCriteria(
                acceptanceCriteria,
                output.AcceptanceGateOwnedAcceptanceCriteria,
                operatorOwnedCriteria);
        }
        AddMarkerDerivedGateOwnedCriteria(
            acceptanceCriteria,
            operatorOwnedCriteria,
            acceptanceGateOwnedCriteria);
        var scenarioBackedCriteria = ApplyFeasibilityResolutions(
            acceptanceCriteria,
            operatorOwnedCriteria,
            resolvedClarifications);
        acceptanceGateOwnedCriteria.RemoveAll(criterion =>
            operatorOwnedCriteria.Contains(criterion, StringComparer.OrdinalIgnoreCase));
        var feasibilityFindings = AcceptanceCriterionFeasibility
            .Evaluate(acceptanceCriteria, fallbackRole: AgentRole.Developer)
            .Where(finding =>
                !scenarioBackedCriteria.Contains(finding.Criterion) &&
                !operatorOwnedCriteria.Contains(finding.Criterion, StringComparer.OrdinalIgnoreCase))
            .ToList();
        foreach (var diagnostic in parseDiagnostics)
            kernel.RecordGoalPolicyDecision(goalId, $"Spec refiner parse diagnostic: {diagnostic}");

        var openQuestions = new List<RefinedSpecOpenQuestion>();
        var raisedClarificationRound = false;
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
            await _raiseCollaborationItem(
                CollaborationItemType.Clarification,
                goalId.Value,
                BuildClarificationSubject(topicKey),
                BuildFeasibilityClarificationBody(
                    finding,
                    includeObjectiveInNextClarification ? goal.Objective : null),
                correlationKey,
                cancellationToken);
            raisedClarificationRound = true;
            includeObjectiveInNextClarification = false;
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
                    var authoritativePrecedent = await ResolveAuthoritativePrecedentAsync(
                        precedent,
                        topicKey,
                        cancellationToken);
                    if (authoritativePrecedent is not null)
                    {
                        AddDecisionIfMissing(
                            decisions,
                            fork.Question,
                            authoritativePrecedent.Value.Choice,
                            authoritativePrecedent.Value.Rationale);
                        continue;
                    }
                }

                if (!surfacedTopicKeys.Add(topicKey) ||
                    !surfacedNormalizedQuestionKeys.Add(normalizedQuestionKey))
                {
                    continue;
                }

                var correlationKey = BuildCorrelationKey(goalId, topicKey);
                await _raiseCollaborationItem(
                    CollaborationItemType.Clarification,
                    goalId.Value,
                    BuildClarificationSubject(topicKey),
                    BuildClarificationBody(
                        fork,
                        includeObjectiveInNextClarification ? goal.Objective : null),
                    correlationKey,
                    cancellationToken);
                raisedClarificationRound = true;
                includeObjectiveInNextClarification = false;
                openQuestions.Add(new RefinedSpecOpenQuestion(
                    correlationKey,
                    fork.Question,
                    fork.Kind,
                    "Open",
                    TopicKey: topicKey,
                    NormalizedQuestionKey: normalizedQuestionKey));
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
            OperatorOwnedAcceptanceCriteria = operatorOwnedCriteria,
            AcceptanceGateOwnedAcceptanceCriteria = acceptanceGateOwnedCriteria,
            ClarificationAnswerHistory = clarificationAnswerHistory
        };

        kernel.RecordGoalRefinement(goalId, spec);
        if (raisedClarificationRound)
            kernel.RecordGoalClarificationRound(goalId);

        return RefinementResult.Completed(
            spec.HasOpenQuestions ? RefinementOutcome.AwaitingClarification : RefinementOutcome.AutoRefined,
            spec,
            attempt.Invocation);
    }

    // Resolves an open clarification by correlationKey and records the answer as a precedent.
    public async Task<bool> TryResolveOpenClarificationAsync(
        string correlationKey,
        string answer,
        CancellationToken cancellationToken = default)
    {
        return await TryResolveOpenClarificationAsync(
            correlationKey,
            answer,
            briefVersion: null,
            cancellationToken);
    }

    public async Task<bool> TryResolveOpenClarificationAsync(
        string correlationKey,
        string answer,
        int briefVersion,
        CancellationToken cancellationToken = default)
    {
        return await TryResolveOpenClarificationAsync(
            correlationKey,
            answer,
            (int?)briefVersion,
            cancellationToken);
    }

    private async Task<bool> TryResolveOpenClarificationAsync(
        string correlationKey,
        string answer,
        int? briefVersion,
        CancellationToken cancellationToken)
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

        var resolved = await _collaboration.TryResolveAsync(
            correlationKey,
            answer,
            cancellationToken,
            briefVersion);
        if (!resolved)
            return false;

        await RecordResolvedClarificationPrecedentAsync(correlationKey, goalId, cancellationToken);

        return true;
    }

    private async Task<(string Choice, string Rationale)?> ResolveAuthoritativePrecedentAsync(
        SpecRefinerPrecedent precedent,
        string topicKey,
        CancellationToken cancellationToken)
    {
        var effectivePrecedent = precedent;
        if (!precedent.HasAnyOriginReference)
        {
            var recoveredOrigins = (await _collaboration.ListAsync(cancellationToken: cancellationToken))
                .Where(item =>
                    item.Type == CollaborationItemType.Clarification &&
                    !string.IsNullOrWhiteSpace(item.GoalId) &&
                    item.CorrelationKey is not null &&
                    string.Equals(
                        ExtractTopicKey(item.CorrelationKey),
                        precedent.ForkKind,
                        StringComparison.OrdinalIgnoreCase))
                .SelectMany(item => (item.AnswerHistory ?? [])
                    .Where(answer => string.Equals(answer.Text, precedent.Choice, StringComparison.Ordinal))
                    .Select(answer => (Item: item, Answer: answer)))
                .ToArray();
            if (recoveredOrigins.Length == 0)
            {
                return (
                    precedent.Choice,
                    $"Unlinked legacy precedent ({topicKey}): {precedent.Rationale}");
            }
            if (recoveredOrigins.Length != 1)
            {
                var allMatchesRemainAuthoritative = recoveredOrigins.All(candidate =>
                    !candidate.Answer.IsRetracted &&
                    string.Equals(
                        candidate.Item.AuthoritativeAnswer?.Id,
                        candidate.Answer.Id,
                        StringComparison.Ordinal));
                return allMatchesRemainAuthoritative
                    ? (
                        precedent.Choice,
                        $"Unlinked legacy precedent ({topicKey}; multiple current associations): {precedent.Rationale}")
                    : null;
            }

            var recovered = recoveredOrigins[0];
            effectivePrecedent = precedent with
            {
                OriginItemId = recovered.Item.Id,
                OriginGoalId = recovered.Item.GoalId,
                OriginAnswerId = recovered.Answer.Id,
                OriginBriefVersion = recovered.Answer.BriefVersion
            };
        }

        if (!effectivePrecedent.HasCompleteOriginReference)
            return null;

        var originItem = (await _collaboration.ListAsync(effectivePrecedent.OriginGoalId, cancellationToken))
            .FirstOrDefault(item =>
                string.Equals(item.Id, effectivePrecedent.OriginItemId, StringComparison.Ordinal) &&
                string.Equals(item.GoalId, effectivePrecedent.OriginGoalId, StringComparison.Ordinal));
        if (originItem is null ||
            originItem.Type != CollaborationItemType.Clarification ||
            string.IsNullOrWhiteSpace(originItem.CorrelationKey) ||
            !string.Equals(
                ExtractTopicKey(originItem.CorrelationKey),
                effectivePrecedent.ForkKind,
                StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var history = originItem.AnswerHistory ?? [];
        var referencedAnswer = history.FirstOrDefault(answer =>
            string.Equals(answer.Id, effectivePrecedent.OriginAnswerId, StringComparison.Ordinal));
        var authoritativeAnswer = originItem.AuthoritativeAnswer;
        if (referencedAnswer is null ||
            authoritativeAnswer is null ||
            referencedAnswer.BriefVersion != effectivePrecedent.OriginBriefVersion ||
            !ReachesAuthoritativeAnswer(referencedAnswer, authoritativeAnswer, history))
        {
            return null;
        }

        var briefBasis = authoritativeAnswer.BriefVersion?.ToString() ?? "unknown";
        return (
            authoritativeAnswer.Text,
            $"Authoritative clarification precedent (topic: {topicKey}, item: {originItem.Id}, " +
            $"answer: {authoritativeAnswer.Id}, brief: {briefBasis}).");
    }

    private static bool ReachesAuthoritativeAnswer(
        HumanInputAnswerRecord referencedAnswer,
        HumanInputAnswerRecord authoritativeAnswer,
        IReadOnlyList<HumanInputAnswerRecord> history)
    {
        HumanInputAnswerRecord? current = referencedAnswer;
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (current is not null && visited.Add(current.Id))
        {
            if (string.Equals(current.Id, authoritativeAnswer.Id, StringComparison.Ordinal))
                return true;
            if (string.IsNullOrWhiteSpace(current.SupersededByAnswerId))
                return false;
            current = history.FirstOrDefault(answer =>
                string.Equals(answer.Id, current.SupersededByAnswerId, StringComparison.Ordinal));
        }

        return false;
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

        var resolved = await TryResolveOpenClarificationAsync(
            correlationKey,
            answer,
            goal.AuthoritativeBrief.Version,
            cancellationToken);
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
    // the goal's RefinedSpec open questions. The durable refinement coordinator is the sole caller: an
    // answer submitted through the listener only resolves the store item, so this is where that answer
    // takes effect on the spec and the goal resumes with the operator's decision.
    // Returns the current (possibly updated) RefinedSpec for the goal.
    public async Task<RefinedSpec?> SyncAnsweredClarificationsAsync(
        AgentOrchestratorKernel kernel,
        GoalId goalId,
        CancellationToken cancellationToken = default)
    {
        var goal = kernel.Goals.FirstOrDefault(candidate => candidate.Id == goalId);
        if (goal?.RefinedSpec is not { } spec)
            return goal?.RefinedSpec;

        var resolvedItems = (await _collaboration.ListAsync(goalId.Value, cancellationToken).ConfigureAwait(false))
            .Where(item =>
                item.Type == CollaborationItemType.Clarification &&
                CollaborationItemLifecycle.IsTerminal(item.Status) &&
                !string.IsNullOrWhiteSpace(item.CorrelationKey))
            .ToArray();
        var answers = resolvedItems
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

                if (!answers.TryGetValue(question.Id, out var answer))
                    return question;

                var rationale = $"Answered by operator (key: {question.Id}).";
                var decisionIndex = decisions.FindIndex(decision =>
                    string.Equals(decision.Rationale, rationale, StringComparison.Ordinal));
                var replacementDecision = new RefinedSpecDecision(question.Question, answer, rationale);
                if (decisionIndex < 0)
                {
                    decisions.Add(replacementDecision);
                    changed = true;
                }
                else if (decisions[decisionIndex] != replacementDecision)
                {
                    decisions[decisionIndex] = replacementDecision;
                    changed = true;
                }

                if (!string.Equals(question.Status, "Answered", StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(question.Answer, answer, StringComparison.Ordinal))
                {
                    changed = true;
                    return question with { Status = "Answered", Answer = answer };
                }

                return question;
            })
            .ToList();

        var answerHistory = resolvedItems
            .SelectMany(item => item.AnswerHistory ?? [])
            .OrderBy(answer => answer.AnsweredAt)
            .ThenBy(answer => answer.Id, StringComparer.Ordinal)
            .ToArray();
        if (!spec.ClarificationAnswerHistory.SequenceEqual(answerHistory))
            changed = true;

        if (!changed)
            return spec;

        var updated = spec with
        {
            Decisions = decisions,
            OpenQuestions = questions,
            ClarificationAnswerHistory = answerHistory
        };
        kernel.SetGoalRefinedSpec(goalId, updated);
        kernel.RecordGoalPolicyDecision(goalId, "Synced operator answers from resolved clarification items into the RefinedSpec.");
        return updated;
    }

    public async Task<RefinedSpec?> SyncAnsweredFeasibilityClarificationsAsync(
        AgentOrchestratorKernel kernel,
        GoalId goalId,
        CancellationToken cancellationToken = default)
    {
        var goal = kernel.Goals.FirstOrDefault(candidate => candidate.Id == goalId);
        if (goal?.RefinedSpec is not { } spec || !spec.HasOpenQuestions)
            return goal?.RefinedSpec;

        var answers = (await _collaboration.ListAsync(goalId.Value, cancellationToken).ConfigureAwait(false))
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

            spec = await ApplyFeasibilityResolutionAsync(
                    kernel,
                    goalId,
                    spec,
                    question,
                    answer,
                    cancellationToken)
                .ConfigureAwait(false);
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

    private async Task<RefinerAttempt> RunRefinerAsync(
        GoalId goalId,
        string objective,
        IReadOnlyList<ResolvedSpecClarification> resolvedClarifications,
        CancellationToken cancellationToken)
    {
        var binding = ResolveSpecRefinerBinding(_catalog.Bindings);
        var prompt = SpecRefinerPlanner.BuildPrompt(objective, resolvedClarifications);
        var promptCharacters = prompt.Length;
        var promptBytes = Encoding.UTF8.GetByteCount(prompt);
        var declaredCriteriaCount = ParseDeclaredAcceptanceCriteria(objective).Count;
        int? declaredIndexUpperBound = declaredCriteriaCount > 0 ? declaredCriteriaCount : null;
        if (binding.Subscription is { } subscription && _workerProfiles is not null)
        {
            var invocation = new RefinementInvocationMetadata(
                promptCharacters,
                promptBytes,
                WorkerProviderCatalog.Default().ResolveProfile(subscription.WorkerProfileName).Identity.Kind.ToString(),
                subscription.WorkerProfileName,
                subscription.ModelAlias ?? string.Empty,
                string.IsNullOrWhiteSpace(subscription.ReasoningEffort)
                    ? AgentCatalog.ComplexReasoningEffort
                    : subscription.ReasoningEffort);
            return await RunSubscriptionRefinerAsync(
                goalId,
                subscription,
                prompt,
                invocation,
                declaredIndexUpperBound,
                cancellationToken).ConfigureAwait(false);
        }

        var apiInvocation = new RefinementInvocationMetadata(
            promptCharacters,
            promptBytes,
            SubscriptionMode.ApiKey.ToString(),
            "none",
            binding.Model.ModelName,
            "provider-default");

        IModelProvider provider;
        try
        {
            provider = _providers.GetRequired(binding.Model.ProviderName);
        }
        catch (Exception ex)
        {
            var detail = $"Spec-refiner provider '{binding.Model.ProviderName}' not found: {ex.Message}";
            return new RefinerAttempt(
                SpecRefinementOutput.Invalid(detail),
                apiInvocation,
                new RefinementFailure("provider-unavailable", detail));
        }

        var request = new ModelRequest(
            "You are a specification refiner. Output only a fenced JSON object.",
            [new ModelMessage("user", prompt)],
            new ModelOptions(Temperature: 0.2, MaxOutputTokens: 2000, ModelName: binding.Model.ModelName));

        try
        {
            var response = await provider.CompleteAsync(request, cancellationToken).ConfigureAwait(false);
            var rawOutputDecision = await PersistRawOutputAsync(goalId, response.Text).ConfigureAwait(false);
            var output = SpecRefinerPlanner.Parse(response.Text, declaredIndexUpperBound);
            return output.IsValid
                ? new RefinerAttempt(output, apiInvocation, RawOutputDecision: rawOutputDecision)
                : new RefinerAttempt(
                    output,
                    apiInvocation,
                    new RefinementFailure(
                        "invalid-output",
                        output.ValidationErrors.FirstOrDefault() ?? "Spec refiner output was invalid."),
                    rawOutputDecision);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var detail = $"Spec refiner model call failed: {ex.Message}";
            return new RefinerAttempt(
                SpecRefinementOutput.Invalid(detail),
                apiInvocation,
                new RefinementFailure("provider-failed", detail));
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

    private async Task<RefinerAttempt> RunSubscriptionRefinerAsync(
        GoalId goalId,
        SubscriptionLaunchProfile subscription,
        string prompt,
        RefinementInvocationMetadata invocation,
        int? declaredIndexUpperBound,
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
            cts.CancelAfter(_subscriptionTimeout);
            var stdout = await completer.CompleteAsync(prompt, "spec-refiner-prompt.md", cts.Token).ConfigureAwait(false);
            var rawOutputDecision = await PersistRawOutputAsync(goalId, stdout).ConfigureAwait(false);
            var output = SpecRefinerPlanner.Parse(stdout, declaredIndexUpperBound);
            return output.IsValid
                ? new RefinerAttempt(output, invocation, RawOutputDecision: rawOutputDecision)
                : new RefinerAttempt(
                    output,
                    invocation,
                    new RefinementFailure(
                        "invalid-output",
                        output.ValidationErrors.FirstOrDefault() ?? "Subscription spec refiner output was invalid."),
                    rawOutputDecision);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            var detail = $"Subscription spec refiner '{subscription.WorkerProfileName}' timed out after {_subscriptionTimeout.TotalSeconds:0.###} seconds.";
            return new RefinerAttempt(
                SpecRefinementOutput.Invalid(detail),
                invocation,
                new RefinementFailure("subscription-timeout", detail));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var detail = $"Subscription spec refiner '{subscription.WorkerProfileName}' failed: {ex.Message}";
            return new RefinerAttempt(
                SpecRefinementOutput.Invalid(detail),
                invocation,
                new RefinementFailure("subscription-failed", detail));
        }
    }

    private async Task<string> PersistRawOutputAsync(GoalId goalId, string responseText)
    {
        try
        {
            Directory.CreateDirectory(_rawOutputDirectory);
            var prefix = goalId.Value[..Math.Min(8, goalId.Value.Length)];
            var stamp = _rawOutputStamp ?? DateTimeOffset.UtcNow.ToString(
                "yyyyMMddHHmmssfff",
                System.Globalization.CultureInfo.InvariantCulture);
            for (var attempt = 1; ; attempt++)
            {
                var suffix = attempt == 1 ? string.Empty : $"-{attempt}";
                var path = Path.GetFullPath(Path.Combine(
                    _rawOutputDirectory,
                    $"spec-refinement-{prefix}-{stamp}{suffix}.refiner.txt"));
                FileStream stream;
                try
                {
                    stream = new FileStream(
                        path,
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.Read,
                        4096,
                        FileOptions.Asynchronous);
                }
                catch (IOException) when (File.Exists(path))
                {
                    // Preserve each attempt when two writes share the same millisecond stamp.
                    continue;
                }

                await using (stream)
                await using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                {
                    await writer.WriteAsync(responseText.AsMemory(), CancellationToken.None).ConfigureAwait(false);
                    await writer.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                }
                return $"Spec refiner raw output: {path}";
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return $"Spec refiner raw output not persisted: {ex.Message}";
        }
    }

    private static RefinedSpec BuildFallbackSpec(string objective)
    {
        var declaredCriteria = ParseDeclaredAcceptanceCriteria(objective);
        IReadOnlyList<string> criteria = declaredCriteria.Count > 0
            ? declaredCriteria
            : [$"The objective is achieved: {objective}"];
        var ownership = CriterionOwnershipDerivation.DeriveForRevision(criteria, [], []);
        return new RefinedSpec(
            $"Implement: {objective}",
            criteria,
            FallbackVerificationClassPolicy.Classify(criteria),
            [],
            [])
        {
            AcceptanceGateOwnedAcceptanceCriteria = ownership.AcceptanceGateOwned,
            OperatorOwnedAcceptanceCriteria = ownership.OperatorOwned
        };
    }

    private static List<string> ResolveAcceptanceCriteria(
        string objective,
        IReadOnlyList<string> refinedCriteria)
    {
        var declaredCriteria = ParseDeclaredAcceptanceCriteria(objective);
        if (declaredCriteria.Count > 0)
            return declaredCriteria;

        return refinedCriteria
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<string> ParseDeclaredAcceptanceCriteria(string objective) =>
        AcceptanceCriteriaParser.ParseDeclared(objective)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static string BuildCorrelationKey(GoalId goalId, string topicKey)
    {
        // Stable by topic so a regenerated equivalent fork refreshes the same collaboration item.
        return $"{CorrelationKeyPrefix}{goalId.Value}:{NormalizeTopicKey(topicKey, null, topicKey)}";
    }

    private static string BuildClarificationSubject(string topicKey) =>
        NormalizeTopicKey(topicKey, null, topicKey);

    private static string BuildClarificationBody(SpecRefinementFork fork, string? objective) => $"""
        Question: {fork.Question}
        Fork kind: {fork.Kind}
        Topic key: {NormalizeTopicKey(fork.TopicKey, fork.Kind, fork.Question)}
        Blast radius: {fork.BlastRadius}
        Refiner confidence: {fork.RefinerConfidence}

        Please provide your answer to resolve this ambiguity before the goal can proceed.
        """ + BuildGoalObjectiveSuffix(objective);

    private static string BuildFeasibilityClarificationBody(
        CriterionFeasibilityFinding finding,
        string? objective) => $"""
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
        """ + BuildGoalObjectiveSuffix(objective);

    private static string BuildGoalObjectiveSuffix(string? objective) =>
        string.IsNullOrWhiteSpace(objective) ? string.Empty : $"\n\nGoal objective: {objective}";

    private async Task<RefinedSpec> ApplyFeasibilityResolutionAsync(
        AgentOrchestratorKernel kernel,
        GoalId goalId,
        RefinedSpec spec,
        RefinedSpecOpenQuestion question,
        string answer,
        CancellationToken cancellationToken = default)
    {
        var goal = kernel.GetGoal(goalId);
        var raisedClarificationRound = false;
        var includeObjectiveInNextClarification = !(await _collaboration.ListAsync(goalId.Value, cancellationToken))
            .Any(item =>
                item.Type == CollaborationItemType.Clarification &&
                !CollaborationItemLifecycle.IsTerminal(item.Status) &&
                item.Body.Contains("Goal objective:", StringComparison.OrdinalIgnoreCase));

        if (string.IsNullOrWhiteSpace(question.Criterion) ||
            !AcceptanceCriterionFeasibility.TryParseDisposition(answer, out var disposition))
        {
            var unresolvedFinding = AcceptanceCriterionFeasibility
                .Evaluate([question.Criterion ?? question.Question], fallbackRole: AgentRole.Developer)
                .FirstOrDefault();
            if (unresolvedFinding is not null)
            {
                await _raiseCollaborationItem(
                    CollaborationItemType.Clarification,
                    goalId.Value,
                    BuildClarificationSubject(unresolvedFinding.TopicKey),
                    BuildFeasibilityClarificationBody(
                        unresolvedFinding,
                        includeObjectiveInNextClarification ? goal.Objective : null),
                    question.Id,
                    cancellationToken);
                raisedClarificationRound = true;
                includeObjectiveInNextClarification = false;
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
            if (raisedClarificationRound)
                kernel.RecordGoalClarificationRound(goalId);
            kernel.RecordGoalPolicyDecision(
                goalId,
                $"Rejected feasibility clarification answer for '{question.Id}': expected re-scope, OPERATOR-OWNED, or supply-reproducing-scenario syntax; clarification remains open.");
            return rejected;
        }

        var workerCriteria = spec.AcceptanceCriteria.ToList();
        var operatorOwnedCriteria = spec.OperatorOwnedAcceptanceCriteria.ToList();
        var acceptanceGateOwnedCriteria = spec.AcceptanceGateOwnedAcceptanceCriteria.ToList();
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
            var previousCriterion = workerCriteria[criterionIndex];
            workerCriteria[criterionIndex] = replacement;
            var gateIndex = acceptanceGateOwnedCriteria.FindIndex(item =>
                string.Equals(item.Trim(), previousCriterion.Trim(), StringComparison.OrdinalIgnoreCase));
            if (gateIndex >= 0)
                acceptanceGateOwnedCriteria[gateIndex] = replacement;
        }
        else if (disposition.Kind == FeasibilityDisposition.OperatorOwned && criterionIndex >= 0)
        {
            var operatorCriterion = workerCriteria[criterionIndex];
            if (!operatorOwnedCriteria.Contains(operatorCriterion, StringComparer.OrdinalIgnoreCase))
                operatorOwnedCriteria.Add(operatorCriterion);
            acceptanceGateOwnedCriteria.RemoveAll(item =>
                string.Equals(item.Trim(), operatorCriterion.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        var questions = spec.OpenQuestions
            .Where(candidate => !string.Equals(candidate.Id, question.Id, StringComparison.Ordinal))
            .ToList();
        CriterionFeasibilityFinding? replacementFinding = null;
        if (replacement is not null)
        {
            replacementFinding = AcceptanceCriterionFeasibility.Evaluate([replacement], fallbackRole: AgentRole.Developer).FirstOrDefault();
            if (replacementFinding is not null)
            {
                var questionText = AcceptanceCriterionFeasibility.BuildQuestion(replacementFinding);
                var correlationKey = BuildCorrelationKey(goalId, replacementFinding.TopicKey);
                await _raiseCollaborationItem(
                    CollaborationItemType.Clarification,
                    goalId.Value,
                    BuildClarificationSubject(replacementFinding.TopicKey),
                    BuildFeasibilityClarificationBody(
                        replacementFinding,
                        includeObjectiveInNextClarification ? goal.Objective : null),
                    correlationKey,
                    cancellationToken);
                raisedClarificationRound = true;
                includeObjectiveInNextClarification = false;
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
                await _raiseCollaborationItem(
                    CollaborationItemType.Clarification,
                    goalId.Value,
                    BuildClarificationSubject(ResolveQuestionTopicKey(released)),
                    BuildReleasedClarificationBody(
                        released,
                        includeObjectiveInNextClarification ? goal.Objective : null),
                    released.Id,
                    cancellationToken);
                raisedClarificationRound = true;
                includeObjectiveInNextClarification = false;
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
            OperatorOwnedAcceptanceCriteria = operatorOwnedCriteria,
            AcceptanceGateOwnedAcceptanceCriteria = acceptanceGateOwnedCriteria
        };
        kernel.SetGoalRefinedSpec(goalId, updated);
        if (raisedClarificationRound)
            kernel.RecordGoalClarificationRound(goalId);
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
        var slug = NormalizeTopicKeyForDisplay(topicKey, forkKind, question);
        if (slug.Length <= 35)
            return slug;

        var suffix = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(slug))).ToLowerInvariant()[..8];
        return $"{slug[..26].TrimEnd('-')}-{suffix}";
    }

    private static string NormalizeTopicKeyForDisplay(string? topicKey, string? forkKind, string question)
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
        return slug;
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

    private static void AddOwnedCriteria(
        List<string> target,
        IReadOnlyList<string> acceptanceCriteria,
        IReadOnlyList<string> requestedOwners)
    {
        foreach (var criterion in ResolveOwnedCriteria(acceptanceCriteria, requestedOwners, []))
        {
            if (!target.Contains(criterion, StringComparer.OrdinalIgnoreCase))
                target.Add(criterion);
        }
    }

    private static void AddMarkerDerivedGateOwnedCriteria(
        IReadOnlyList<string> acceptanceCriteria,
        IReadOnlyList<string> operatorOwnedCriteria,
        List<string> acceptanceGateOwnedCriteria)
    {
        foreach (var criterion in acceptanceCriteria)
        {
            if (!AcceptanceCriterionOwnershipMarker.HasAcceptanceGateOwnershipMarker(criterion) ||
                AcceptanceCriterionOwnershipMarker.HasOperatorOwnershipPhrase(criterion) ||
                operatorOwnedCriteria.Contains(criterion, StringComparer.OrdinalIgnoreCase) ||
                acceptanceGateOwnedCriteria.Contains(criterion, StringComparer.Ordinal))
            {
                continue;
            }

            acceptanceGateOwnedCriteria.Add(criterion);
        }
    }

    private static List<string> ResolveDeclaredOwnedCriteria(
        IReadOnlyList<string> acceptanceCriteria,
        IReadOnlyList<SpecRefinementCriterionOwnership> requestedOwners,
        List<string> operatorOwnedCriteria,
        List<string> diagnostics)
    {
        var acceptanceGateOwnedCriteria = new List<string>();
        var claimedOwners = new Dictionary<int, string>();
        var declaredOwners = ClaimDeclaredEvidenceOwners(
            acceptanceCriteria, operatorOwnedCriteria, acceptanceGateOwnedCriteria, claimedOwners);
        var persistedOperatorIndexes = new HashSet<int>();
        foreach (var criterion in operatorOwnedCriteria.ToArray())
        {
            var criterionIndex = FindCriterionIndex(acceptanceCriteria, criterion);
            if (criterionIndex < 0)
                continue;
            claimedOwners[criterionIndex] = "operator";
            persistedOperatorIndexes.Add(criterionIndex);
        }

        var textMarkerIndexes = new HashSet<int>();
        for (var criterionIndex = 0; criterionIndex < acceptanceCriteria.Count; criterionIndex++)
        {
            var criterion = acceptanceCriteria[criterionIndex];
            var marker = AcceptanceCriterionOwnershipMarker.Classify(criterion);
            if (marker.Classification is not (
                    AcceptanceCriterionOwnershipClassification.OperatorOwned or
                    AcceptanceCriterionOwnershipClassification.OperatorOwnedWeakSignal))
            {
                if (marker.TrailingRegion.Length == 0 &&
                    AcceptanceCriterionOwnershipMarker.HasOperatorOwnershipPhrase(criterion))
                {
                    diagnostics.Add(
                        $"Spec refiner owner dropped: operator text marker for declared criterion {criterionIndex + 1} is outside the trailing ownership region");
                }
                continue;
            }

            textMarkerIndexes.Add(criterionIndex);
            claimedOwners[criterionIndex] = "operator";
            if (!operatorOwnedCriteria.Contains(criterion, StringComparer.OrdinalIgnoreCase))
                operatorOwnedCriteria.Add(criterion);

            if (marker.Classification == AcceptanceCriterionOwnershipClassification.OperatorOwnedWeakSignal)
            {
                diagnostics.Add(
                    $"Spec refiner owner defaulted: declared criterion {criterionIndex + 1} is real-world-dependent with no declared owner, classified operator-owned");
            }
            else if (marker.ConflictingDeclaredOwner is { } conflictingOwner)
            {
                diagnostics.Add(
                    $"Spec refiner owner conflict: text marker claims operator for declared criterion {criterionIndex + 1}, declared text claims {conflictingOwner}");
            }
        }

        var overriddenIndexes = new HashSet<int>();
        foreach (var requested in requestedOwners.Where(item =>
                     item.Owner is "acceptance-gate" or "operator"))
        {
            var criterionIndex = requested.DeclaredIndex is { } declaredIndex
                ? declaredIndex - 1
                : FindCriterionIndex(acceptanceCriteria, requested.Text);
            if (criterionIndex < 0 || criterionIndex >= acceptanceCriteria.Count)
            {
                diagnostics.Add(
                    $"Spec refiner owner dropped: {requested.Owner} for '{PreviewCriterion(requested.Text)}' matched no declared criterion");
                continue;
            }

            if (TryApplyDeclaredOwnerOverride(
                    criterionIndex, requested.Owner, declaredOwners, overriddenIndexes, diagnostics))
                continue;

            if (claimedOwners.TryGetValue(criterionIndex, out var existingOwner))
            {
                if (textMarkerIndexes.Contains(criterionIndex))
                {
                    if (requested.Owner == "operator")
                        continue;

                    diagnostics.Add(
                        $"Spec refiner owner conflict: text marker claims operator for declared criterion {criterionIndex + 1}, refiner claims {requested.Owner}");
                    continue;
                }

                if (persistedOperatorIndexes.Contains(criterionIndex) &&
                    requested.Owner == "operator" &&
                    existingOwner == "operator")
                {
                    continue;
                }

                diagnostics.Add(
                    $"Spec refiner owner conflict: {requested.Owner} for declared criterion {criterionIndex + 1} already owned by {existingOwner}");
                continue;
            }

            claimedOwners[criterionIndex] = requested.Owner;
            var criterion = acceptanceCriteria[criterionIndex];
            if (requested.Owner == "operator")
            {
                if (!operatorOwnedCriteria.Contains(criterion, StringComparer.OrdinalIgnoreCase))
                    operatorOwnedCriteria.Add(criterion);
            }
            else
            {
                acceptanceGateOwnedCriteria.Add(criterion);
            }
        }

        return acceptanceGateOwnedCriteria;
    }

    private static void AddDuplicateDeclaredCriterionDiagnostics(
        IReadOnlyList<string> declaredCriteria,
        List<string> diagnostics)
    {
        var firstDeclaredIndexByText = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var criterionIndex = 0; criterionIndex < declaredCriteria.Count; criterionIndex++)
        {
            var criterion = declaredCriteria[criterionIndex];
            if (firstDeclaredIndexByText.TryGetValue(criterion, out var firstDeclaredIndex))
            {
                diagnostics.Add(
                    $"Spec refiner owner conflict: declared criteria {firstDeclaredIndex + 1} and {criterionIndex + 1} share text '{PreviewCriterion(criterion)}'");
                continue;
            }

            firstDeclaredIndexByText[criterion] = criterionIndex;
        }
    }

    private static int FindCriterionIndex(IReadOnlyList<string> acceptanceCriteria, string requested) =>
        acceptanceCriteria
            .Select((criterion, index) => (criterion, index))
            .FirstOrDefault(item => string.Equals(
                item.criterion.Trim(),
                requested.Trim(),
                StringComparison.OrdinalIgnoreCase),
                (criterion: string.Empty, index: -1))
            .index;

    private static string PreviewCriterion(string criterion) =>
        criterion[..Math.Min(80, criterion.Length)];

    private static List<string> ResolveOwnedCriteria(
        IReadOnlyList<string> acceptanceCriteria,
        IReadOnlyList<string> requestedOwners,
        IReadOnlyList<string> excludedOwners)
    {
        return acceptanceCriteria
            .Where(criterion => requestedOwners.Any(requested =>
                string.Equals(requested.Trim(), criterion.Trim(), StringComparison.OrdinalIgnoreCase)))
            .Where(criterion => !excludedOwners.Any(excluded =>
                string.Equals(excluded.Trim(), criterion.Trim(), StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string BuildResolutionText(CollaborationItem item) =>
        string.IsNullOrWhiteSpace(item.Resolution) ? "dismissed by operator" : item.Resolution!;

    private static string BuildReleasedClarificationBody(
        RefinedSpecOpenQuestion question,
        string? objective) => $"""
        Question: {question.Question}
        Fork kind: {question.ForkKind}
        Topic key: {ResolveQuestionTopicKey(question)}
        Blast radius: {question.BlastRadius ?? "high"}

        The criterion's feasibility disposition is resolved. Please answer this previously withheld measurement/assertion clarification.
        """ + BuildGoalObjectiveSuffix(objective);

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
