using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorBatchLoop
{
    internal static LiveAcceptanceAdmissionDecision DecideFallbackAcceptanceAdmission(
        ConductorParallelAcceptanceAttemptCoordinator coordinator,
        IReadOnlyCollection<Goal> goals,
        ConductorAcceptanceCapacitySnapshot activeCohorts,
        string goalId,
        int width,
        int tick)
    {
        var reservations = BuildActiveParallelAcceptanceReservations(coordinator, goals);
        if (reservations.Failure is { } capacityFailure)
        {
            return new LiveAcceptanceAdmissionDecision(
                false,
                $"acceptance capacity state unavailable; retry on next conduct tick: {SanitizeReason(capacityFailure.Message)}");
        }

        var census = CaptureLiveAcceptanceCensus(
            reservations.Attempts,
            reservations.AttemptIds,
            activeCohorts,
            tick,
            new List<string>(),
            blockAdmissionOnFailure: true);
        if (census.CaptureFailure is null &&
            census.Occupants.Contains($"goal:{ShortIdentity(goalId)}", StringComparer.OrdinalIgnoreCase))
        {
            return new LiveAcceptanceAdmissionDecision(true, string.Empty);
        }

        return DecideLiveAcceptanceAdmission(census, width);
    }

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
