using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal enum RetentionGoalState
{
    Active,
    Waiting,
    ReadyForAcceptance,
    AcceptedCleaned,
    Failed,
    Abandoned,
    Superseded
}

internal enum RetentionArtifactKind
{
    Worktree,
    ContextPackage,
    WorkerLogs,
    BuildLease,
    TestEvidence,
    OperationJournal,
    Transcript
}

internal enum RetentionDecision
{
    Keep,
    Archive,
    DeleteWhenSafe,
    DeleteNow,
    Missing
}

internal sealed record GoalArtifactRetentionPlan(
    GoalId GoalId,
    string GoalPrefix,
    RetentionGoalState State,
    bool DryRun,
    IReadOnlyList<GoalArtifactRetentionItem> Items);

internal sealed record GoalArtifactRetentionItem(
    RetentionArtifactKind Kind,
    RetentionDecision Decision,
    string Path,
    bool Exists,
    string Reason,
    string? SuggestedCommand);

internal static class GoalArtifactRetentionPlanner
{
    public static GoalArtifactRetentionPlan Build(
        AgentOrchestratorKernel kernel,
        Goal goal,
        OrchestratorWorkspace workspace,
        bool dryRun = true,
        DotnetBuildStorageRoot? buildStorageRoot = null)
    {
        var goalPrefix = goal.Id.Value[..8];
        var worktreePath = GoalWorktrees.TryResolve(workspace.ExecutionDirectory, goal.Id);
        var branchExists = BranchExists(workspace.ExecutionDirectory, GoalWorktrees.BranchName(goal.Id));
        var acceptance = GoalAcceptanceStatusProjector.Build(kernel, goal, workspace.ExecutionDirectory);
        var state = ClassifyState(goal, acceptance, worktreePath is not null, branchExists);
        var buildLease = DotnetBuildEnvironmentManager.InspectGoalLease(goal.Id, buildStorageRoot);
        var contextPath = Path.Combine(workspace.ExecutionDirectory, ".orchestrator-context", goal.Id.Value);
        var journal = GoalOperationJournal.Read(workspace.ExecutionDirectory, goal.Id);
        var transcriptPath = Path.Combine(workspace.ExecutionDirectory, ".orchestrator", "transcripts", $"{goalPrefix}.md");
        var acceptanceEvidencePath = Path.Combine(
            workspace.OrchestratorDirectory,
            "acceptance-gate-attempts",
            goal.Id.Value);
        var preReviewEvidencePath = Path.Combine(
            workspace.OrchestratorDirectory,
            "pre-review-evidence-attempts",
            goal.Id.Value);

        var items = new List<GoalArtifactRetentionItem>
        {
            WorktreeItem(goalPrefix, state, worktreePath, branchExists),
            ContextItem(state, contextPath),
            LogsItem(state, workspace.LogDirectory),
            BuildLeaseItem(state, buildLease),
            TestEvidenceItem(state, acceptanceEvidencePath, "acceptance-gate"),
            TestEvidenceItem(state, preReviewEvidencePath, "pre-review"),
            JournalItem(journal.Path),
            TranscriptItem(transcriptPath)
        };

        return new GoalArtifactRetentionPlan(goal.Id, goalPrefix, state, dryRun, items);
    }

    private static RetentionGoalState ClassifyState(
        Goal goal,
        GoalAcceptanceSummary acceptance,
        bool hasWorktree,
        bool hasBranch)
    {
        return goal.Status switch
        {
            GoalStatus.Completed when acceptance.IsAccepted && !hasWorktree && !hasBranch => RetentionGoalState.AcceptedCleaned,
            GoalStatus.Verified when acceptance.IsAccepted => RetentionGoalState.ReadyForAcceptance,
            GoalStatus.Completed => RetentionGoalState.Waiting,
            GoalStatus.Failed => RetentionGoalState.Failed,
            GoalStatus.Cancelled => RetentionGoalState.Abandoned,
            GoalStatus.Superseded => RetentionGoalState.Superseded,
            GoalStatus.WaitingForHuman => RetentionGoalState.Waiting,
            GoalStatus.Parked => RetentionGoalState.Waiting,
            _ => RetentionGoalState.Active
        };
    }

    private static GoalArtifactRetentionItem WorktreeItem(
        string goalPrefix,
        RetentionGoalState state,
        string? worktreePath,
        bool branchExists)
    {
        var path = worktreePath ?? $"branch:{goalPrefix}";
        return state switch
        {
            RetentionGoalState.AcceptedCleaned => new(
                RetentionArtifactKind.Worktree,
                RetentionDecision.Missing,
                path,
                Exists: false,
                "Accepted goal has no remaining worktree or goal branch.",
                null),
            RetentionGoalState.ReadyForAcceptance => new(
                RetentionArtifactKind.Worktree,
                RetentionDecision.DeleteWhenSafe,
                path,
                worktreePath is not null || branchExists,
                "Keep until acceptance/merge completes; remove after audit evidence is preserved.",
                $"acceptance {goalPrefix} && workspace remove {goalPrefix}"),
            RetentionGoalState.Failed or RetentionGoalState.Abandoned or RetentionGoalState.Superseded => new(
                RetentionArtifactKind.Worktree,
                RetentionDecision.Archive,
                path,
                worktreePath is not null || branchExists,
                "Preserve failed or abandoned workspace until rollback/abandon workflow archives useful artifacts.",
                $"goal-recovery {goalPrefix}"),
            _ => new(
                RetentionArtifactKind.Worktree,
                RetentionDecision.Keep,
                path,
                worktreePath is not null || branchExists,
                "Goal is still active or waiting; keep workspace state intact.",
                null)
        };
    }

    private static GoalArtifactRetentionItem ContextItem(RetentionGoalState state, string path)
    {
        return new GoalArtifactRetentionItem(
            RetentionArtifactKind.ContextPackage,
            state is RetentionGoalState.AcceptedCleaned or RetentionGoalState.Failed or RetentionGoalState.Abandoned or RetentionGoalState.Superseded
                ? RetentionDecision.Archive
                : RetentionDecision.Keep,
            path,
            Directory.Exists(path),
            "Context packages are audit evidence and should outlive workspace cleanup.",
            null);
    }

    private static GoalArtifactRetentionItem LogsItem(RetentionGoalState state, string path)
    {
        return new GoalArtifactRetentionItem(
            RetentionArtifactKind.WorkerLogs,
            state is RetentionGoalState.AcceptedCleaned ? RetentionDecision.Archive : RetentionDecision.Keep,
            path,
            Directory.Exists(path),
            "Worker logs remain shared audit evidence; age-based pruning should archive before deletion.",
            null);
    }

    private static GoalArtifactRetentionItem BuildLeaseItem(RetentionGoalState state, DotnetBuildLeaseStatus lease)
    {
        var decision = state switch
        {
            RetentionGoalState.AcceptedCleaned when lease.RootExists => RetentionDecision.DeleteNow,
            RetentionGoalState.Failed or RetentionGoalState.Abandoned or RetentionGoalState.Superseded when lease.CanCleanup => RetentionDecision.DeleteNow,
            RetentionGoalState.Failed or RetentionGoalState.Abandoned or RetentionGoalState.Superseded => RetentionDecision.DeleteWhenSafe,
            _ when !lease.RootExists => RetentionDecision.Missing,
            _ => RetentionDecision.Keep
        };

        return new GoalArtifactRetentionItem(
            RetentionArtifactKind.BuildLease,
            decision,
            lease.RootPath,
            lease.RootExists,
            lease.Detail,
            decision is RetentionDecision.DeleteNow or RetentionDecision.DeleteWhenSafe
                ? "build-lease-cleanup --confirm-build-lease-cleanup"
                : null);
    }

    private static GoalArtifactRetentionItem JournalItem(string path)
    {
        return new GoalArtifactRetentionItem(
            RetentionArtifactKind.OperationJournal,
            RetentionDecision.Keep,
            path,
            File.Exists(path),
            "Operation journal is durable lifecycle audit evidence.",
            null);
    }

    private static GoalArtifactRetentionItem TestEvidenceItem(
        RetentionGoalState state,
        string path,
        string evidenceKind)
    {
        var terminal = state is RetentionGoalState.AcceptedCleaned or
            RetentionGoalState.Failed or
            RetentionGoalState.Abandoned or
            RetentionGoalState.Superseded;
        var factRevision = EvidenceRetentionPolicy.ComputeFactRevision([], [path], state.ToString());
        var eligibility = EvidenceRetentionPolicy.EvaluatePath(
            terminal,
            owner: null,
            EvidenceOwnershipSource.Unresolved,
            protectedAttempt: false,
            referencedArtifact: false,
            countBound: false,
            aged: false,
            byteBoundEligible: false,
            factRevision,
            requireOwner: false);
        var decision = eligibility.Disposition switch
        {
            EvidenceEligibility.Keep => RetentionDecision.Keep,
            EvidenceEligibility.Archive => RetentionDecision.Archive,
            EvidenceEligibility.DeleteWhenSafe => RetentionDecision.DeleteWhenSafe,
            _ => throw new InvalidOperationException($"Unsupported evidence eligibility '{eligibility.Disposition}'.")
        };
        return new GoalArtifactRetentionItem(
            RetentionArtifactKind.TestEvidence,
            decision,
            path,
            Directory.Exists(path),
            $"{evidenceKind} receipts are goal-owned audit evidence and follow the goal retention lifecycle.",
            null);
    }

    private static GoalArtifactRetentionItem TranscriptItem(string path)
    {
        return new GoalArtifactRetentionItem(
            RetentionArtifactKind.Transcript,
            RetentionDecision.Archive,
            path,
            File.Exists(path),
            "Transcript files are optional but should be archived when present.",
            null);
    }

    private static bool BranchExists(string executionDirectory, string branch) =>
        GitCli.Run(executionDirectory, "rev-parse", "--verify", "--quiet", $"refs/heads/{branch}").Succeeded;
}
