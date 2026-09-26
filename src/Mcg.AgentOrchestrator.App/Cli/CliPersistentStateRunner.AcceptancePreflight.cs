using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static partial class CliPersistentStateRunner
{
    private static void ReplayAcceptancePreflightRepairs(
        GoalSnapshot initialSnapshot,
        AgentOrchestratorKernel proposedKernel,
        AgentOrchestratorKernel currentKernel,
        GoalId goalId,
        string? currentBranchHeadSha,
        string? currentMainHeadSha)
    {
        var initialKernel = KernelFromGoalSnapshot(initialSnapshot);
        var initialGoal = initialKernel.GetGoal(goalId);
        var proposedGoal = proposedKernel.GetGoal(goalId);
        var currentGoal = currentKernel.GetGoal(goalId);

        foreach (var proposedTask in proposedGoal.Tasks)
        {
            var initialTask = initialGoal.Tasks.Single(task => task.Id == proposedTask.Id);
            if (!SameAcceptancePreflightValue(initialTask.LastVerification, proposedTask.LastVerification) &&
                proposedTask.LastVerification is { } verification)
            {
                currentKernel.RecordTaskVerification(goalId, proposedTask.Id, verification);
            }
        }

        if (initialGoal.Status == GoalStatus.AcceptanceFailed &&
            proposedGoal.Status == GoalStatus.Verified &&
            proposedGoal.LatestAcceptanceFailure is null)
        {
            currentKernel.RestoreVerifiedForSupersededAcceptanceFailure(
                goalId,
                currentBranchHeadSha,
                currentMainHeadSha);
        }
        else if (initialGoal.Status == GoalStatus.Completed && proposedGoal.Status == GoalStatus.Verified)
        {
            currentKernel.NormalizePrematureCompletedGoalForAcceptance(
                goalId,
                "acceptance: persisted the Completed-to-Verified repair before verification.");
        }
        else if (initialGoal.Status == GoalStatus.Verifying && proposedGoal.Status == GoalStatus.Verified)
        {
            currentKernel.ReconcileGoalAcceptanceVerified(
                goalId,
                "acceptance: persisted the Verifying-to-Verified repair before verification.");
        }
        else if (proposedGoal.Status == GoalStatus.Verified && currentKernel.GetGoal(goalId).Status != GoalStatus.Verified)
        {
            currentKernel.ReconcileGoalVerificationStatus(
                goalId,
                "acceptance: persisted task verification repairs before verification.");
        }

        if (proposedGoal.LatestAcceptanceFailure is null &&
            initialGoal.LatestAcceptanceFailure is not null &&
            SameAcceptancePreflightValue(currentGoal.LatestAcceptanceFailure, initialGoal.LatestAcceptanceFailure))
        {
            currentKernel.ClearAcceptanceFailure(goalId);
        }
    }

    private static bool SameAcceptancePreflightValue<T>(T left, T right) =>
        string.Equals(JsonSerializer.Serialize(left), JsonSerializer.Serialize(right), StringComparison.Ordinal);
}
