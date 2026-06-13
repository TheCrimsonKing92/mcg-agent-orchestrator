using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal enum GoalSupervisorProposalKind
{
    RefreshRunningProcess,
    ReDelegateAfterRecoverableFailure,
    DeferUntilRetryAfter,
    ParkForOperator,
    VerifyMissingEvidence,
    AcceptanceGate,
    Monitor
}

internal sealed record GoalSupervisorPlan(
    GoalId GoalId,
    string GoalPrefix,
    string PolicyName,
    bool ApplySafe,
    IReadOnlyList<GoalSupervisorProposal> Proposals);

internal sealed record GoalSupervisorProposal(
    GoalSupervisorProposalKind Kind,
    int? TaskNumber,
    TaskId? TaskId,
    AutonomyAction? PolicyAction,
    bool PolicyAllows,
    bool CanApply,
    bool RequiresOperatorGate,
    string Reason,
    string SuggestedCommand);

internal sealed record GoalSupervisorApplyResult(
    GoalSupervisorPlan Plan,
    IReadOnlyList<string> AppliedActions);

internal static class GoalSupervisor
{
    public static GoalSupervisorPlan Build(
        AgentOrchestratorKernel kernel,
        Goal goal,
        IReadOnlyList<AgentDefinition> agents,
        string executionDirectory,
        AutonomyPolicy policy,
        bool applySafe = false,
        DateTimeOffset? now = null)
    {
        var recovery = GoalRecoveryPlanner.Build(kernel, goal, executionDirectory);
        var proposals = new List<GoalSupervisorProposal>();
        var observedAt = now ?? DateTimeOffset.UtcNow;

        foreach (var finding in recovery.TaskFindings)
        {
            var task = goal.Tasks.First(task => task.Id == finding.TaskId);
            AddTaskProposal(proposals, goal, task, finding, agents, policy, observedAt);
        }

        if (recovery.WorktreeDirty == true)
        {
            proposals.Add(new GoalSupervisorProposal(
                GoalSupervisorProposalKind.ParkForOperator,
                null,
                null,
                null,
                PolicyAllows: false,
                CanApply: false,
                RequiresOperatorGate: true,
                "Goal worktree is dirty; inspect the diff before starting more file work.",
                "inspect worktree dirty state before dispatching more file work"));
        }

        if (goal.Status == GoalStatus.Completed && recovery.HasBranchDiff)
        {
            var allows = policy.Allows(AutonomyAction.Acceptance);
            proposals.Add(new GoalSupervisorProposal(
                GoalSupervisorProposalKind.AcceptanceGate,
                null,
                null,
                AutonomyAction.Acceptance,
                allows,
                CanApply: false,
                RequiresOperatorGate: true,
                allows
                    ? "Goal has branch diff ready for acceptance, but supervisor leaves merge as an operator gate."
                    : $"Goal has branch diff ready for acceptance, but policy {policy.Name} blocks acceptance.",
                $"acceptance {goal.Id.Value[..8]} --autonomy {AutonomyPolicy.SupervisedAuto.Name}"));
        }

        if (proposals.Count == 0)
        {
            proposals.Add(new GoalSupervisorProposal(
                GoalSupervisorProposalKind.Monitor,
                null,
                null,
                AutonomyAction.Refresh,
                policy.Allows(AutonomyAction.Refresh),
                CanApply: false,
                RequiresOperatorGate: false,
                "No safe supervisor action is currently available.",
                "monitor"));
        }

        return new GoalSupervisorPlan(goal.Id, goal.Id.Value[..8], policy.Name, applySafe, proposals);
    }

    public static GoalSupervisorApplyResult ApplySafe(
        AgentOrchestratorKernel kernel,
        Goal goal,
        IReadOnlyList<AgentDefinition> agents,
        OrchestratorWorkspace workspace,
        AutonomyPolicy policy)
    {
        var plan = Build(kernel, goal, agents, workspace.ExecutionDirectory, policy, applySafe: true);
        var applied = new List<string>();
        var appliedKeys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var proposal in plan.Proposals.Where(proposal => proposal.CanApply && !proposal.RequiresOperatorGate))
        {
            var key = $"{proposal.Kind}:{proposal.TaskId?.Value ?? "goal"}";
            if (!appliedKeys.Add(key))
            {
                continue;
            }

            if (proposal.Kind == GoalSupervisorProposalKind.RefreshRunningProcess && proposal.TaskId is { } refreshTaskId)
            {
                AutonomyPolicyEvidence.Record(kernel, goal, policy, AutonomyAction.Refresh, "supervisor refresh", allowed: true);
                new BackgroundDispatchRunner().RefreshLatestProcess(kernel, goal.Id, refreshTaskId);
                applied.Add(proposal.SuggestedCommand);
                continue;
            }

            if (proposal.Kind == GoalSupervisorProposalKind.ReDelegateAfterRecoverableFailure && proposal.TaskId is { } redelegateTaskId)
            {
                AutonomyPolicyEvidence.Record(kernel, goal, policy, AutonomyAction.ProviderFailover, "supervisor re-delegate", allowed: true);
                kernel.RedelegateTask(goal.Id, redelegateTaskId, agents);
                applied.Add(proposal.SuggestedCommand);
            }
        }

        var updatedPlan = Build(kernel, goal, agents, workspace.ExecutionDirectory, policy, applySafe: true);
        return new GoalSupervisorApplyResult(updatedPlan, applied);
    }

    private static void AddTaskProposal(
        List<GoalSupervisorProposal> proposals,
        Goal goal,
        TaskSpec task,
        GoalRecoveryTaskFinding finding,
        IReadOnlyList<AgentDefinition> agents,
        AutonomyPolicy policy,
        DateTimeOffset now)
    {
        if (task.LastProcess is { IsRunning: true })
        {
            var allows = policy.Allows(AutonomyAction.Refresh);
            proposals.Add(new GoalSupervisorProposal(
                GoalSupervisorProposalKind.RefreshRunningProcess,
                finding.TaskNumber,
                task.Id,
                AutonomyAction.Refresh,
                allows,
                CanApply: allows,
                RequiresOperatorGate: false,
                "Recorded background worker is running; refresh can reconcile durable state without starting new work.",
                $"refresh-dispatch {finding.TaskNumber} --autonomy {policy.Name}"));
            return;
        }

        if (DispatchFailureClassifier.IsSubscriptionRetryDeferred(task, now, out var retryAfter))
        {
            proposals.Add(new GoalSupervisorProposal(
                GoalSupervisorProposalKind.DeferUntilRetryAfter,
                finding.TaskNumber,
                task.Id,
                null,
                PolicyAllows: false,
                CanApply: false,
                RequiresOperatorGate: false,
                $"Subscription retry window is deferred until {retryAfter:u}.",
                $"wait until {retryAfter:u}"));
            return;
        }

        if (DispatchFailureClassifier.RequiresSubscriptionLimitReview(task))
        {
            proposals.Add(new GoalSupervisorProposal(
                GoalSupervisorProposalKind.ParkForOperator,
                finding.TaskNumber,
                task.Id,
                null,
                PolicyAllows: false,
                CanApply: false,
                RequiresOperatorGate: true,
                "Repeated subscription usage-limit failures require an operator review note before redispatch.",
                $"subscription-dispatch {finding.TaskNumber} --confirm-limit-review <note>"));
            return;
        }

        if (DispatchFailureClassifier.HasProviderNeutralProgressStallFailure(task) ||
            DispatchFailureClassifier.HasRecoverableProviderConnectivityFailure(task))
        {
            var hasAlternate = HasAlternateAgent(task, agents);
            var allows = policy.Allows(AutonomyAction.ProviderFailover);
            proposals.Add(new GoalSupervisorProposal(
                GoalSupervisorProposalKind.ReDelegateAfterRecoverableFailure,
                finding.TaskNumber,
                task.Id,
                AutonomyAction.ProviderFailover,
                allows,
                CanApply: allows && hasAlternate,
                RequiresOperatorGate: !hasAlternate,
                hasAlternate
                    ? "Recoverable provider-neutral failure has an unused alternate agent; supervisor can re-delegate under policy."
                    : "Recoverable provider-neutral failure has no unused alternate agent; operator must add or repair an agent.",
                hasAlternate ? $"re-delegate {finding.TaskNumber} --autonomy {policy.Name}" : $"agent-add {task.RequiredRole} <provider> <model>"));
            return;
        }

        if (task.Status == WorkTaskStatus.Completed && task.LastVerification is null)
        {
            var allows = policy.Allows(AutonomyAction.BuildTest);
            proposals.Add(new GoalSupervisorProposal(
                GoalSupervisorProposalKind.VerifyMissingEvidence,
                finding.TaskNumber,
                task.Id,
                AutonomyAction.BuildTest,
                allows,
                CanApply: false,
                RequiresOperatorGate: true,
                "Task is completed without verification evidence; deterministic verification command is required.",
                $"verify {finding.TaskNumber} <command> --autonomy {policy.Name}"));
            return;
        }

        if (finding.SuggestedCommand.StartsWith("retry ", StringComparison.OrdinalIgnoreCase))
        {
            var allows = policy.Allows(AutonomyAction.Retry);
            proposals.Add(new GoalSupervisorProposal(
                GoalSupervisorProposalKind.ParkForOperator,
                finding.TaskNumber,
                task.Id,
                AutonomyAction.Retry,
                allows,
                CanApply: false,
                RequiresOperatorGate: true,
                "Retry requires a specific operator note or repair reason.",
                $"retry {finding.TaskNumber} <note> --autonomy {policy.Name}"));
            return;
        }

        proposals.Add(new GoalSupervisorProposal(
            GoalSupervisorProposalKind.ParkForOperator,
            finding.TaskNumber,
            task.Id,
            null,
            PolicyAllows: false,
            CanApply: false,
            RequiresOperatorGate: true,
            finding.Finding,
            finding.SuggestedCommand));
    }

    private static bool HasAlternateAgent(TaskSpec task, IReadOnlyList<AgentDefinition> agents)
    {
        return agents.Any(agent =>
            agent.Role == task.RequiredRole &&
            agent.Status == AgentStatus.Available &&
            agent.Id != task.AssignedAgentId);
    }
}
