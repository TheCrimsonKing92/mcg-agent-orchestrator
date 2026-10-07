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
        string? currentMain = null;
        var mainResolved = false;
        foreach (var goal in kernel.Goals.Where(goal =>
                     goal.Id.Value != landedGoalId && goal.Status != GoalStatus.Completed))
        {
            IReadOnlyList<ConductorParallelAcceptanceAttempt> attempts;
            try
            {
                attempts = driver.ParallelAcceptanceAttemptCoordinator.GetSupersedableGateAttempts(
                    [goal.Id.Value]);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                EmitProgress($"SUPERSEDED_GATE_STOP_FAILED goal={goal.Id.Value} error={SanitizeReason(ex.Message)}");
                continue;
            }

            foreach (var attempt in attempts)
            {
                try
                {
                    if (!mainResolved)
                    {
                        currentMain = driver.ResolveCurrentMainHeadSha(goal);
                        mainResolved = true;
                    }
                    if (attempt.MainHeadSha is null || currentMain is null ||
                        string.Equals(attempt.MainHeadSha, currentMain, StringComparison.Ordinal))
                        continue;

                    var stop = SupersededAttemptStop ??
                        driver.ParallelAcceptanceAttemptCoordinator.RequestSupersededMainStop;
                    stop(attempt, currentMain);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    EmitProgress($"SUPERSEDED_GATE_STOP_FAILED goal={goal.Id.Value} attempt={attempt.AttemptId} error={SanitizeReason(ex.Message)}");
                }
            }
        }
        try
        {
            driver.StopInvalidatedFollowerGates();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            EmitProgress($"FOLLOWER_GATE_STOP_FAILED error={SanitizeReason(ex.Message)}");
        }
    }
}
