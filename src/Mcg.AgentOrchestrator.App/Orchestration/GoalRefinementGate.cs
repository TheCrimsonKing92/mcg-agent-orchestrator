using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record GoalRefinementGateResult(
    RefinementOutcome Outcome,
    bool RanRefinement,
    RefinedSpec Spec)
{
    public bool AwaitingClarification => Outcome == RefinementOutcome.AwaitingClarification;
}

internal static class GoalRefinementGate
{
    public static GoalRefinementGateResult EnsureRefined(
        AgentOrchestratorKernel kernel,
        OrchestratorWorkspace workspace,
        IModelProviderRegistry providers,
        Goal goal,
        ConductorAutonomyPolicy? policy = null,
        WorkerProfileCatalog? workerProfiles = null,
        IGoalLifecycleEventWriter? eventWriter = null)
    {
        if (goal.RefinedSpec is { } existing)
        {
            // Pick up any operator answers submitted since refinement: resolved clarification items in
            // the store are written into the spec's open questions here (the listener only resolves the
            // store item), so an answered goal clears AwaitingClarification and planning resumes with the
            // operator's decisions recorded in the spec.
            if (existing.HasOpenQuestions)
            {
                existing = CreateService(workspace, providers, workerProfiles)
                    .SyncAnsweredClarifications(kernel, goal.Id) ?? existing;
            }

            var outcome = existing.HasOpenQuestions ? RefinementOutcome.AwaitingClarification : RefinementOutcome.AutoRefined;
            if (outcome == RefinementOutcome.AwaitingClarification)
                eventWriter?.AppendClarificationNeeded(goal.Id, "spec");
            return new GoalRefinementGateResult(outcome, RanRefinement: false, existing);
        }

        var service = CreateService(workspace, providers, workerProfiles);
        var result = service.RefineAsync(kernel, goal.Id, policy ?? ConductorAutonomyPolicy.Conservative).GetAwaiter().GetResult();
        kernel.RecordGoalPolicyDecision(
            goal.Id,
            result.Outcome == RefinementOutcome.AwaitingClarification
                ? "Goal refinement attached a RefinedSpec and raised clarification item(s); planning is held until they are resolved."
                : "Goal refinement attached a RefinedSpec before planning.");
        if (result.Outcome == RefinementOutcome.AwaitingClarification)
            eventWriter?.AppendClarificationNeeded(goal.Id, "spec");
        return new GoalRefinementGateResult(result.Outcome, RanRefinement: true, result.Spec);
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

        var questions = openClarifications
            .Select(item => item.Subject)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        reason = questions.Length == 0
            ? "Resolve the spec clarification item before dispatching planner work."
            : $"Resolve spec clarification before dispatching planner work: {string.Join("; ", questions)}";
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

    private static GoalRefinementService CreateService(
        OrchestratorWorkspace workspace,
        IModelProviderRegistry providers,
        WorkerProfileCatalog? workerProfiles) =>
        new(
            providers,
            ModelFunctionCatalogStore.Load(workspace.ModelFunctionCatalogPath),
            CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory),
            new SpecRefinerPrecedentStore(workspace.SpecRefinerPrecedentsPath),
            workerProfiles ?? WorkerProfileStore.Load(workspace.WorkerProfilePath));

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
