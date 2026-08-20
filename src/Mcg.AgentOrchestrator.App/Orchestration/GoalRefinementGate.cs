using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal enum GoalRefinementReadiness
{
    Ready,
    Pending,
    AwaitingClarification,
    RepairRequired,
    Failed,
    Quarantined
}

internal sealed record GoalRefinementClarificationFacts(
    IReadOnlyList<CollaborationItem> Items);

internal sealed record GoalRefinementReadinessResult(
    GoalRefinementReadiness Readiness,
    string Detail);

internal static class GoalRefinementGate
{
    public static GoalRefinementReadinessResult Evaluate(
        Goal goal,
        OrchestratorStateOutboxState? outboxState,
        GoalRefinementClarificationFacts clarificationFacts)
    {
        if (outboxState is { } typedState &&
            (!typedState.Message.Id.Equals(GoalRefinementWorkCoordinator.MessageId(goal.Id), StringComparison.Ordinal) ||
             !typedState.Message.Kind.Equals(GoalRefinementWorkCoordinator.OutboxKind, StringComparison.Ordinal)))
        {
            return new GoalRefinementReadinessResult(
                GoalRefinementReadiness.Quarantined,
                "outbox-identity-mismatch");
        }

        if (outboxState?.Status == OrchestratorStateOutboxStatus.Quarantined)
            return new GoalRefinementReadinessResult(GoalRefinementReadiness.Quarantined, outboxState.Detail ?? "quarantined");
        if (outboxState?.Status == OrchestratorStateOutboxStatus.Failed)
            return new GoalRefinementReadinessResult(GoalRefinementReadiness.Failed, outboxState.Detail ?? "last-executor-failed");

        if (goal.RefinedSpec is null &&
            (!GoalRefinementWorkCoordinator.HasPendingWork(goal) || outboxState is null))
        {
            return new GoalRefinementReadinessResult(
                GoalRefinementReadiness.RepairRequired,
                outboxState is null ? "missing-durable-work" : "missing-pending-marker");
        }

        if (outboxState?.Status is OrchestratorStateOutboxStatus.Pending or OrchestratorStateOutboxStatus.Processing)
            return new GoalRefinementReadinessResult(GoalRefinementReadiness.Pending, outboxState.Status.ToString().ToLowerInvariant());

        if (goal.RefinedSpec is null)
            return new GoalRefinementReadinessResult(GoalRefinementReadiness.RepairRequired, "missing-durable-work");

        var resolvedItems = clarificationFacts.Items
            .Where(item =>
                item.Type == CollaborationItemType.Clarification &&
                CollaborationItemLifecycle.IsTerminal(item.Status) &&
                !string.IsNullOrWhiteSpace(item.CorrelationKey))
            .ToArray();
        if (goal.RefinedSpec.OpenQuestions.Any(question =>
                HasResolvedAnswerPendingSync(question, resolvedItems)))
        {
            return new GoalRefinementReadinessResult(
                GoalRefinementReadiness.RepairRequired,
                "resolved-answers-pending-sync");
        }

        if (goal.RefinedSpec.HasOpenQuestions)
        {
            return new GoalRefinementReadinessResult(
                GoalRefinementReadiness.AwaitingClarification,
                BuildAwaitingClarificationReason(goal, clarificationFacts.Items));
        }

        return new GoalRefinementReadinessResult(GoalRefinementReadiness.Ready, "ready");
    }

    private static bool HasResolvedAnswerPendingSync(
        RefinedSpecOpenQuestion question,
        IReadOnlyList<CollaborationItem> resolvedItems)
    {
        var exactMatches = resolvedItems
            .Where(item => string.Equals(item.CorrelationKey, question.Id, StringComparison.Ordinal))
            .ToArray();
        if (exactMatches.Length == 0)
            return false;

        var selected = string.Equals(
            question.ForkKind,
            AcceptanceCriterionFeasibility.ForkKind,
            StringComparison.OrdinalIgnoreCase)
            ? exactMatches[^1]
            : exactMatches[0];
        var resolved = ToResolvedClarification(selected);
        return !string.Equals(question.Answer, resolved.Resolution, StringComparison.Ordinal);
    }

    internal static string BuildPolicyReceipt(RefinementResult result)
    {
        var metadata = result.Invocation;
        var prefix = $"spec_refinement outcome={result.Disposition.ToString().ToLowerInvariant()}";
        var failure = result.Failure is null
            ? string.Empty
            : $" reason_code={Token(result.Failure.ReasonCode)} detail={Token(result.Failure.Detail, 240)}";
        var clarification = result.Outcome == RefinementOutcome.AwaitingClarification
            ? " clarification=pending"
            : " clarification=none";
        return prefix + failure + clarification +
            $" prompt_chars={metadata.PromptCharacterCount}" +
            $" prompt_bytes={metadata.PromptUtf8ByteCount}" +
            $" provider_kind={Token(metadata.ProviderKind)}" +
            $" worker_profile={Token(metadata.WorkerProfile)}" +
            $" model={Token(metadata.Model)}" +
            $" reasoning_effort={Token(metadata.ReasoningEffort)}";
    }

    private static string Token(string value, int maxLength = 100)
    {
        var bounded = value.Length <= maxLength ? value : value[..maxLength];
        return string.Concat(bounded.Select(character => char.IsWhiteSpace(character) ? '_' : character));
    }

    public static void ThrowIfAwaitingClarification(
        OrchestratorWorkspace workspace,
        Goal goal,
        IGoalLifecycleEventWriter? eventWriter = null)
    {
        if (TryBuildAwaitingClarificationEscalationReason(workspace, goal, eventWriter, out var reason))
        {
            throw new InvalidOperationException(reason);
        }
    }

    public static bool TryBuildAwaitingClarificationEscalationReason(
        OrchestratorWorkspace workspace,
        Goal goal,
        IGoalLifecycleEventWriter? eventWriter,
        out string reason)
    {
        var items = ListGoalCollaborationItems(workspace, goal);
        if (!GoalRefinementService.HasOpenClarification(items))
        {
            reason = string.Empty;
            return false;
        }

        var openClarifications = items
            .Where(item =>
                item.Type == CollaborationItemType.Clarification &&
                !CollaborationItemLifecycle.IsTerminal(item.Status) &&
                item.CorrelationKey?.StartsWith(GoalRefinementService.CorrelationKeyPrefix, StringComparison.Ordinal) == true)
            .ToArray();
        var staleTopicKeys = FindStaleClarificationTopicKeys(openClarifications, items);
        if (staleTopicKeys.Count > 0)
        {
            var recoveryCommand = $"attention dismiss {goal.Id.Value[..8]}";
            eventWriter?.AppendStaleClarificationDetected(goal.Id, staleTopicKeys, recoveryCommand);
            reason =
                $"Stale spec clarification detected for goal {goal.Id.Value[..8]}: {string.Join(", ", staleTopicKeys)}. " +
                $"Recovery: run `{recoveryCommand}` to clear stale clarification attention, then retry dispatch.";
            return true;
        }

        reason = openClarifications.Length == 0
            ? "Resolve the spec clarification item before dispatching planner work."
            : $"Resolve spec clarification before dispatching planner work: {openClarifications.Length} pending for goal {goal.Id.Value[..8]}. " +
              $"Run `attention show {goal.Id.Value[..8]}` to read and answer them.";
        return true;
    }

    public static bool HasOpenClarification(OrchestratorWorkspace workspace, Goal goal) =>
        GoalRefinementService.HasOpenClarification(ListGoalCollaborationItems(workspace, goal));

    public static IReadOnlySet<GoalId> OpenClarificationGoalIds(
        OrchestratorWorkspace workspace,
        IEnumerable<GoalId> goalIds)
    {
        var scopedGoalIds = goalIds.Distinct().ToArray();
        var itemsByGoalId = CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory)
            .ListForGoalIdsAsync(scopedGoalIds.Select(goalId => goalId.Value))
            .GetAwaiter()
            .GetResult()
            .Where(item => !string.IsNullOrWhiteSpace(item.GoalId))
            .GroupBy(item => item.GoalId!, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<CollaborationItem>)group.ToArray(),
                StringComparer.Ordinal);

        var results = new HashSet<GoalId>();
        foreach (var goalId in scopedGoalIds)
        {
            if (itemsByGoalId.TryGetValue(goalId.Value, out var items) &&
                GoalRefinementService.HasOpenClarification(items))
            {
                results.Add(goalId);
            }
        }

        return results;
    }

    private static IReadOnlyList<CollaborationItem> ListGoalCollaborationItems(
        OrchestratorWorkspace workspace,
        Goal goal) =>
        CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory)
            .ListAsync(goal.Id.Value)
            .GetAwaiter()
            .GetResult();

    private static IReadOnlyList<string> FindStaleClarificationTopicKeys(
        IReadOnlyList<CollaborationItem> openClarifications,
        IReadOnlyList<CollaborationItem> allItems)
    {
        var resolved = allItems
            .Where(item =>
                item.Type == CollaborationItemType.Clarification &&
                CollaborationItemLifecycle.IsTerminal(item.Status) &&
                item.CorrelationKey?.StartsWith(GoalRefinementService.CorrelationKeyPrefix, StringComparison.Ordinal) == true)
            .Select(ToResolvedClarification)
            .ToArray();
        if (resolved.Length == 0)
            return [];

        return openClarifications
            .Select(ToOpenQuestion)
            .Where(question => GoalRefinementService.MatchesResolvedIdentity(question, resolved))
            .Select(GoalRefinementService.ResolveQuestionTopicKey)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string BuildAwaitingClarificationReason(
        Goal goal,
        IReadOnlyList<CollaborationItem> items)
    {
        var openClarifications = items
            .Where(item =>
                item.Type == CollaborationItemType.Clarification &&
                !CollaborationItemLifecycle.IsTerminal(item.Status) &&
                item.CorrelationKey?.StartsWith(GoalRefinementService.CorrelationKeyPrefix, StringComparison.Ordinal) == true)
            .ToArray();
        var staleTopicKeys = FindStaleClarificationTopicKeys(openClarifications, items);
        if (staleTopicKeys.Count > 0)
        {
            var recoveryCommand = $"attention dismiss {goal.Id.Value[..8]}";
            return
                $"Stale spec clarification detected for goal {goal.Id.Value[..8]}: {string.Join(", ", staleTopicKeys)}. " +
                $"Recovery: run `{recoveryCommand}` to clear stale clarification attention, then retry dispatch.";
        }

        return openClarifications.Length == 0
            ? "Resolve the spec clarification item before dispatching planner work."
            : $"Resolve spec clarification before dispatching planner work: {openClarifications.Length} pending for goal {goal.Id.Value[..8]}. " +
              $"Run `attention show {goal.Id.Value[..8]}` to read and answer them.";
    }

    private static ResolvedSpecClarification ToResolvedClarification(CollaborationItem item)
    {
        var question = GoalRefinementService.ExtractQuestion(item);
        var forkKind = GoalRefinementService.ExtractForkKindFromBody(item.Body) ??
            GoalRefinementService.ExtractTopicKey(item.CorrelationKey!) ??
            "other";
        var topicKey = GoalRefinementService.NormalizeTopicKey(
            GoalRefinementService.ExtractTopicKey(item.CorrelationKey!),
            forkKind,
            question);
        var resolution = string.IsNullOrWhiteSpace(item.Resolution) ? "dismissed by operator" : item.Resolution!;
        return new ResolvedSpecClarification(
            topicKey,
            GoalRefinementService.BuildNormalizedQuestionKey(forkKind, question),
            question,
            resolution,
            resolution.Contains("dismiss", StringComparison.OrdinalIgnoreCase));
    }

    private static RefinedSpecOpenQuestion ToOpenQuestion(CollaborationItem item)
    {
        var question = GoalRefinementService.ExtractQuestion(item);
        var forkKind = GoalRefinementService.ExtractForkKindFromBody(item.Body) ??
            GoalRefinementService.ExtractTopicKey(item.CorrelationKey!) ??
            "other";
        var topicKey = GoalRefinementService.NormalizeTopicKey(
            GoalRefinementService.ExtractTopicKey(item.CorrelationKey!),
            forkKind,
            question);
        return new RefinedSpecOpenQuestion(
            item.CorrelationKey!,
            question,
            forkKind,
            "Open",
            TopicKey: topicKey,
            NormalizedQuestionKey: GoalRefinementService.BuildNormalizedQuestionKey(forkKind, question));
    }
}
