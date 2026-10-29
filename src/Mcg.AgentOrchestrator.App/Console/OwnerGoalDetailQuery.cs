using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Mcg.AgentOrchestrator.App.Orchestration;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed class OwnerGoalDetailQuery(IOrchestratorStateQueries state, IGoalEventTail tail,
    Func<OwnerConsoleGateProgress?>? gate = null, OwnerGoalChangeSummaryQuery? summary = null,
    OwnerConsoleEvidenceAttemptProgressReader? evidence = null)
{
    private readonly OwnerGoalChangeSummaryQuery _summary = summary ?? new();

    internal async Task<OwnerGoalChangeSummary?> LoadSummaryAsync(string goalId, CancellationToken token)
    {
        var selected = await state.LoadGoalsAsync([new GoalId(goalId)], token);
        var goal = selected.Goals.SingleOrDefault(item => item.Id.Value == goalId);
        return goal is null ? null : await _summary.LoadAsync(goal, token);
    }

    internal Task<string?> ResolveGoalIdAsync(string idOrPrefix, OwnerConsoleCapturedOutput output,
        CancellationToken cancellationToken) =>
        OwnerConsoleGoalDetail.ResolveAsync(state, idOrPrefix, output, cancellationToken);

    internal async Task<OwnerGoalDetailSnapshot?> LoadAsync(string goalId, CancellationToken cancellationToken)
    {
        var kernel = await state.LoadGoalsAsync([new GoalId(goalId)], cancellationToken);
        var goal = kernel.Goals.SingleOrDefault(goal => goal.Id.Value == goalId);
        if (goal is null) return null;
        int? position = null;
        if (goal.Status == GoalStatus.Verified && goal.CurrentHold is null)
        {
            var queued = await state.ListAcceptanceQueueFactsAsync(cancellationToken);
            var ordered = queued.OrderByDescending(item => item.Priority).ThenBy(item => item.VerifiedAt)
                .ThenBy(item => item.CreatedAt).ThenBy(item => item.GoalId, StringComparer.Ordinal).ToList();
            var index = ordered.FindIndex(item => item.GoalId == goalId);
            if (index >= 0) position = index + 1;
        }
        var roles = goal.Tasks.ToDictionary(task => task.Id.Value, task => task.RequiredRole);
        var events = tail.ReadLast(goalId, 100)
            .Select(line => OwnerGoalLifecycleEvent.TryParse(line, goalId, out var item) ? item : null)
            .Where(item => item is not null).Select(item => OwnerGoalLifecycleEvent.WithRole(item!, roles)).ToArray();
        return Observe(new OwnerGoalDetailSnapshot(goal, position, events,
            Changes: await _summary.LoadAsync(goal, cancellationToken)), gate?.Invoke());
    }

    internal OwnerGoalDetailSnapshot Observe(OwnerGoalDetailSnapshot detail, OwnerConsoleGateProgress? observed) =>
        detail.WithGate(observed) with { Evidence = evidence?.Read(detail.Goal.Id.Value, observed) };
}
