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

    public static void ThrowIfAwaitingClarification(OrchestratorWorkspace workspace, Goal goal)
    {
        var items = ListGoalCollaborationItems(workspace, goal);
        if (!GoalRefinementService.HasOpenClarification(items))
        {
            return;
        }

        var questions = items
            .Where(item =>
                item.Type == CollaborationItemType.Clarification &&
                !CollaborationItemLifecycle.IsTerminal(item.Status) &&
                item.CorrelationKey?.StartsWith(GoalRefinementService.CorrelationKeyPrefix, StringComparison.Ordinal) == true)
            .Select(item => item.Subject)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var detail = questions.Length == 0
            ? "Resolve the spec clarification item before dispatching planner work."
            : $"Resolve spec clarification before dispatching planner work: {string.Join("; ", questions)}";
        throw new InvalidOperationException(detail);
    }

    public static bool HasOpenClarification(OrchestratorWorkspace workspace, Goal goal) =>
        GoalRefinementService.HasOpenClarification(ListGoalCollaborationItems(workspace, goal));

    public static IReadOnlyDictionary<GoalId, bool> HasOpenClarificationAll(
        OrchestratorWorkspace workspace,
        IEnumerable<Goal> goals)
    {
        var goalIds = goals.Select(goal => goal.Id).Distinct().ToArray();
        var itemsByGoalId = CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory)
            .ListAsync(null)
            .GetAwaiter()
            .GetResult()
            .Where(item => !string.IsNullOrWhiteSpace(item.GoalId))
            .GroupBy(item => item.GoalId!, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<CollaborationItem>)group.ToArray(),
                StringComparer.Ordinal);

        var results = new Dictionary<GoalId, bool>();
        foreach (var goalId in goalIds)
        {
            results[goalId] = itemsByGoalId.TryGetValue(goalId.Value, out var items) &&
                GoalRefinementService.HasOpenClarification(items);
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
}
