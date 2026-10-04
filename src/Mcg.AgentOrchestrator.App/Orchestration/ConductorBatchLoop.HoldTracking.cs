using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorBatchLoop
{
    internal static ConductorAdvanceResult CohortMemberHeld(
        Goal goal, ConductorAutonomyPolicy policy, string detail) =>
        ParallelAcceptanceHeld(goal, policy,
            $"Acceptance cohort gate owns this member: {detail}", ConductorHoldOwner.BackgroundAttempt);

    internal static void TrackGoalOutcome(
        AgentOrchestratorKernel kernel,
        ConductorDriver driver,
        Goal goal,
        ConductorAdvanceOutcome outcome,
        DateTimeOffset observedAt,
        TimeSpan stallThreshold,
        HashSet<GoalId> changedGoalIds,
        List<string> tickLines,
        ConductEventLogWriter? conductEventLogWriter = null)
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

            if (held.Owner is ConductorHoldOwner.BackgroundAttempt or ConductorHoldOwner.AcceptanceQueue
                or ConductorHoldOwner.DurableOutbox or ConductorHoldOwner.WorkerCapacity)
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
                held.StableIdentity,
                driver,
                conductEventLogWriter);
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
        string? stableIdentity = null,
        ConductorDriver? driver = null,
        ConductEventLogWriter? conductEventLogWriter = null)
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
            RaiseOwnerlessStallEscalation(
                goal, state, blocker, repeatedForSeconds, driver,
                conductEventLogWriter ?? CurrentConductEventLogWriter.Value);
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

    private static void RaiseOwnerlessStallEscalation(
        Goal goal, string state, string blocker, long heldForSeconds,
        ConductorDriver? driver, ConductEventLogWriter? conduct)
    {
        var text = $"ownerless-hold-stalled state={Sanitize(state)} heldForSeconds={heldForSeconds} " +
            $"blocker={blocker.Replace('\r', ' ').Replace('\n', ' ')}";
        var lifecycleState = Enum.TryParse<GoalLifecycleState>(state, out var parsed)
            ? parsed : GoalLifecycleState.Blocked;
        // Each channel is independent: a failed conduct append must not suppress the timeline.
        TryWrite(() => conduct?.Append("goal-escalation", goal.Id.Value, text));
        TryWrite(() => driver?.HoldEscalationEventWriter?.AppendGoalEscalated(
            goal.Id, lifecycleState, goal.Status, text, "ownerless-hold-stall"));

        void TryWrite(Action write)
        {
            try { write(); }
            catch (Exception ex)
            {
                try
                {
                    Console.Error.WriteLine($"GOAL_STALL_ESCALATION_FAILED goal={goal.Id.Value[..8]} " +
                        $"exception={ex.GetType().Name} message={SanitizeHandoffDetail(ex.Message)}");
                }
                catch { /* Console diagnostics are best effort during fault isolation. */ }
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
