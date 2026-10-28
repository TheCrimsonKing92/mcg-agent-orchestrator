using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

/// <summary>Selects a unique upstream Developer with committed output evidence for Tester recovery.</summary>
internal static class TesterFindingDeveloperTargetPolicy
{
    public static TaskSpec? Select(Goal goal, TaskSpec verifyingTask)
    {
        var upstreamDevelopers = goal.Tasks
            .TakeWhile(candidate => candidate.Id != verifyingTask.Id)
            .Where(candidate =>
                candidate.RequiredRole == AgentRole.Developer &&
                HasEverCommittedOutput(goal, candidate, verifyingTask))
            .Take(2)
            .ToArray();
        return upstreamDevelopers.Length == 1 ? upstreamDevelopers[0] : null;
    }

    private static bool HasEverCommittedOutput(Goal goal, TaskSpec task, TaskSpec verifyingTask) =>
        task.VerificationHistory.Any(verification => verification.HasCommittedChanges) ||
        task.DispatchHistory.Any(dispatch =>
            !string.IsNullOrWhiteSpace(dispatch.ResultCommit) &&
            (string.IsNullOrWhiteSpace(dispatch.BaseCommit) ||
             !string.Equals(dispatch.BaseCommit, dispatch.ResultCommit, StringComparison.OrdinalIgnoreCase))) ||
        // A failed round can be committed at the pre-dispatch checkpoint boundary. Its original
        // verification and ResultCommit stay unchanged; the typed decision owns that output.
        // Bind it to its dispatch, and to a successful Developer round on the reviewed candidate,
        // rather than treating a Completed label or an arbitrary historical commit as ownership.
        (task.LastVerification?.Succeeded == true &&
         task.LastDispatch is { } currentDispatch &&
         verifyingTask.LastVerification is { ReviewedCommit: { } reviewedCommit } findingRecord &&
         !string.IsNullOrWhiteSpace(reviewedCommit) &&
         currentDispatch.DispatchedAt >= task.LatestRetryAt.GetValueOrDefault() &&
         task.LastVerification.CompletedAt >= currentDispatch.DispatchedAt &&
         task.LastVerification.CompletedAt <= findingRecord.CompletedAt &&
         string.Equals(currentDispatch.ResultCommit, reviewedCommit, StringComparison.OrdinalIgnoreCase) &&
         task.DispatchHistory.Any(dispatch =>
             dispatch.FailedRoundCheckpointDecision is
                 { Outcome: FailedRoundCheckpointOutcome.Committed, CommitSha: { } commitSha } decision &&
             !string.IsNullOrWhiteSpace(commitSha) &&
             !string.Equals(dispatch.BaseCommit, commitSha, StringComparison.OrdinalIgnoreCase) &&
             dispatch.DispatchedAt <= currentDispatch.DispatchedAt &&
             decision.DispatchId == DispatchIdentity.BuildId(goal.Id, task.Id, dispatch)));
}
