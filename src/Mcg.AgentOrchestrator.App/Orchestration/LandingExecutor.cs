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
    string? MergeCommitSha = null,
    IReadOnlyList<string>? ChangedFiles = null);

internal enum AcceptanceCohortLandingOutcome
{
    Advanced,
    StateInvalidated,
    RetryableHold
}

internal sealed record AcceptanceCohortLandingResult(
    AcceptanceCohortLandingOutcome Outcome,
    string Message,
    string? CommitRevision = null,
    IReadOnlyList<AcceptanceCohortCoverage>? Coverage = null)
{
    public bool MainAdvanced => Outcome == AcceptanceCohortLandingOutcome.Advanced;
}

internal static partial class LandingExecutor
{
    public const string IntegrationBranchName = "integration";
    private const string TempWorktreeDirName = ".orchestrator-integration-tmp";
    private const string LandingAnchorRefPrefix = "refs/orchestrator/landing/";
    private const string OwnershipHoldReasonPrefix = "ownership-denylist hold";
    private const string MutationHoldReasonPrefix = "landing mutation blocked:";
    private const string PostLandingConfirmationReasonPrefix =
        "goal landed previously, then post-landing confirmation failed:";
    internal static Func<string, string[], GitCli.GitResult> GitRunner { get; set; } =
        (workingDirectory, args) => GitCli.Run(workingDirectory, args);

    public static LandingResult Execute(
        AgentOrchestratorKernel kernel,
        Goal goal,
        OrchestratorWorkspace workspace,
        IOperatorChannel? channel = null,
        ConductorAutonomyPolicy? policy = null,
        IGoalLifecycleEventWriter? eventWriter = null,
        Func<string?>? mutationBlocker = null)
    {
        var executionDirectory = workspace.ExecutionDirectory;
        var goalPrefix = goal.Id.Value[..8];
        var goalBranch = GoalWorktrees.BranchName(goal.Id);

        if (!BranchExists(executionDirectory, goalBranch))
        {
            throw new InvalidOperationException(
                $"Goal branch '{goalBranch}' does not exist. Create the workspace first with: workspace create {goalPrefix}");
        }

        if (TryResolveCompletedLanding(
                goal,
                workspace,
                goalPrefix,
                goalBranch,
                channel,
                eventWriter) is { } completedLanding)
        {
            return completedLanding;
        }

        if (TryRecoverInterruptedLanding(
                kernel,
                goal,
                workspace,
                goalPrefix,
                channel,
                eventWriter) is { } interruptedLanding)
        {
            return interruptedLanding;
        }

        var changedFilesResult = GoalWorktrees.ResolveChangedFilesAgainstHead(
            executionDirectory,
            goal.Id,
            GitRunner);
        if (!changedFilesResult.Succeeded)
        {
            var diffDecision = new LandingDecision.Escalate(
                $"diff scope unknown: {changedFilesResult.FailureReason}");
            OperatorInbox.RecordLandingEscalation(
                workspace,
                goal,
                diffDecision.Reason,
                IntegrationBranchName,
                channel);
            eventWriter?.AppendGoalEscalated(
                goal.Id,
                GoalLifecycleState.Verified,
                goal.Status,
                diffDecision.Reason,
                IntegrationBranchName);
            return new LandingResult(
                goal.Id.Value,
                goalPrefix,
                diffDecision,
                IntegrationBranchName,
                false,
                $"Landing blocked before merge: {diffDecision.Reason}");
        }

        var changedFiles = changedFilesResult.Files;
        var acceptance = GoalAcceptanceStatusProjector.Build(kernel, goal, workspace.ExecutionDirectory);
        if (!acceptance.IsAccepted)
        {
            // A sibling landing path can complete after this attempt's initial journal read.
            // Re-read at the decision point so an irreversible landing is not reported as
            // a failed acceptance check.
            if (TryResolveCompletedLanding(
                    goal,
                    workspace,
                    goalPrefix,
                    goalBranch,
                    channel,
                    eventWriter) is { } concurrentlyCompletedLanding)
            {
                return concurrentlyCompletedLanding;
            }

            var decision = LandingDecisionEngine.Decide(new LandingInputs(
                RepositoryChangeClassifier.Classify(changedFiles),
                AcceptancePassed: false,
                IntegrationToMainIsCleanFastForward: true,
                GoalFailureRetryCount: 0,
                Policy: policy,
                AcceptanceHoldDescription: acceptance.AcceptanceHoldDescription));
            if (decision is not LandingDecision.Escalate acceptanceDecision)
            {
                throw new InvalidOperationException(
                    "Landing decision engine promoted a candidate whose acceptance status was not accepted.");
            }
            OperatorInbox.RecordLandingEscalation(workspace, goal, acceptanceDecision.Reason, IntegrationBranchName, channel);
            eventWriter?.AppendGoalEscalated(goal.Id, GoalLifecycleState.Verified, goal.Status, acceptanceDecision.Reason, IntegrationBranchName);
            return new LandingResult(goal.Id.Value, goalPrefix, acceptanceDecision, IntegrationBranchName,
                false, $"Parked on {IntegrationBranchName}: {acceptanceDecision.Reason}");
        }

        var evidenceCandidateSha = ResolveRef(executionDirectory, goalBranch);
        var boundMainRevision = ResolveRef(executionDirectory, "main");
        var evidenceDiagnostic = AcceptanceCriterionEvidence.RebindRecordFromPassedCandidateAndDescribeOutstanding(
            goal,
            evidenceCandidateSha,
            boundMainRevision,
            kernel,
            executionDirectory);
        if (evidenceDiagnostic is not null)
        {
            var evidenceDecision = new LandingDecision.Escalate(evidenceDiagnostic);
            OperatorInbox.RecordLandingEscalation(workspace, goal, evidenceDiagnostic, IntegrationBranchName, channel);
            eventWriter?.AppendGoalEscalated(
                goal.Id,
                GoalLifecycleState.Verified,
                goal.Status,
                evidenceDiagnostic,
                IntegrationBranchName);
            return new LandingResult(
                goal.Id.Value,
                goalPrefix,
                evidenceDecision,
                IntegrationBranchName,
                false,
                $"Parked on {IntegrationBranchName}: {evidenceDiagnostic}");
        }

        var ownershipGuard = RepositoryOwnershipMap.GuardWriteSet(changedFiles);
        if (ownershipGuard.RequiresOperatorApproval && policy?.AllowsAutonomousHighRiskOwnership != true)
        {
            var holdRequests = BuildOwnershipHoldRequests(goal, executionDirectory, ownershipGuard);
            if (holdRequests.Count > 0)
            {
                OperatorInbox.RecordOwnershipHolds(workspace, goal, holdRequests, channel);
                var reason = $"{OwnershipHoldReasonPrefix}: {holdRequests.Count} task(s) touched RequiresOperatorApproval path(s)";
                var holdDecision = new LandingDecision.Escalate(reason);
                eventWriter?.AppendGoalEscalated(goal.Id, GoalLifecycleState.Verified, goal.Status, reason, "ownership-hold");
                return new LandingResult(goal.Id.Value, goalPrefix, holdDecision, IntegrationBranchName,
                    false, $"Parked on {IntegrationBranchName}: {reason}");
            }

            var unknownReason = "ownership-denylist diff touched RequiresOperatorApproval path(s), but no writing task attribution was available";
            var unknownDecision = new LandingDecision.Escalate(unknownReason);
            OperatorInbox.RecordLandingEscalation(workspace, goal, unknownReason, IntegrationBranchName, channel);
            eventWriter?.AppendGoalEscalated(goal.Id, GoalLifecycleState.Verified, goal.Status, unknownReason, IntegrationBranchName);
            return new LandingResult(goal.Id.Value, goalPrefix, unknownDecision, IntegrationBranchName,
                false, $"Parked on {IntegrationBranchName}: {unknownReason}");
        }

        var previousIntegrationRevision = TryResolveBranch(executionDirectory, IntegrationBranchName);
        if (previousIntegrationRevision is not null &&
            RunGit(executionDirectory, "merge-base", "--is-ancestor", previousIntegrationRevision, boundMainRevision).ExitCode != 0)
        {
            const string reason = "integration branch contains state not present on bound main";
            OperatorInbox.RecordLandingEscalation(workspace, goal, reason, IntegrationBranchName, channel);
            eventWriter?.AppendGoalEscalated(goal.Id, GoalLifecycleState.Verified, goal.Status, reason, IntegrationBranchName);
            return new LandingResult(goal.Id.Value, goalPrefix, new LandingDecision.Escalate(reason), IntegrationBranchName,
                false, $"Parked on {IntegrationBranchName}: {reason}");
        }

        var tempPath = Path.Combine(executionDirectory, TempWorktreeDirName);
        if (IsRegisteredWorktree(executionDirectory, tempPath))
        {
            RunGit(executionDirectory, "worktree", "remove", "--force", tempPath);
        }

        string? candidateRevision = null;
        try
        {
            var blockReason = mutationBlocker?.Invoke();
            if (!string.IsNullOrWhiteSpace(blockReason))
            {
                return BuildMutationBlockedResult(goal, goalPrefix, blockReason);
            }

            if (!TryPrepareCandidateFromBoundMain(
                    goal,
                    executionDirectory,
                    tempPath,
                    boundMainRevision,
                    goalBranch,
                    out candidateRevision,
                    out var preparationFailure))
            {
                var conflictReason = $"landing candidate preparation failed for {goalBranch} from bound main {boundMainRevision}: {preparationFailure}";
                OperatorInbox.RecordLandingEscalation(workspace, goal, conflictReason, IntegrationBranchName, channel);
                eventWriter?.AppendGoalEscalated(goal.Id, GoalLifecycleState.Verified, goal.Status, conflictReason, IntegrationBranchName);
                return new LandingResult(goal.Id.Value, goalPrefix, new LandingDecision.Escalate(conflictReason), IntegrationBranchName,
                    false, $"Parked on {IntegrationBranchName}: {conflictReason}");
            }

            var anchor = CreateLandingAnchor(executionDirectory, goal, candidateRevision!);
            if (anchor.ExitCode != 0)
            {
                var anchorReason = $"landing candidate anchor update failed: {FormatGitFailure(anchor)}";
                OperatorInbox.RecordLandingEscalation(workspace, goal, anchorReason, IntegrationBranchName, channel);
                eventWriter?.AppendGoalEscalated(goal.Id, GoalLifecycleState.Verified, goal.Status, anchorReason, IntegrationBranchName);
                return new LandingResult(goal.Id.Value, goalPrefix, new LandingDecision.Escalate(anchorReason), IntegrationBranchName,
                    false, $"Parked on {IntegrationBranchName}: {anchorReason}");
            }
        }
        finally
        {
            if (IsRegisteredWorktree(executionDirectory, tempPath))
            {
                RunGit(executionDirectory, "worktree", "remove", "--force", tempPath);
            }
        }

        try
        {
            GoalOperationJournal.RecordLandingIntent(
                executionDirectory,
                goal,
                goalBranch,
                IntegrationBranchName,
                candidateRevision!,
                "LandingExecutor",
                boundMainRevision: boundMainRevision,
                previousIntegrationRevision: previousIntegrationRevision);
        }
        catch
        {
            DeleteLandingAnchor(executionDirectory, goal, candidateRevision!);
            throw;
        }

        var publicationBlockReason = mutationBlocker?.Invoke();
        if (!string.IsNullOrWhiteSpace(publicationBlockReason))
        {
            GoalOperationJournal.TombstoneLandingIntent(
                executionDirectory,
                goal,
                $"landing publication blocked after intent write: {publicationBlockReason}");
            DeleteLandingAnchor(executionDirectory, goal, candidateRevision!);
            return BuildMutationBlockedResult(goal, goalPrefix, publicationBlockReason);
        }

        var integrationWrite = UpdateRef(executionDirectory, IntegrationBranchName, candidateRevision!, previousIntegrationRevision);
        if (integrationWrite.ExitCode != 0)
        {
            GoalOperationJournal.TombstoneLandingIntent(
                executionDirectory,
                goal,
                $"integration ref update failed after landing intent write: {FormatGitFailure(integrationWrite)}");
            DeleteLandingAnchor(executionDirectory, goal, candidateRevision!);
            var reason = $"integration ref update failed: {FormatGitFailure(integrationWrite)}";
            OperatorInbox.RecordLandingEscalation(workspace, goal, reason, IntegrationBranchName, channel);
            eventWriter?.AppendGoalEscalated(goal.Id, GoalLifecycleState.Verified, goal.Status, reason, IntegrationBranchName);
            return new LandingResult(goal.Id.Value, goalPrefix, new LandingDecision.Escalate(reason), IntegrationBranchName,
                false, $"Parked on {IntegrationBranchName}: {reason}");
        }

        var mainWrite = UpdateRef(executionDirectory, "main", candidateRevision!, boundMainRevision);
        if (mainWrite.ExitCode != 0)
        {
            var rollback = UpdateRef(executionDirectory, IntegrationBranchName, previousIntegrationRevision, candidateRevision!);
            var rollbackDetail = rollback.ExitCode == 0
                ? "integration ref restored"
                : $"integration ref rollback failed: {FormatGitFailure(rollback)}";
            GoalOperationJournal.TombstoneLandingIntent(
                executionDirectory,
                goal,
                $"main ref update failed after landing intent write: {FormatGitFailure(mainWrite)}; {rollbackDetail}");
            if (rollback.ExitCode == 0)
            {
                DeleteLandingAnchor(executionDirectory, goal, candidateRevision!);
            }
            var reason = $"main ref update failed: {FormatGitFailure(mainWrite)}; {rollbackDetail}";
            OperatorInbox.RecordLandingEscalation(workspace, goal, reason, IntegrationBranchName, channel);
            eventWriter?.AppendGoalEscalated(goal.Id, GoalLifecycleState.Verified, goal.Status, reason, IntegrationBranchName);
            return new LandingResult(goal.Id.Value, goalPrefix, new LandingDecision.Escalate(reason), IntegrationBranchName,
                false, $"Parked on {IntegrationBranchName}: {reason}");
        }

        DeleteLandingAnchor(executionDirectory, goal, candidateRevision!);

        var synchronizeWorktree = RunGit(
            executionDirectory,
            "read-tree",
            "-m",
            "-u",
            boundMainRevision,
            candidateRevision!);
        if (synchronizeWorktree.ExitCode != 0)
        {
            throw new InvalidOperationException($"main advanced to {candidateRevision}, but the execution worktree could not synchronize: {FormatGitFailure(synchronizeWorktree)}");
        }

        eventWriter?.AppendGoalLanded(goal.Id, IntegrationBranchName, goalBranch);
        OperatorInbox.ClearOwnershipHoldsAfterLanding(workspace, goal, $"land {goalPrefix}");
        StateEffectProposalApplier.ApplyLandedProposals(kernel, goal, workspace, changedFiles, Console.WriteLine);
        return new LandingResult(goal.Id.Value, goalPrefix, new LandingDecision.Promote(), IntegrationBranchName,
            true, $"Promoted: {goalBranch} prepared from bound main via {IntegrationBranchName} into main.",
            candidateRevision,
            changedFiles);
    }

    private static LandingResult? TryResolveCompletedLanding(
        Goal goal,
        OrchestratorWorkspace workspace,
        string goalPrefix,
        string goalBranch,
        IOperatorChannel? channel,
        IGoalLifecycleEventWriter? eventWriter)
    {
        var journal = GoalOperationJournal.Read(workspace.ExecutionDirectory, goal.Id);
        if (!GoalOperationJournal.HasCompletedLandingEvidence(journal))
        {
            return null;
        }

        var landingIntent = GoalOperationJournal.TryGetLatestLandingIntent(journal);
        var landingRevision = landingIntent?.MergeCommitSha ?? goalBranch;
        GitCli.GitResult reachability;
        try
        {
            reachability = RunGit(
                workspace.ExecutionDirectory,
                "merge-base",
                "--is-ancestor",
                landingRevision,
                "main");
        }
        catch (Exception exception)
        {
            return BuildPostLandingConfirmationFailure(
                goal,
                workspace,
                goalPrefix,
                channel,
                eventWriter,
                $"could not check whether revision {landingRevision} is reachable from main: {exception.Message}");
        }

        if (!reachability.Succeeded)
        {
            var detail = reachability.DrainTimedOut
                ? $"reachability check for revision {landingRevision} against main timed out while draining git output"
                : reachability.ExitCode == 1
                    ? $"revision {landingRevision} is not reachable from main"
                    : $"git merge-base --is-ancestor {landingRevision} main failed: {FormatGitFailure(reachability)}";
            return BuildPostLandingConfirmationFailure(
                goal,
                workspace,
                goalPrefix,
                channel,
                eventWriter,
                detail);
        }

        GoalOperationJournal.Completed(
            workspace.ExecutionDirectory,
            goal,
            "conductor:post-landing-skip",
            $"Completed landing evidence for {landingRevision} was confirmed reachable from main; " +
            "skipped redundant acceptance evaluation and merge.");
        return new LandingResult(
            goal.Id.Value,
            goalPrefix,
            new LandingDecision.Promote(),
            landingIntent?.IntegrationBranch ?? IntegrationBranchName,
            MainAdvanced: false,
            Message: $"Goal {goalPrefix} already landed; skipped redundant acceptance evaluation and merge.",
            MergeCommitSha: landingIntent?.MergeCommitSha,
            ChangedFiles: []);
    }

    private static LandingResult BuildPostLandingConfirmationFailure(
        Goal goal,
        OrchestratorWorkspace workspace,
        string goalPrefix,
        IOperatorChannel? channel,
        IGoalLifecycleEventWriter? eventWriter,
        string detail)
    {
        var reason = $"{PostLandingConfirmationReasonPrefix} {detail}";
        OperatorInbox.RecordLandingEscalation(
            workspace,
            goal,
            reason,
            IntegrationBranchName,
            channel);
        eventWriter?.AppendGoalEscalated(
            goal.Id,
            GoalLifecycleState.Merged,
            goal.Status,
            reason,
            IntegrationBranchName);
        return new LandingResult(
            goal.Id.Value,
            goalPrefix,
            new LandingDecision.Escalate(reason),
            IntegrationBranchName,
            MainAdvanced: false,
            Message: reason);
    }

    private static LandingResult? TryRecoverInterruptedLanding(
        AgentOrchestratorKernel kernel,
        Goal goal,
        OrchestratorWorkspace workspace,
        string goalPrefix,
        IOperatorChannel? channel,
        IGoalLifecycleEventWriter? eventWriter)
    {
        var intent = GoalOperationJournal.TryGetLatestLandingIntent(
            GoalOperationJournal.Read(workspace.ExecutionDirectory, goal.Id));
        if (intent is null)
        {
            return null;
        }

        var currentMain = ResolveRef(workspace.ExecutionDirectory, "main");
        var reachability = RunGit(
            workspace.ExecutionDirectory,
            "merge-base",
            "--is-ancestor",
            intent.MergeCommitSha,
            currentMain);
        if (reachability.DrainTimedOut)
        {
            return BuildPostLandingConfirmationFailure(
                goal,
                workspace,
                goalPrefix,
                channel,
                eventWriter,
                $"reachability check for recovered publication {intent.MergeCommitSha} timed out while draining git output");
        }

        if (reachability.Succeeded)
        {
            if (currentMain.Equals(intent.MergeCommitSha, StringComparison.OrdinalIgnoreCase))
            {
                var recoveryBaseline = string.IsNullOrWhiteSpace(intent.BoundMainRevision)
                    ? $"{intent.MergeCommitSha}^1"
                    : intent.BoundMainRevision;
                var synchronizeWorktree = RunGit(
                    workspace.ExecutionDirectory,
                    "read-tree",
                    "-m",
                    "-u",
                    recoveryBaseline,
                    intent.MergeCommitSha);
                if (synchronizeWorktree.ExitCode != 0)
                {
                    throw new InvalidOperationException(
                        $"main already contains recovered publication {intent.MergeCommitSha}, but the execution worktree could not synchronize: {FormatGitFailure(synchronizeWorktree)}");
                }
            }
            var changedFiles = ResolveRecoveredChangedFiles(
                workspace.ExecutionDirectory,
                intent.BoundMainRevision,
                intent.MergeCommitSha);
            GoalOperationJournal.Completed(
                workspace.ExecutionDirectory,
                goal,
                "conductor:landing-recovery",
                $"Recovered completed publication of {intent.MergeCommitSha} from durable landing intent.");
            StateEffectProposalApplier.ApplyLandedProposals(kernel, goal, workspace, changedFiles, Console.WriteLine);
            GoalOperationJournal.Completed(
                workspace.ExecutionDirectory,
                goal,
                "conductor:land",
                $"Recovered completed publication of {intent.MergeCommitSha} and reconciled its post-landing effects.");
            GoalOperationJournal.TombstoneLandingIntent(
                workspace.ExecutionDirectory,
                goal,
                "recovered completed publication was reconciled and its post-landing effects were applied");
            DeleteLandingAnchor(workspace.ExecutionDirectory, goal, intent.MergeCommitSha);
            return new LandingResult(
                goal.Id.Value,
                goalPrefix,
                new LandingDecision.Promote(),
                intent.IntegrationBranch,
                MainAdvanced: true,
                Message: $"Goal {goalPrefix} publication was recovered as already landed.",
                MergeCommitSha: intent.MergeCommitSha,
                ChangedFiles: changedFiles);
        }

        if (string.IsNullOrWhiteSpace(intent.BoundMainRevision))
        {
            return BuildPostLandingConfirmationFailure(
                goal,
                workspace,
                goalPrefix,
                channel,
                eventWriter,
                "incomplete landing intent lacks a bound main predecessor and was left unchanged for operator recovery");
        }

        if (!currentMain.Equals(intent.BoundMainRevision, StringComparison.OrdinalIgnoreCase))
        {
            return BuildPostLandingConfirmationFailure(
                goal,
                workspace,
                goalPrefix,
                channel,
                eventWriter,
                $"incomplete landing intent bound main {intent.BoundMainRevision}, but main is now {currentMain}; refs were left unchanged");
        }

        var currentIntegration = TryResolveBranch(workspace.ExecutionDirectory, intent.IntegrationBranch);
        var predecessorRestored = string.Equals(
            currentIntegration,
            intent.PreviousIntegrationRevision,
            StringComparison.OrdinalIgnoreCase);
        if (predecessorRestored)
        {
            GoalOperationJournal.TombstoneLandingIntent(
                workspace.ExecutionDirectory,
                goal,
                "interrupted landing intent found before integration publication; no ref recovery was required");
            DeleteLandingAnchor(workspace.ExecutionDirectory, goal, intent.MergeCommitSha);
            return null;
        }

        if (!string.Equals(currentIntegration, intent.MergeCommitSha, StringComparison.OrdinalIgnoreCase))
        {
            return BuildPostLandingConfirmationFailure(
                goal,
                workspace,
                goalPrefix,
                channel,
                eventWriter,
                $"incomplete landing intent expected integration {intent.MergeCommitSha}, but found {currentIntegration ?? "<absent>"}; refs were left unchanged");
        }

        var rollback = UpdateRef(
            workspace.ExecutionDirectory,
            intent.IntegrationBranch,
            intent.PreviousIntegrationRevision,
            intent.MergeCommitSha);
        if (rollback.ExitCode != 0)
        {
            return BuildPostLandingConfirmationFailure(
                goal,
                workspace,
                goalPrefix,
                channel,
                eventWriter,
                $"incomplete landing integration rollback failed: {FormatGitFailure(rollback)}");
        }

        GoalOperationJournal.TombstoneLandingIntent(
            workspace.ExecutionDirectory,
            goal,
            "interrupted landing recovery restored the recorded integration predecessor");
        DeleteLandingAnchor(workspace.ExecutionDirectory, goal, intent.MergeCommitSha);
        return null;
    }

    private static string FormatGitFailure(GitCli.GitResult result) =>
        string.IsNullOrWhiteSpace(result.Error)
            ? $"git exited {result.ExitCode}"
            : result.Error.Trim();

    internal static AcceptanceCohortLandingResult ExecuteCohort(
        AgentOrchestratorKernel kernel,
        IReadOnlyList<Goal> goals,
        OrchestratorWorkspace workspace,
        AcceptanceCohortReceipt receipt,
        string combinedCommitRevision,
        CohortAcceptanceStore store,
        ConductorAutonomyPolicy policy,
        IGoalLifecycleEventWriter? eventWriter = null,
        Func<string?>? mutationBlocker = null)
    {
        ArgumentNullException.ThrowIfNull(goals);
        ArgumentNullException.ThrowIfNull(receipt);
        ArgumentNullException.ThrowIfNull(store);
        if (goals.Count != 2 || goals[0].Id == goals[1].Id || receipt.Identity.Members.Count != 2)
        {
            throw new ArgumentException("Cohort landing requires exactly two distinct bound goals.", nameof(goals));
        }
        if (!receipt.HasAuthoritativeLandingEvidence)
        {
            throw new InvalidOperationException(
                "Only an exact passing cohort receipt with successful exit and extant coherent TRX evidence can authorize landing.");
        }

        var executionDirectory = workspace.ExecutionDirectory;
        var blockReason = mutationBlocker?.Invoke();
        if (!string.IsNullOrWhiteSpace(blockReason))
        {
            return new AcceptanceCohortLandingResult(
                AcceptanceCohortLandingOutcome.RetryableHold,
                $"Landing held at mutation boundary: {blockReason}");
        }

        var liveMain = GoalWorktrees.ResolveRequiredRef(executionDirectory, "refs/heads/main");
        if (!liveMain.Equals(receipt.Identity.ObservedMainRevision, StringComparison.Ordinal))
        {
            return new AcceptanceCohortLandingResult(
                AcceptanceCohortLandingOutcome.StateInvalidated,
                "Cohort receipt invalidated because main changed after the gate.");
        }
        var commit = AcceptanceCohortMemberBinding.NormalizeRevision(
            combinedCommitRevision,
            nameof(combinedCommitRevision));
        var tree = GoalWorktrees.ResolveRequiredRef(executionDirectory, $"{commit}^{{tree}}");
        if (!tree.Equals(receipt.Identity.CombinedTreeRevision, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Cohort landing commit does not contain the exact tested tree.");
        }
        for (var index = 0; index < goals.Count; index++)
        {
            var member = receipt.Identity.Members[index];
            if (goals[index].Id != member.GoalId)
            {
                throw new InvalidOperationException("Cohort goal order differs from the tested receipt.");
            }
            var liveBranch = GoalWorktrees.ResolveRequiredRef(
                executionDirectory,
                $"refs/heads/{GoalWorktrees.BranchName(member.GoalId)}");
            if (!liveBranch.Equals(member.BranchRevision, StringComparison.Ordinal) ||
                !liveBranch.Equals(member.CandidateRevision, StringComparison.Ordinal))
            {
                return new AcceptanceCohortLandingResult(
                    AcceptanceCohortLandingOutcome.StateInvalidated,
                    $"Cohort receipt invalidated because goal {member.GoalId.Value[..8]} changed after the gate.");
            }
        }

        var evidenceDiagnostic = AcceptanceCriterionEvidence.RebindRecordAndDescribeOutstanding(
            goals,
            goalId => receipt.Identity.Members.Single(member => member.GoalId == goalId).CandidateRevision,
            kernel,
            $"cohort-receipt:{receipt.ReceiptId}",
            executionDirectory);
        if (evidenceDiagnostic is not null)
        {
            return new AcceptanceCohortLandingResult(
                AcceptanceCohortLandingOutcome.RetryableHold,
                $"Cohort landing held before mutation because {evidenceDiagnostic}");
        }

        var changedFiles = receipt.Identity.Members
            .SelectMany(member => member.LandingPaths)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var ownership = RepositoryOwnershipMap.GuardWriteSet(changedFiles);
        if (ownership.RequiresOperatorApproval && !policy.AllowsAutonomousHighRiskOwnership)
        {
            return new AcceptanceCohortLandingResult(
                AcceptanceCohortLandingOutcome.RetryableHold,
                "Cohort landing requires operator approval for ownership-protected paths.");
        }

        var currentIntegration = RunGit(
            executionDirectory,
            "rev-parse", "--verify", "--quiet", $"refs/heads/{IntegrationBranchName}");
        string? priorIntegrationRevision = null;
        if (currentIntegration.ExitCode == 0 && !string.IsNullOrWhiteSpace(currentIntegration.Output))
        {
            priorIntegrationRevision = currentIntegration.Output.Trim();
            if (RunGit(executionDirectory, "merge-base", "--is-ancestor", priorIntegrationRevision, liveMain).ExitCode != 0)
            {
                return new AcceptanceCohortLandingResult(
                    AcceptanceCohortLandingOutcome.RetryableHold,
                    "Cohort landing held because the integration branch contains state not present on bound main.");
            }
        }

        // The prepared intent captures the exact integration ref predecessor before either ref moves.
        // Recovery can therefore restore integration when the process exits before the main CAS, or
        // replay post-main lifecycle effects when the tested commit is already reachable from main.
        store.PrepareLanding(receipt, commit, priorIntegrationRevision);
        foreach (var goal in goals)
        {
            GoalOperationJournal.RecordLandingIntent(
                executionDirectory,
                goal,
                GoalWorktrees.BranchName(goal.Id),
                $"cohort/{receipt.Identity.Value}",
                commit,
                "LandingExecutor.ExecuteCohort",
                boundMainRevision: liveMain,
                previousIntegrationRevision: priorIntegrationRevision);
        }

        blockReason = mutationBlocker?.Invoke();
        if (!string.IsNullOrWhiteSpace(blockReason))
        {
            foreach (var goal in goals)
            {
                GoalOperationJournal.TombstoneLandingIntent(
                    executionDirectory,
                    goal,
                    $"cohort landing mutation blocked after intent write: {blockReason}");
            }
            return new AcceptanceCohortLandingResult(
                AcceptanceCohortLandingOutcome.RetryableHold,
                $"Landing held at mutation boundary: {blockReason}");
        }

        if (priorIntegrationRevision is not null)
        {
            var advanceIntegration = RunGit(
                executionDirectory,
                "update-ref",
                $"refs/heads/{IntegrationBranchName}",
                commit,
                priorIntegrationRevision);
            if (advanceIntegration.ExitCode != 0)
            {
                foreach (var goal in goals)
                {
                    GoalOperationJournal.TombstoneLandingIntent(
                        executionDirectory,
                        goal,
                        $"cohort integration ref update failed: {advanceIntegration.Error}");
                }
                return new AcceptanceCohortLandingResult(
                    AcceptanceCohortLandingOutcome.RetryableHold,
                    $"Cohort integration ref update failed: {advanceIntegration.Error}");
            }
        }
        else
        {
            var createIntegration = RunGit(
                executionDirectory,
                "update-ref",
                $"refs/heads/{IntegrationBranchName}",
                commit);
            if (createIntegration.ExitCode != 0)
            {
                foreach (var goal in goals)
                {
                    GoalOperationJournal.TombstoneLandingIntent(
                        executionDirectory,
                        goal,
                        $"cohort integration ref creation failed: {createIntegration.Error}");
                }
                return new AcceptanceCohortLandingResult(
                    AcceptanceCohortLandingOutcome.RetryableHold,
                    $"Cohort integration ref creation failed: {createIntegration.Error}");
            }
        }

        var advanceMain = RunGit(
            executionDirectory,
            "update-ref",
            "refs/heads/main",
            commit,
            receipt.Identity.ObservedMainRevision);
        if (advanceMain.ExitCode != 0)
        {
            var rollbackIntegration = priorIntegrationRevision is null
                ? RunGit(
                    executionDirectory,
                    "update-ref", "-d", $"refs/heads/{IntegrationBranchName}", commit)
                : RunGit(
                    executionDirectory,
                    "update-ref",
                    $"refs/heads/{IntegrationBranchName}",
                    priorIntegrationRevision,
                    commit);
            var rollbackDetail = rollbackIntegration.ExitCode == 0
                ? "integration ref restored"
                : $"integration ref rollback failed: {rollbackIntegration.Error}";
            foreach (var goal in goals)
            {
                GoalOperationJournal.TombstoneLandingIntent(
                    executionDirectory,
                    goal,
                    $"cohort main compare-and-swap failed: {advanceMain.Error}; {rollbackDetail}");
            }
            return new AcceptanceCohortLandingResult(
                rollbackIntegration.ExitCode == 0
                    ? AcceptanceCohortLandingOutcome.StateInvalidated
                    : AcceptanceCohortLandingOutcome.RetryableHold,
                $"Cohort receipt invalidated because main changed at the compare-and-swap boundary: {advanceMain.Error}; {rollbackDetail}.");
        }
        var refreshCheckout = RunGit(
            executionDirectory,
            "read-tree", "-m", "-u", receipt.Identity.ObservedMainRevision, commit);
        if (refreshCheckout.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Main advanced to the exact cohort commit, but its checked-out files could not be refreshed: {refreshCheckout.Error}");
        }

        var coverage = store.FinalizeLanding(receipt.Identity.Value, receipt.ReceiptId);
        foreach (var goal in goals)
        {
            GoalOperationJournal.Completed(
                executionDirectory,
                goal,
                "conductor:land",
                $"Shared cohort receipt {receipt.ReceiptId} landed exact tree {receipt.Identity.CombinedTreeRevision}.");
            eventWriter?.AppendGoalLanded(
                goal.Id,
                $"cohort/{receipt.Identity.Value}",
                GoalWorktrees.BranchName(goal.Id));
            StateEffectProposalApplier.ApplyLandedProposals(kernel, goal, workspace, changedFiles, Console.WriteLine);
        }
        return new AcceptanceCohortLandingResult(
            AcceptanceCohortLandingOutcome.Advanced,
            $"Landed exact tested cohort tree for {goals[0].Id.Value[..8]},{goals[1].Id.Value[..8]}.",
            commit,
            coverage);
    }

    private static bool TryPrepareCandidateFromBoundMain(
        Goal goal,
        string executionDirectory,
        string tempPath,
        string boundMainRevision,
        string goalBranch,
        out string? candidateRevision,
        out string? failure)
    {
        candidateRevision = null;
        failure = null;
        var add = RunGit(executionDirectory, "worktree", "add", "--detach", tempPath, boundMainRevision);
        if (add.ExitCode != 0)
        {
            failure = $"could not create landing preparation worktree at '{tempPath}': {FormatGitFailure(add)}";
            return false;
        }

        var mergeArgs = new List<string> { "merge", "--no-ff", goalBranch, "-m", $"Integrate {goalBranch}" };
        var goalTitleBody = OrchestratorCommitMessage.GoalTitleBody(goal);
        if (goalTitleBody is not null)
        {
            mergeArgs.AddRange(["-m", goalTitleBody]);
        }

        var merge = RunGit(tempPath, mergeArgs.ToArray());
        if (merge.ExitCode != 0)
        {
            failure = FormatGitFailure(merge);
            return false;
        }

        candidateRevision = ResolveRef(tempPath, "HEAD");
        return true;
    }

    private static LandingResult BuildMutationBlockedResult(
        Goal goal,
        string goalPrefix,
        string blockReason)
    {
        var reason = $"{MutationHoldReasonPrefix} {blockReason}";
        return new LandingResult(
            goal.Id.Value,
            goalPrefix,
            new LandingDecision.Escalate(reason),
            IntegrationBranchName,
            MainAdvanced: false,
            $"Landing held before merge: {blockReason}");
    }

    private static string? TryResolveBranch(string executionDirectory, string branch)
    {
        var result = RunGit(executionDirectory, "rev-parse", "--verify", "--quiet", $"refs/heads/{branch}");
        return result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.Output)
            ? result.Output.Trim()
            : null;
    }

    private static GitCli.GitResult UpdateRef(
        string executionDirectory,
        string branch,
        string? newRevision,
        string? expectedRevision)
    {
        var reference = $"refs/heads/{branch}";
        if (newRevision is null)
        {
            return RunGit(executionDirectory, "update-ref", "-d", reference, expectedRevision!);
        }

        return RunGit(
            executionDirectory,
            "update-ref",
            reference,
            newRevision,
            expectedRevision ?? string.Empty);
    }

    private static GitCli.GitResult CreateLandingAnchor(
        string executionDirectory,
        Goal goal,
        string candidateRevision) =>
        RunGit(
            executionDirectory,
            "update-ref",
            LandingAnchorReference(goal),
            candidateRevision,
            string.Empty);

    private static void DeleteLandingAnchor(
        string executionDirectory,
        Goal goal,
        string candidateRevision)
    {
        _ = RunGit(
            executionDirectory,
            "update-ref",
            "-d",
            LandingAnchorReference(goal),
            candidateRevision);
    }

    private static string LandingAnchorReference(Goal goal) =>
        $"{LandingAnchorRefPrefix}{goal.Id.Value}";

    private static IReadOnlyList<string> ResolveRecoveredChangedFiles(
        string executionDirectory,
        string? boundMainRevision,
        string candidateRevision)
    {
        var baseline = string.IsNullOrWhiteSpace(boundMainRevision)
            ? $"{candidateRevision}^1"
            : boundMainRevision;
        var diff = RunGit(executionDirectory, "diff", "--name-only", baseline, candidateRevision);
        if (!diff.Succeeded)
        {
            return [];
        }

        return diff.Output
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
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

    internal static bool IsOwnershipHoldEscalation(string reason) =>
        reason.StartsWith(OwnershipHoldReasonPrefix, StringComparison.OrdinalIgnoreCase);

    internal static bool IsMutationHoldEscalation(string reason) =>
        reason.StartsWith(MutationHoldReasonPrefix, StringComparison.OrdinalIgnoreCase);

    internal static bool IsPostLandingConfirmationEscalation(string reason) =>
        reason.StartsWith(PostLandingConfirmationReasonPrefix, StringComparison.OrdinalIgnoreCase);

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
