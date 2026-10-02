using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorBatchLoop
{
    internal static void TrackGoalOutcome(
        AgentOrchestratorKernel kernel,
        ConductorDriver driver,
        Goal goal,
        ConductorAdvanceOutcome outcome,
        DateTimeOffset observedAt,
        TimeSpan stallThreshold,
        HashSet<GoalId> changedGoalIds,
        List<string> tickLines)
    {
        if (outcome is ConductorAdvanceOutcome.Held held)
        {
            if (driver.SliceBatchParentExecutionGuard?.IsSliceBatchParent(goal) == true)
            {
                ClearGoalHold(kernel, goal, changedGoalIds);
                return;
            }

            if (held.State is GoalLifecycleState.Running
                or GoalLifecycleState.AwaitingVerification
                or GoalLifecycleState.Verifying)
            {
                ClearGoalHold(kernel, goal, changedGoalIds);
                return;
            }

            if (held.Owner is ConductorHoldOwner.BackgroundAttempt or ConductorHoldOwner.AcceptanceQueue)
            {
                ClearGoalHold(kernel, goal, changedGoalIds);
                return;
            }

            TrackGoalHold(
                kernel,
                goal,
                held.State.ToString(),
                held.Reason,
                observedAt,
                stallThreshold,
                changedGoalIds,
                tickLines,
                held.StableIdentity);
            return;
        }

        ClearGoalHold(kernel, goal, changedGoalIds);
    }

    private static void TrackGoalHold(
        AgentOrchestratorKernel kernel,
        Goal goal,
        string state,
        string blocker,
        DateTimeOffset observedAt,
        TimeSpan stallThreshold,
        HashSet<GoalId> changedGoalIds,
        List<string> tickLines,
        string? stableIdentity = null)
    {
        try
        {
            var observation = kernel.ObserveGoalHold(
                goal.Id,
                state,
                blocker,
                observedAt,
                stallThreshold,
                stableIdentity);
            if (observation.StateChanged)
            {
                changedGoalIds.Add(goal.Id);
            }

            if (!observation.BecameStalled)
            {
                return;
            }

            var repeatedForSeconds = Math.Max(
                0,
                (long)(observedAt - observation.Hold.StartedAt).TotalSeconds);
            EmitProgress(
                $"GOAL_STALLED goal={goal.Id.Value[..8]} state={Sanitize(state)} owner=none " +
                $"repeatedForSeconds={repeatedForSeconds} blocker={FormatStalledBlockerDetail(blocker)}",
                tickLines);
        }
        catch (Exception ex)
        {
            // The watchdog is diagnostic safety infrastructure. A persistence or event-stream
            // failure here must not take down the conductor loop it is meant to protect.
            try
            {
                Console.Error.WriteLine(
                    $"GOAL_STALL_TRACKING_FAILED goal={goal.Id.Value[..8]} " +
                    $"exception={ex.GetType().Name} message={SanitizeHandoffDetail(ex.Message)}");
                Console.Error.Flush();
            }
            catch
            {
                // Console diagnostics are best effort during fault isolation.
            }
        }
    }

    private static void ClearGoalHold(
        AgentOrchestratorKernel kernel,
        Goal goal,
        HashSet<GoalId> changedGoalIds)
    {
        if (kernel.ClearGoalHold(goal.Id))
        {
            changedGoalIds.Add(goal.Id);
        }
    }
}
