using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorBatchLoop
{
    private static void ReconcileParallelAcceptanceTerminalStateUnlessReused(
        AgentOrchestratorKernel kernel,
        Goal goal,
        ConductorParallelAcceptanceRunResult run,
        ConductorParallelAcceptanceAttempt attempt)
    {
        if (attempt.ReconciledAt.HasValue)
        {
            if (attempt.Outcome != ConductorParallelAcceptanceAttemptOutcome.Passed)
            {
                throw new InvalidOperationException(
                    $"Only a passed acceptance attempt may be reused after reconciliation: {attempt.AttemptId}.");
            }

            return;
        }

        ReconcileParallelAcceptanceTerminalState(kernel, goal, run, attempt);
    }
}
