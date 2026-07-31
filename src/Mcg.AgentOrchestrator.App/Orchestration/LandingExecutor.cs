using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

public sealed record LandingResult(
    string GoalId,
    string GoalPrefix,
    LandingDecision Decision,
    string IntegrationBranch,
    bool MainAdvanced,
    string Message,
    string? MergeCommitSha = null);

internal sealed record LandingChangedFilesResult(string[] Files, string? UnknownReason);

internal static class LandingExecutor
{
    public const string IntegrationBranchName = "integration";
    private const string TempWorktreeDirName = ".orchestrator-integration-tmp";
    private const string OwnershipHoldReasonPrefix = "ownership-denylist hold";
    internal static Func<string, string[], GitCli.GitResult> GitRunner { get; set; } =
        (workingDirectory, args) => GitCli.Run(workingDirectory, args);

    public static LandingResult Execute(
        AgentOrchestratorKernel kernel,
        Goal goal,
        OrchestratorWorkspace workspace,
        IOperatorChannel? channel = null,
        ConductorAutonomyPolicy? policy = null,
        IGoalLifecycleEventWriter? eventWriter = null)
    {
        var executionDirectory = workspace.ExecutionDirectory;
        var goalPrefix = goal.Id.Value[..8];
        var goalBranch = GoalWorktrees.BranchName(goal.Id);

        if (!BranchExists(executionDirectory, goalBranch))
        {
            throw new InvalidOperationException(
                $"Goal branch '{goalBranch}' does not exist. Create the workspace first with: workspace create {goalPrefix}");
        }

        EnsureIntegrationBranch(executionDirectory);

        var tempPath = Path.Combine(executionDirectory, TempWorktreeDirName);

        // Remove any leftover temp worktree from a prior interrupted run.
        if (IsRegisteredWorktree(executionDirectory, tempPath))
        {
            RunGit(executionDirectory, "worktree", "remove", "--force", tempPath);
        }

        bool mergeSucceeded;
        try
        {
            mergeSucceeded = MergeGoalIntoIntegration(executionDirectory, tempPath, goalBranch);
        }
        finally
        {
            if (IsRegisteredWorktree(executionDirectory, tempPath))
            {
                RunGit(executionDirectory, "worktree", "remove", "--force", tempPath);
            }
        }

        if (!mergeSucceeded)
        {
            var conflictReason = $"merge conflict integrating {goalBranch} into {IntegrationBranchName}";
            OperatorInbox.RecordLandingEscalation(workspace, goal, conflictReason, IntegrationBranchName, channel);
            eventWriter?.AppendGoalEscalated(goal.Id, GoalLifecycleState.Verified, conflictReason, IntegrationBranchName);
            var conflictDecision = new LandingDecision.Escalate(conflictReason);
            return new LandingResult(goal.Id.Value, goalPrefix, conflictDecision, IntegrationBranchName,
                false, $"Parked on {IntegrationBranchName}: {conflictReason}");
        }

        var acceptancePassed = GoalAcceptanceStatusProjector.Build(kernel, goal, workspace.ExecutionDirectory).IsAccepted;
        if (!acceptancePassed)
        {
            var acceptanceDecision = new LandingDecision.Escalate("acceptance verification not passed");
            OperatorInbox.RecordLandingEscalation(workspace, goal, acceptanceDecision.Reason, IntegrationBranchName, channel);
            eventWriter?.AppendGoalEscalated(goal.Id, GoalLifecycleState.Verified, acceptanceDecision.Reason, IntegrationBranchName);
            return new LandingResult(goal.Id.Value, goalPrefix, acceptanceDecision, IntegrationBranchName,
                false, $"Parked on {IntegrationBranchName}: {acceptanceDecision.Reason}");
        }

        var changedFilesResult = GetChangedFiles(executionDirectory, goalBranch);
        if (changedFilesResult.UnknownReason is not null)
        {
            var diffDecision = new LandingDecision.Escalate($"diff scope unknown: {changedFilesResult.UnknownReason}");
            OperatorInbox.RecordLandingEscalation(workspace, goal, diffDecision.Reason, IntegrationBranchName, channel);
            eventWriter?.AppendGoalEscalated(goal.Id, GoalLifecycleState.Verified, diffDecision.Reason, IntegrationBranchName);
            return new LandingResult(goal.Id.Value, goalPrefix, diffDecision, IntegrationBranchName,
                false, $"Parked on {IntegrationBranchName}: {diffDecision.Reason}");
        }

        var changedFiles = changedFilesResult.Files;
        var ownershipGuard = RepositoryOwnershipMap.GuardWriteSet(changedFiles);
        if (ownershipGuard.RequiresOperatorApproval && policy?.AllowsAutonomousHighRiskOwnership != true)
        {
            var holdRequests = BuildOwnershipHoldRequests(goal, executionDirectory, ownershipGuard);
            if (holdRequests.Count > 0)
            {
                OperatorInbox.RecordOwnershipHolds(workspace, goal, holdRequests, channel);
                var reason = $"{OwnershipHoldReasonPrefix}: {holdRequests.Count} task(s) touched RequiresOperatorApproval path(s)";
                var holdDecision = new LandingDecision.Escalate(reason);
                eventWriter?.AppendGoalEscalated(goal.Id, GoalLifecycleState.Verified, reason, "ownership-hold");
                return new LandingResult(goal.Id.Value, goalPrefix, holdDecision, IntegrationBranchName,
                    false, $"Parked on {IntegrationBranchName}: {reason}");
            }

            var unknownReason = "ownership-denylist diff touched RequiresOperatorApproval path(s), but no writing task attribution was available";
            var unknownDecision = new LandingDecision.Escalate(unknownReason);
            OperatorInbox.RecordLandingEscalation(workspace, goal, unknownReason, IntegrationBranchName, channel);
            eventWriter?.AppendGoalEscalated(goal.Id, GoalLifecycleState.Verified, unknownReason, IntegrationBranchName);
            return new LandingResult(goal.Id.Value, goalPrefix, unknownDecision, IntegrationBranchName,
                false, $"Parked on {IntegrationBranchName}: {unknownReason}");
        }

        var cleanFastForward = IsIntegrationFastForwardableIntoMain(executionDirectory);
        LandingDecision decision = cleanFastForward
            ? new LandingDecision.Promote()
            : new LandingDecision.Escalate("integration->main conflict");

        if (decision is LandingDecision.Promote)
        {
            var mergeCommitSha = ResolveRef(executionDirectory, IntegrationBranchName);
            GoalOperationJournal.RecordLandingIntent(
                executionDirectory,
                goal,
                goalBranch,
                IntegrationBranchName,
                mergeCommitSha,
                "LandingExecutor");

            var merge = RunGit(executionDirectory, "merge", "--ff-only", IntegrationBranchName);
            if (merge.ExitCode != 0)
            {
                GoalOperationJournal.TombstoneLandingIntent(
                    executionDirectory,
                    goal,
                    $"integration->main fast-forward failed after landing intent write: {merge.Error}");
                var unexpectedReason = $"integration->main fast-forward failed: {merge.Error}";
                OperatorInbox.RecordLandingEscalation(workspace, goal, unexpectedReason, IntegrationBranchName, channel);
                eventWriter?.AppendGoalEscalated(goal.Id, GoalLifecycleState.Verified, unexpectedReason, IntegrationBranchName);
                var fallback = new LandingDecision.Escalate(unexpectedReason);
                return new LandingResult(goal.Id.Value, goalPrefix, fallback, IntegrationBranchName,
                    false, $"Parked on {IntegrationBranchName}: {unexpectedReason}");
            }

            eventWriter?.AppendGoalLanded(goal.Id, IntegrationBranchName, goalBranch);
            OperatorInbox.ClearOwnershipHoldsAfterLanding(workspace, goal, $"land {goalPrefix}");
            StateEffectProposalApplier.ApplyLandedProposals(kernel, goal, workspace, changedFiles, Console.WriteLine);
            return new LandingResult(goal.Id.Value, goalPrefix, decision, IntegrationBranchName,
                true, $"Promoted: {goalBranch} integrated via {IntegrationBranchName} into main.", mergeCommitSha);
        }

        var escalate = (LandingDecision.Escalate)decision;
        OperatorInbox.RecordLandingEscalation(workspace, goal, escalate.Reason, IntegrationBranchName, channel);
        eventWriter?.AppendGoalEscalated(goal.Id, GoalLifecycleState.Verified, escalate.Reason, IntegrationBranchName);
        return new LandingResult(goal.Id.Value, goalPrefix, decision, IntegrationBranchName,
            false, $"Parked on {IntegrationBranchName}: {escalate.Reason}");
    }

    private static void EnsureIntegrationBranch(string executionDirectory)
    {
        if (BranchExists(executionDirectory, IntegrationBranchName))
        {
            return;
        }

        var result = RunGit(executionDirectory, "branch", IntegrationBranchName, "main");
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Failed to create '{IntegrationBranchName}' branch from main: {result.Error}");
        }
    }

    private static bool MergeGoalIntoIntegration(
        string executionDirectory,
        string tempPath,
        string goalBranch)
    {
        var add = RunGit(executionDirectory, "worktree", "add", tempPath, IntegrationBranchName);
        if (add.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Failed to create integration worktree at '{tempPath}': {add.Error}");
        }

        var merge = RunGit(tempPath, "merge", "--no-ff", goalBranch, "-m", $"Integrate {goalBranch}");
        return merge.ExitCode == 0;
    }

    private static bool IsIntegrationFastForwardableIntoMain(string executionDirectory)
    {
        // Exits 0 if main is an ancestor of integration — fast-forward from main to integration tip is possible.
        return RunGit(executionDirectory, "merge-base", "--is-ancestor", "main", IntegrationBranchName).ExitCode == 0;
    }

    private static string ResolveRef(string executionDirectory, string reference)
    {
        var result = RunGit(executionDirectory, "rev-parse", reference);
        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.Output))
        {
            throw new InvalidOperationException($"Failed to resolve '{reference}' before landing: {result.Error}");
        }

        return result.Output.Trim();
    }

    private static bool BranchExists(string executionDirectory, string branch)
    {
        return RunGit(executionDirectory, "rev-parse", "--verify", "--quiet", $"refs/heads/{branch}").ExitCode == 0;
    }

    private static bool IsRegisteredWorktree(string executionDirectory, string path)
    {
        var result = RunGit(executionDirectory, "worktree", "list", "--porcelain");
        if (result.ExitCode != 0)
        {
            return false;
        }

        var normalized = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        return result.Output
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.StartsWith("worktree ", StringComparison.Ordinal))
            .Select(line => Path.GetFullPath(line["worktree ".Length..].Trim()).TrimEnd(Path.DirectorySeparatorChar))
            .Any(wt => string.Equals(wt, normalized, StringComparison.OrdinalIgnoreCase));
    }

    private static LandingChangedFilesResult GetChangedFiles(string executionDirectory, string goalBranch)
    {
        var result = RunGit(executionDirectory, "diff", "--name-only", $"main...{goalBranch}");
        if (result.DrainTimedOut)
        {
            return new LandingChangedFilesResult([], "git diff output drain timed out");
        }

        if (result.ExitCode != 0)
        {
            return new LandingChangedFilesResult([], $"git diff failed: {result.Error}");
        }

        if (string.IsNullOrWhiteSpace(result.Output))
        {
            return new LandingChangedFilesResult([], null);
        }

        var files = result.Output
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();
        return new LandingChangedFilesResult(files, null);
    }

    internal static bool IsOwnershipHoldEscalation(string reason) =>
        reason.StartsWith(OwnershipHoldReasonPrefix, StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<OwnershipHoldRequest> BuildOwnershipHoldRequests(
        Goal goal,
        string executionDirectory,
        RepositoryWriteSetGuardReport ownershipGuard)
    {
        var approvalPaths = ownershipGuard.Paths
            .Where(path => path.IsHighRisk || path.IsGeneratedOrNoisy)
            .Select(path => path.Path)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (approvalPaths.Count == 0)
        {
            return [];
        }

        var changes = GoalChangesReader.Build(
            goal,
            executionDirectory,
            showCommitted: true,
            showWorking: false,
            roleFilter: null,
            taskFilter: null);
        var requests = new List<OwnershipHoldRequest>();
        foreach (var entry in changes.Entries.Where(entry => IsWriteRole(entry.Role)))
        {
            var touchedApprovalPaths = entry.Files
                .Select(NormalizePath)
                .Where(path => approvalPaths.Contains(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (touchedApprovalPaths.Length == 0)
            {
                continue;
            }

            requests.Add(new OwnershipHoldRequest(
                new TaskId(entry.TaskId),
                ResolveTaskNumber(goal, entry.TaskId),
                entry.Role,
                touchedApprovalPaths,
                entry.Attribution.ToString()));
        }

        return requests;
    }

    private static bool IsWriteRole(AgentRole role) =>
        role is AgentRole.Developer or AgentRole.Tester;

    private static int ResolveTaskNumber(Goal goal, string taskId)
    {
        for (var index = 0; index < goal.Tasks.Count; index++)
        {
            if (goal.Tasks[index].Id.Value.Equals(taskId, StringComparison.OrdinalIgnoreCase))
            {
                return index + 1;
            }
        }

        return 0;
    }

    private static string NormalizePath(string path) =>
        path.Replace('\\', '/').Trim().TrimStart('/');

    private static GitCli.GitResult RunGit(string workingDirectory, params string[] args) =>
        GitRunner(workingDirectory, args);

    // Counts DISTINCT tasks with genuine verification failures. A task whose only failures were
    // transient empty-output dispatch flakes and whose latest verification passed was auto-recovered
    // by the conductor, so it is not evidence that the goal struggled.
    internal static int CountFailedVerifications(Goal goal)
    {
        return goal.Tasks.Count(HasCountableFailedVerification);
    }

    private static bool HasCountableFailedVerification(TaskSpec task)
    {
        var failedVerifications = task.VerificationHistory
            .Where(verification => !verification.Succeeded)
            .ToArray();

        if (failedVerifications.Length == 0)
        {
            return false;
        }

        return task.LastVerification?.Succeeded is not true ||
            failedVerifications.Any(verification => !DispatchFailureClassifier.IsTransientEmptyOutputDispatchFlake(verification));
    }
}
