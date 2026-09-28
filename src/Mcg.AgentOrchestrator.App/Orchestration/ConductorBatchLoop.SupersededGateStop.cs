using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorBatchLoop
{
    internal Func<ConductorParallelAcceptanceAttempt, string, bool>? SupersededAttemptStop { get; init; }

    private void NoteLandingAndStopSupersededAttempts(
        AgentOrchestratorKernel kernel,
        ConductorDriver driver,
        string landedGoalId)
    {
        NoteLandingForTick(landedGoalId);
        var goals = kernel.Goals.Where(goal => goal.Id.Value != landedGoalId).ToArray();
        foreach (var attempt in driver.ParallelAcceptanceAttemptCoordinator.GetSupersedableGateAttempts(
                     goals.Select(goal => goal.Id.Value)))
        {
            var goal = goals.Single(goal => goal.Id.Value == attempt.GoalId);
            var currentMain = driver.ResolveCurrentMainHeadSha(goal);
            if (attempt.MainHeadSha is null || currentMain is null ||
                string.Equals(attempt.MainHeadSha, currentMain, StringComparison.Ordinal))
                continue;

            var stop = SupersededAttemptStop ??
                driver.ParallelAcceptanceAttemptCoordinator.RequestSupersededMainStop;
            stop(attempt, currentMain);
        }
    }
}
