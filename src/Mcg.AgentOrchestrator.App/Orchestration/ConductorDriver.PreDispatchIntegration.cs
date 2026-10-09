using Mcg.AgentOrchestrator.App.Application;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorDriver
{
    internal static DeveloperBranchIntegrationResult IntegrateMainBeforeDispatch(
        string executionDirectory,
        Goal goal,
        AgentRole role, string integrationBranch)
    {
        var branch = GoalWorktrees.BranchName(goal.Id);
        var worktreePath = GoalWorktrees.TryResolve(executionDirectory, goal.Id);
        if (worktreePath is null)
        {
            return DeveloperIntegrationFailure($"Goal branch {branch} has no registered worktree.");
        }

        var status = GitCli.InspectWorktreeStatus(worktreePath);
        if (!status.Succeeded || status.IsDirty)
        {
            var paths = status.Succeeded && status.CommitWorthyPaths.Count > 0
                ? " Dirty paths: " + string.Join(", ", status.CommitWorthyPaths.Take(20)) +
                  (status.CommitWorthyPaths.Count > 20 ? $" (+{status.CommitWorthyPaths.Count - 20} more)" : "")
                : "";
            return DeveloperIntegrationFailure(
                $"Goal branch {branch} has uncommitted changes; conductor integration cannot start from a dirty worktree." + paths);
        }

        var currentBranch = GitCli.Run(worktreePath, "branch", "--show-current");
        if (currentBranch.ExitCode != 0 ||
            !string.Equals(currentBranch.Output.Trim(), branch, StringComparison.Ordinal))
        {
            return DeveloperIntegrationFailure(
                $"Registered worktree for {branch} is not attached to the expected branch.");
        }

        var mainHead = GitCli.Run(worktreePath, "rev-parse", "--verify", $"{integrationBranch}^{{commit}}");
        var branchHead = GitCli.Run(worktreePath, "rev-parse", "--verify", "HEAD^{commit}");
        if (mainHead.ExitCode != 0 || branchHead.ExitCode != 0 ||
            string.IsNullOrWhiteSpace(mainHead.Output) || string.IsNullOrWhiteSpace(branchHead.Output))
        {
            return DeveloperIntegrationFailure(
                $"Could not resolve {integrationBranch} and {branch} before {role} dispatch.");
        }

        var mainRevision = mainHead.Output.Trim();
        var branchRevision = branchHead.Output.Trim();
        if (GitCli.Run(worktreePath, "merge-base", "--is-ancestor", mainRevision, branchRevision).ExitCode == 0)
        {
            return new DeveloperBranchIntegrationResult(
                DeveloperBranchIntegrationStatus.Current,
                $"Goal branch {branch} is already current with {integrationBranch} at {mainRevision[..12]}.",
                []);
        }

        ReviewerMergeTreeStatus mergeTree;
        try
        {
            mergeTree = new WorkerGitContext().ReadReviewerMergeTreeStatus(
                worktreePath,
                mainRevision,
                branchRevision);
        }
        catch (ReviewerMergeTreeStatusException ex)
        {
            return DeveloperIntegrationFailure(
                $"Conductor could not inspect divergence for {branch} before {role} dispatch: {ex.Message}");
        }

        if (!mergeTree.IsClean)
        {
            return new DeveloperBranchIntegrationResult(
                DeveloperBranchIntegrationStatus.Conflict,
                BuildDeveloperIntegrationConflictMessage(branch, mergeTree.ConflictPaths, role, integrationBranch),
                mergeTree.ConflictPaths);
        }

        var mergeArgs = new List<string> { "merge", "--no-ff", mainRevision, "-m", $"Integrate {integrationBranch} into {branch} before {role} dispatch" };
        var goalTitleBody = OrchestratorCommitMessage.GoalTitleBody(goal);
        if (goalTitleBody is not null)
        {
            mergeArgs.AddRange(["-m", goalTitleBody]);
        }

        var merge = GitCli.Run(worktreePath, mergeArgs.ToArray());
        if (merge.ExitCode != 0)
        {
            var conflictPaths = ReadUnmergedPaths(worktreePath);
            _ = GitCli.Run(worktreePath, "merge", "--abort");
            if (conflictPaths.Length > 0)
            {
                return new DeveloperBranchIntegrationResult(
                    DeveloperBranchIntegrationStatus.Conflict,
                    BuildDeveloperIntegrationConflictMessage(branch, conflictPaths, role, integrationBranch),
                    conflictPaths);
            }

            var diagnostic = string.Join(
                " | ",
                new[] { merge.Error, merge.Output }
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Select(value => value.Trim().ReplaceLineEndings(" | ")));
            return DeveloperIntegrationFailure(
                $"Conductor could not integrate {integrationBranch} into {branch} before {role} dispatch: " +
                (diagnostic.Length == 0 ? $"git merge exited {merge.ExitCode}." : diagnostic));
        }

        var integratedHead = GitCli.Run(worktreePath, "rev-parse", "--verify", "HEAD^{commit}");
        var integrationIsCurrent = integratedHead.ExitCode == 0 &&
            GitCli.Run(
                worktreePath,
                "merge-base",
                "--is-ancestor",
                mainRevision,
                integratedHead.Output.Trim()).ExitCode == 0;
        if (!integrationIsCurrent || GitCli.IsWorktreeDirty(worktreePath))
        {
            return DeveloperIntegrationFailure(
                $"Conductor integrated {integrationBranch} into {branch}, but the resulting branch failed the clean/current invariant; {role} dispatch is blocked.");
        }

        return new DeveloperBranchIntegrationResult(
            DeveloperBranchIntegrationStatus.Integrated,
            $"Conductor integrated {integrationBranch} {mainRevision[..12]} into {branch} before {role} dispatch at {integratedHead.Output.Trim()[..12]}.",
            [],
            OriginalCandidateSha: branchRevision,
            IntegratedMainSha: mainRevision,
            ResultingCandidateSha: integratedHead.Output.Trim());
    }

    private static DeveloperBranchIntegrationResult DeveloperIntegrationFailure(string message) =>
        new(DeveloperBranchIntegrationStatus.Failed, message, []);

    private static string BuildDeveloperIntegrationConflictMessage(
        string branch,
        IReadOnlyList<string> conflictPaths,
        AgentRole role, string integrationBranch) =>
        $"{role} dispatch blocked: {integrationBranch} conflicts with {branch} in {string.Join(", ", conflictPaths)}. " +
        "Conflict resolution requires semantic ownership and must be performed by the conductor or operator; " +
        "do not instruct a worker to rebase or resolve the branch integration.";

    private static string[] ReadUnmergedPaths(string worktreePath)
    {
        var result = GitCli.Run(worktreePath, "diff", "--name-only", "--diff-filter=U");
        return result.ExitCode == 0
            ? result.Output
                .ReplaceLineEndings("\n")
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray()
            : [];
    }

    internal static DeveloperBranchIntegrationResult IntegrateMainBeforeReadOnlyDispatch(
        string executionDirectory,
        Goal goal,
        AgentRole role, string integrationBranch)
    {
        if (GoalWorktrees.TryResolve(executionDirectory, goal.Id) is null)
        {
            return new DeveloperBranchIntegrationResult(
                DeveloperBranchIntegrationStatus.Current,
                "No registered goal worktree; read-only pre-dispatch integration skipped.",
                []);
        }

        return IntegrateMainBeforeDispatch(executionDirectory, goal, role, integrationBranch);
    }

    private static bool TryGetAssignedReadOnlyRoleReadyForDispatch(Goal goal, out AgentRole role)
    {
        var task = goal.Tasks.FirstOrDefault(task =>
            (task.RequiredRole is AgentRole.Researcher or AgentRole.Planner) &&
            task.Status == WorkTaskStatus.Assigned &&
            !goal.Tasks.Any(candidate =>
                DispatchReadinessRules.IsEarlierSdlcStageOf(candidate.RequiredRole, task.RequiredRole) &&
                candidate.Status != WorkTaskStatus.Completed));
        role = task?.RequiredRole ?? default;
        return task is not null;
    }

    private bool TryIntegrateMainBeforeReadOnlyDispatch(
        Goal goal,
        string goalPrefix,
        ConductorAutonomyPolicy policy,
        GoalLifecycleState fromState,
        out ConductorAdvanceResult result)
    {
        result = default!;
        if (_integrateMainBeforeReadOnlyDispatch is null ||
            !TryGetAssignedReadOnlyRoleReadyForDispatch(goal, out var role))
        {
            return false;
        }

        if (_executionDirectory is not null &&
            GoalWorktrees.TryResolve(_executionDirectory, goal.Id) is null)
        {
            return false;
        }

        if (_tryBeginDeveloperIntegrationEvidenceOperation is not null)
        {
            var operationStart = _tryBeginDeveloperIntegrationEvidenceOperation(goal);
            if (operationStart.Scope is null)
            {
                var status = operationStart.LeaseFact is { } leaseFact
                    ? GoalEvidenceLeaseRecoveryStatuses.Format(leaseFact.RecoveryStatus)
                    : "state-unavailable";
                result = DispatchStartResult(goal, goalPrefix, policy, fromState, new DispatchStartFacts(goal.Id.Value)
                {
                    ReadOnlyIntegrationHoldReason = $"Goal evidence mutation is held for {goal.Id.Value}; lease-recovery={status}."
                });
                return true;
            }

            using (operationStart.Scope)
            {
                DeveloperBranchIntegrationResult integration;
                try
                {
                    integration = _integrateMainBeforeReadOnlyDispatch(goal, role);
                    _recordPreDispatchIntegrationReceipt(goal, integration);
                }
                catch (OperationCanceledException ex)
                {
                    operationStart.Scope.Abort(ex.Message);
                    throw;
                }
                catch (Exception ex)
                {
                    operationStart.Scope.Fail(ex.Message);
                    throw;
                }

                if (integration.CanDispatch)
                {
                    operationStart.Scope.Complete(integration.Message);
                }
                else
                {
                    operationStart.Scope.Fail(integration.Message);
                    result = Escalate(goal, goalPrefix, policy, fromState, integration.Message);
                    return true;
                }
            }
        }
        else
        {
            using var integrationEvidenceMutationLease = _tryAcquireEvidenceMutationLease(
                goal,
                "conductor:developer-branch-integration");
            if (integrationEvidenceMutationLease is null)
            {
                result = DispatchStartResult(goal, goalPrefix, policy, fromState, new DispatchStartFacts(goal.Id.Value)
                {
                    ReadOnlyIntegrationHoldReason = $"Goal evidence mutation is blocked by concurrent acceptance or replacement for {goal.Id.Value}."
                });
                return true;
            }

            var integration = _integrateMainBeforeReadOnlyDispatch(goal, role);
            _recordPreDispatchIntegrationReceipt(goal, integration);
            if (!integration.CanDispatch)
            {
                result = Escalate(goal, goalPrefix, policy, fromState, integration.Message);
                return true;
            }
        }

        return false;
    }

    private ConductorAdvanceResult DispatchStartResult(
        Goal goal, string goalPrefix, ConductorAutonomyPolicy policy,
        GoalLifecycleState fromState, DispatchStartFacts facts)
    {
        var decision = DispatchStartPolicy.Evaluate(facts);
        if (decision.Action == DispatchStartAction.Hold)
            return MakeResult(goal.Id.Value, goalPrefix, policy,
                new ConductorAdvanceOutcome.Held(fromState, decision.Reason) { Decision = decision.ToRecord() });
        if (decision.Action != DispatchStartAction.Escalate)
            throw new InvalidOperationException("A dispatch-start refusal must hold or escalate.");

        var result = Escalate(goal, goalPrefix, policy, fromState, decision.Reason);
        var escalated = result.Outcome as ConductorAdvanceOutcome.Escalated
            ?? throw new InvalidOperationException("Dispatch-start escalation must produce an escalated outcome.");
        return result with { Outcome = escalated with { Decision = decision.ToRecord() } };
    }

    private static DispatchStartFacts WithIntegrationFacts(
        DispatchStartFacts facts, DeveloperBranchIntegrationResult integration) => facts with
    {
        IntegrationCanDispatch = integration.CanDispatch ? "allowed" : "refused",
        IntegrationMessage = integration.Message
    };

    private static DispatchStartFacts WithReadinessFacts(
        DispatchStartFacts facts, DispatchReadinessVerdict readiness) => facts with
    {
        ReadinessVerdict = readiness switch
        {
            DispatchReadinessReady => "ready",
            DispatchReadinessDeferred => "deferred",
            DispatchReadinessBlocked { HasCandidates: true } => "blocked-with-candidates",
            DispatchReadinessBlocked => "blocked-without-candidates",
            _ => "other"
        },
        ReadinessReason = readiness switch
        {
            DispatchReadinessDeferred deferred => deferred.Reason,
            DispatchReadinessBlocked blocked => blocked.Reason,
            _ => ""
        }
    };
}
