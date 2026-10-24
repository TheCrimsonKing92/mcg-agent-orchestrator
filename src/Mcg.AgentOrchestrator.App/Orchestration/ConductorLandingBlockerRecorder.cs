using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class ConductorLandingBlockerRecorder
{
    private static readonly IReadOnlyDictionary<string, (string Actor, string Verb)> NextActions =
        new Dictionary<string, (string, string)>(StringComparer.Ordinal)
        {
            ["criterion-evidence-hold"] = ("evidence-owner", "criterion-evidence-record"),
            ["acceptance-apparatus-hold"] = ("operator", "acceptance-retry"),
            ["environmental-apparatus-hold"] = ("operator", "acceptance-retry"),
            ["evidence-mutation-lease-hold"] = ("conductor", "next-tick"),
            ["replacement-evidence-mutation-lease"] = ("conductor", "next-tick"),
            ["acceptance-width-admission"] = ("conductor", "next-tick"),
            ["gate-start-infrastructure-deferred"] = ("conductor", "next-tick"),
            ["gate-start-build-slots-busy"] = ("conductor", "next-tick"),
            ["gate-start-build-lock-blocked"] = ("conductor", "next-tick"),
            ["attempt-running-in-background"] = ("conductor", "gate-reconciliation")
        };

    internal static void Observe(
        AgentOrchestratorKernel kernel, Goal goal, ConductorAdvanceOutcome outcome,
        DateTimeOffset observedAt, HashSet<GoalId> changedGoalIds)
    {
        try
        {
            var current = kernel.GetGoal(goal.Id);
            bool changed;
            if (outcome is ConductorAdvanceOutcome.Held held
                && (held.State is GoalLifecycleState.Verified or GoalLifecycleState.Verifying
                    || current.Status == GoalStatus.Verified))
            {
                var kind = held.Decision is { } decision
                    ? decision.Stage + "/" + decision.DiscriminatingEvidence
                    : "untyped/" + held.Owner;
                var next = held.Decision is { } typed
                    && NextActions.TryGetValue(typed.DiscriminatingEvidence, out var action)
                    ? action : (Actor: "unmapped", Verb: "unmapped");
                changed = current.ObserveLandingBlocker(new GoalLandingBlocker(
                    kind, held.Reason.Replace('\r', ' ').Replace('\n', ' '),
                    kind + ":" + (held.StableIdentity ?? ""), observedAt, next.Actor, next.Verb));
            }
            else
            {
                changed = current.ClearLandingBlocker();
            }

            if (changed) changedGoalIds.Add(current.Id);
        }
        catch (Exception ex)
        {
            try
            {
                Console.Error.WriteLine(
                    $"LANDING_BLOCKER_RECORD_FAILED goal={goal.Id.Value[..8]} exception={ex.GetType().Name}");
                Console.Error.Flush();
            }
            catch
            {
                // Console diagnostics are best effort during fault isolation.
            }
        }
    }
}
