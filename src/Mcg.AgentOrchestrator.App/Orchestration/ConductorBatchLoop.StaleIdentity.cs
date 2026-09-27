using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorBatchLoop
{
    internal const string IdentityStaleDisposition = "identity-stale";
    private const int IdentityStaleRegateCap = 2;

    private static bool IsIdentityStaleRun(ConductorParallelAcceptanceRunResult run) =>
        run.Exception is AcceptanceExecutionIdentityChangedException { IsChangedIdentity: true };

    private static bool ReconcileIdentityStaleAcceptance(
        AgentOrchestratorKernel kernel,
        Goal goal,
        ConductorParallelAcceptanceRunResult run,
        ConductorParallelAcceptanceAttempt attempt)
    {
        if (!IsIdentityStaleRun(run))
        {
            kernel.ResetAcceptanceIdentityStale(goal.Id);
            return false;
        }

        if (goal.Status != GoalStatus.Verifying)
        {
            return false;
        }

        var count = kernel.RecordAcceptanceIdentityStale(goal.Id, attempt.AttemptId);
        if (count > IdentityStaleRegateCap)
        {
            return false;
        }

        kernel.ReconcileGoalAcceptanceVerified(
            goal.Id,
            $"Acceptance run '{attempt.AttemptId}' went stale because main moved during the run; " +
            $"goal returned to Verified for re-gating ({count}/{IdentityStaleRegateCap}).");
        return true;
    }

    private static bool IsIdentityStaleRegated(
        ConductorParallelAcceptanceRunResult run,
        ConductorParallelAcceptanceAttempt attempt) =>
        IsIdentityStaleRun(run) &&
        run.Candidate.Goal.Status == GoalStatus.Verified &&
        run.Candidate.Goal.LastAcceptanceIdentityStaleAttemptId == attempt.AttemptId &&
        run.Candidate.Goal.ConsecutiveAcceptanceIdentityStaleCount is > 0 and <= IdentityStaleRegateCap;

    private static ConductorAdvanceResult CompleteIdentityStaleRun(
        ConductorDriver driver,
        ConductorAutonomyPolicy policy,
        ConductorParallelAcceptanceRunResult run,
        ConductorParallelAcceptanceAttempt attempt)
    {
        if (IsIdentityStaleRegated(run, attempt))
        {
            return ParallelAcceptanceHeld(
                run.Candidate,
                policy,
                $"Acceptance run '{attempt.AttemptId}' identity-stale; re-gate on next admission " +
                $"({run.Candidate.Goal.ConsecutiveAcceptanceIdentityStaleCount}/{IdentityStaleRegateCap}).");
        }

        var count = run.Candidate.Goal.ConsecutiveAcceptanceIdentityStaleCount;
        if (run.Candidate.Goal.LastAcceptanceIdentityStaleAttemptId == attempt.AttemptId &&
            count > IdentityStaleRegateCap)
        {
            return driver.EscalateParallelLandingAcceptance(
                run.Candidate,
                policy,
                $"parallel acceptance fault: identity-stale {count} consecutive results " +
                $"(attempt {attempt.AttemptId}): {SanitizeReason(run.Exception!.Message)}");
        }

        return ParallelAcceptanceFault(driver, run.Candidate, policy, attempt, run.Exception!);
    }
}
