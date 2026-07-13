using Mcg.AgentOrchestrator.App.SubscriptionPlanning;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record HistoricalDogfoodEvaluationReport(
    GoalId GoalId,
    string GoalPrefix,
    string Objective,
    GoalStatus Status,
    int Score,
    string Grade,
    IReadOnlyList<HistoricalDogfoodMetric> Metrics,
    IReadOnlyList<string> Recommendations);

internal sealed record HistoricalDogfoodMetric(
    string Name,
    int Value,
    int Penalty,
    string Detail);

internal static class HistoricalDogfoodEvaluationHarness
{
    public static HistoricalDogfoodEvaluationReport Evaluate(
        AgentOrchestratorKernel kernel,
        Goal goal,
        IReadOnlyList<AgentDefinition> agents,
        WorkerProfileCatalog profiles,
        OrchestratorWorkspace workspace)
    {
        var monitor = kernel.BuildMonitor(goal.Id);
        var inbox = OperatorInbox.Build(kernel, agents, profiles, workspace, goal.Id.Value[..8], includeAcknowledged: false);
        var subscription = SubscriptionPlanBuilder.Build(goal, agents, profiles);
        var acceptance = GoalAcceptanceStatusProjector.Build(kernel, goal, workspace.ExecutionDirectory);
        var recovery = GoalRecoveryPlanner.Build(kernel, goal, workspace.ExecutionDirectory);

        var metrics = new List<HistoricalDogfoodMetric>
        {
            BuildMetric(
                "operator-interventions",
                inbox.OpenCount + monitor.AttentionItems.Count,
                4,
                $"inbox={inbox.OpenCount}, monitorAttention={monitor.AttentionItems.Count}"),
            BuildMetric(
                "provider-deferrals",
                subscription.CapacitySchedule.DeferredCount,
                6,
                subscription.CapacitySchedule.Recommendation),
            BuildMetric(
                "provider-reroute-options",
                subscription.CapacitySchedule.Actions.Count(action => action.Alternatives.Count > 0),
                -2,
                "Alternate routes available reduce unattended blockage risk."),
            BuildMetric(
                "prompt-cost-risk",
                subscription.CapacitySchedule.HasCostRisk ? 1 : 0,
                10,
                subscription.ReadyStartCostRecommendation ?? "No large paid prompt risk recorded."),
            BuildMetric(
                "prompt-over-budget",
                subscription.Items.Count(item => item.TaskBriefHeadroom is < 0),
                8,
                "Task briefs with negative prompt headroom are likely to trip large-prompt guards."),
            BuildMetric(
                "false-completion-risk",
                CountFalseCompletionRisk(goal),
                10,
                "Completed tasks without execution, dispatch, process, or verification proof need operator review."),
            BuildMetric(
                "verification-gaps",
                goal.Tasks.Count(task => task.Status == WorkTaskStatus.Completed && task.LastVerification is null),
                8,
                "Completed tasks missing verification block acceptance readiness."),
            BuildMetric(
                "acceptance-blockers",
                acceptance.Blockers.Count,
                6,
                acceptance.IsAccepted ? "Accepted." : "Acceptance still has blockers or is not ready."),
            BuildMetric(
                "cleanup-risk",
                CountCleanupRisk(recovery),
                6,
                "Dirty worktrees, branch diffs, or orphaned build leases need deterministic cleanup."),
            BuildMetric(
                "failed-or-stale-work",
                recovery.TaskFindings.Count,
                5,
                "Recovery findings approximate replayed stuck, failed, or stale worker outcomes.")
        };

        var penalty = metrics.Sum(metric => metric.Penalty);
        var score = Math.Clamp(100 - penalty, 0, 100);
        return new HistoricalDogfoodEvaluationReport(
            goal.Id,
            goal.Id.Value[..8],
            goal.Objective,
            goal.Status,
            score,
            Grade(score),
            metrics,
            BuildRecommendations(metrics, subscription.CapacitySchedule, recovery).ToArray());
    }

    private static HistoricalDogfoodMetric BuildMetric(
        string name,
        int value,
        int penaltyPerUnit,
        string detail)
    {
        var penalty = penaltyPerUnit < 0
            ? Math.Max(penaltyPerUnit * value, -10)
            : Math.Min(value * penaltyPerUnit, 30);
        return new HistoricalDogfoodMetric(name, value, penalty, detail);
    }

    private static int CountFalseCompletionRisk(Goal goal)
    {
        return goal.Tasks.Count(task =>
            task.Status == WorkTaskStatus.Completed &&
            task.LastExecution is null &&
            task.LastDispatch is null &&
            task.LastProcess is null &&
            task.LastVerification is null);
    }

    private static int CountCleanupRisk(GoalRecoveryReport recovery)
    {
        var risk = 0;
        if (recovery.WorktreeDirty == true)
        {
            risk++;
        }

        if (recovery.HasBranchDiff)
        {
            risk++;
        }

        if (recovery.BuildLease.CanCleanup)
        {
            risk++;
        }

        return risk;
    }

    private static IEnumerable<string> BuildRecommendations(
        IReadOnlyList<HistoricalDogfoodMetric> metrics,
        ProviderCapacitySchedule capacity,
        GoalRecoveryReport recovery)
    {
        foreach (var metric in metrics.Where(metric => metric.Value > 0 && metric.Penalty > 0).OrderByDescending(metric => metric.Penalty).Take(5))
        {
            yield return metric.Name switch
            {
                "operator-interventions" => "Inspect operator-inbox and monitor attention before starting more automation.",
                "provider-deferrals" => capacity.Recommendation,
                "prompt-cost-risk" or "prompt-over-budget" => "Regenerate context packages or split task briefs before subscription dispatch.",
                "false-completion-risk" or "verification-gaps" => "Run verification-needed checks and reject manual-only completions without evidence.",
                "acceptance-blockers" => "Use acceptance-queue or acceptance evidence before merge.",
                "cleanup-risk" => "Run goal-recovery, retention-plan, or abandon-goal before deleting artifacts.",
                "failed-or-stale-work" => recovery.RecommendedActions.FirstOrDefault() ?? "Run goal-recovery.",
                _ => $"Inspect metric {metric.Name}."
            };
        }
    }

    private static string Grade(int score) => score switch
    {
        >= 90 => "ready",
        >= 70 => "review",
        >= 50 => "risky",
        _ => "blocked"
    };
}
