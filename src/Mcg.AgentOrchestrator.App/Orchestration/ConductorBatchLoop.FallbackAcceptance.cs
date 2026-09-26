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
        if (goal.Status == GoalStatus.Verifying)
            return false;

        if (goal.Status != GoalStatus.Verified)
            throw new InvalidOperationException(
                $"Background acceptance attempt {attempt.AttemptId} cannot start for goal {goal.Id} in {goal.Status} state.");

        return kernel.BeginGoalAcceptanceVerification(
            goal.Id,
            $"Batch loop tick {tick}: acceptance gate record {attempt.AttemptId} is running in background; goal entered Verifying.");
    }
}
