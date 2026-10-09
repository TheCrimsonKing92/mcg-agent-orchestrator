using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record GoalDrainPlan(
    string PolicyName,
    GoalDrainPolicy DrainPolicy,
    bool Apply,
    GoalDrainScheduleDecision Schedule,
    int ReadySubscriptionGoalCount,
    int FirstBatchGoalCount,
    int AcceptanceReadyCount,
    int OperatorGateCount,
    int SafeSupervisorActionCount,
    IReadOnlyList<GoalDrainItem> Items);

internal sealed record GoalDrainItem(
    string GoalPrefix,
    string Objective,
    string Stage,
    bool CanApply,
    bool RequiresOperatorGate,
    string Detail,
    string SuggestedCommand);

internal static class GoalDrainPlanner
{
    public static GoalDrainPlan Build(
        AgentOrchestratorKernel kernel,
        IReadOnlyList<AgentDefinition> agents,
        WorkerProfileCatalog profiles,
        OrchestratorWorkspace workspace,
        AutonomyPolicy policy,
        bool apply,
        bool costRiskConfirmed = false,
        GoalDrainPolicy? drainPolicy = null,
        DateTimeOffset? now = null)
    {
        drainPolicy ??= GoalDrainPolicy.Default;
        var schedule = drainPolicy.EvaluateSchedule(now ?? DateTimeOffset.Now);
        var items = new List<GoalDrainItem>();
        var crossGoalStart = CrossGoalSubscriptionStartPlanner.Build(kernel, agents, profiles, costRiskConfirmed);
        var firstBatchIds = crossGoalStart.FirstBatchCandidates
            .Where(_ => schedule.IsOpen)
            .Where(candidate => AllowsCandidate(kernel, candidate, agents, drainPolicy, out _))
            .Take(drainPolicy.MaxSubscriptionStartsPerDrain)
            .Select(candidate => candidate.GoalId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var acceptance = AcceptanceQueuePlanner.Build(kernel, workspace.ExecutionDirectory, policy);

        foreach (var goal in kernel.Goals.OrderBy(goal => goal.Timeline.FirstOrDefault()?.OccurredAt ?? DateTimeOffset.MaxValue))
        {
            var goalPrefix = goal.Id.Value[..8];
            var supervisor = GoalSupervisor.Build(kernel, goal, agents, workspace.ExecutionDirectory, workspace.IntegrationBranch, policy);
            foreach (var proposal in supervisor.Proposals.Where(proposal => proposal.CanApply && !proposal.RequiresOperatorGate))
            {
                items.Add(new GoalDrainItem(
                    goalPrefix,
                    goal.Objective,
                    "supervisor",
                    CanApply: true,
                    RequiresOperatorGate: false,
                    proposal.Reason,
                    proposal.SuggestedCommand));
            }

            foreach (var proposal in supervisor.Proposals.Where(proposal => proposal.RequiresOperatorGate))
            {
                items.Add(new GoalDrainItem(
                    goalPrefix,
                    goal.Objective,
                    "operator-gate",
                    CanApply: false,
                    RequiresOperatorGate: true,
                    proposal.Reason,
                    proposal.SuggestedCommand));
            }
        }

        foreach (var candidate in crossGoalStart.Candidates)
        {
            var inFirstBatch = firstBatchIds.Contains(candidate.GoalId);
            var allowedByDrainPolicy = AllowsCandidate(kernel, candidate, agents, drainPolicy, out var drainPolicyReason);
            var scheduleReason = schedule.IsOpen ? null : schedule.Detail;
            var policyReason = string.Join("; ", new[] { drainPolicyReason, scheduleReason }.Where(reason => !string.IsNullOrWhiteSpace(reason)));
            items.Add(new GoalDrainItem(
                candidate.GoalPrefix,
                candidate.Objective,
                "subscription-start",
                CanApply: inFirstBatch && allowedByDrainPolicy && schedule.IsOpen && policy.Allows(AutonomyAction.DispatchStart) && (!candidate.RequiresCostConfirmation || costRiskConfirmed),
                RequiresOperatorGate: !allowedByDrainPolicy || (candidate.RequiresCostConfirmation && !costRiskConfirmed),
                string.IsNullOrWhiteSpace(policyReason) ? candidate.Detail : $"{candidate.Detail}; drain policy: {policyReason}",
                inFirstBatch
                    ? $"start-subscription-ready-goals --confirm-batch-start --autonomy {policy.Name}"
                    : allowedByDrainPolicy && schedule.IsOpen ? "wait for earlier parallel-safe batch or drain policy capacity" : "blocked by drain policy"));
        }

        foreach (var item in acceptance.Items.Where(item => item.Disposition != AcceptanceQueueDisposition.Blocked))
        {
            items.Add(new GoalDrainItem(
                item.GoalPrefix,
                item.Objective,
                item.Disposition == AcceptanceQueueDisposition.Ready ? "acceptance-gate" : "operator-gate",
                CanApply: false,
                RequiresOperatorGate: true,
                item.Reason,
                item.SuggestedCommand));
        }

        return new GoalDrainPlan(
            policy.Name,
            drainPolicy,
            apply,
            schedule,
            crossGoalStart.Candidates.Count,
            firstBatchIds.Count,
            acceptance.ReadyCount,
            items.Count(item => item.RequiresOperatorGate),
            items.Count(item => item.Stage == "supervisor" && item.CanApply),
            items);
    }

    private static bool AllowsCandidate(
        AgentOrchestratorKernel kernel,
        CrossGoalSubscriptionStartCandidate candidate,
        IReadOnlyList<AgentDefinition> agents,
        GoalDrainPolicy policy,
        out string? reason)
    {
        reason = null;
        if (policy.MaxSubscriptionStartsPerDrain <= 0)
        {
            reason = "maxSubscriptionStartsPerDrain=0";
            return false;
        }

        var goal = kernel.Goals.FirstOrDefault(goal => goal.Id.Value.Equals(candidate.GoalId, StringComparison.OrdinalIgnoreCase));
        if (goal is null)
        {
            reason = "goal no longer exists";
            return false;
        }

        var candidateTaskIds = candidate.TaskIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var tasks = goal.Tasks.Where(task => candidateTaskIds.Contains(task.Id.Value)).ToArray();
        var blockedRole = tasks
            .Select(task => task.RequiredRole.ToString())
            .FirstOrDefault(role => !policy.AllowsRole(role));
        if (blockedRole is not null)
        {
            reason = $"role {blockedRole} is not allowed";
            return false;
        }

        var blockedProvider = tasks
            .Select(task => task.AssignedAgentId is null ? null : agents.FirstOrDefault(agent => agent.Id == task.AssignedAgentId)?.Model.ProviderName)
            .FirstOrDefault(provider => !policy.AllowsProvider(provider));
        if (blockedProvider is not null)
        {
            reason = $"provider {blockedProvider} is not allowed";
            return false;
        }

        return true;
    }
}
