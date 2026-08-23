using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorBatchLoop
{
    internal static bool MarkParallelAcceptanceStarted(
        AgentOrchestratorKernel kernel,
        Goal goal,
        ConductorParallelAcceptanceAttempt attempt,
        int tick)
    {
        if (goal.Status == GoalStatus.Completed &&
            !kernel.NormalizePrematureCompletedGoalToVerified(
                goal.Id,
                $"Batch loop tick {tick}: background acceptance gate {attempt.AttemptId} repaired premature Completed state before verification."))
        {
            return false;
        }

        if (goal.Status != GoalStatus.Verified)
        {
            return false;
        }

        return kernel.BeginGoalAcceptanceVerification(
            goal.Id,
            $"Batch loop tick {tick}: acceptance gate record {attempt.AttemptId} is running in background; goal entered Verifying.");
    }
}
