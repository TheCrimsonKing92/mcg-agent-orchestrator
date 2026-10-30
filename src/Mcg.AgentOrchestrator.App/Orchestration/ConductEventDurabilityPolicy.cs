namespace Mcg.AgentOrchestrator.App.Orchestration;

// Events whose loss must remain visible to the operator rather than being advisory.
internal static class ConductEventDurabilityPolicy
{
    internal static bool IsRequired(string kind, string line) =>
        kind is "loop-start-deferred" or "loop-relaunch-rollback" or "loop-janitorial-failure" or
            "loop-janitorial-degraded" or "goal-stalled" or "sweep-blocker" or "sweep-owned-root-deferred" or
            "sweep-remedy-attempt" or "sweep-remedy-result" or "sweep-escalation" or "exit-unapplied" or
            "blocked-recheck-heartbeat" or "policy-reload-failed" or "prompt-rollout-suspect" or "invalidated-attempt-stop" ||
        line.StartsWith("LOOP_HANDOFF_FAILED ", StringComparison.Ordinal);
}
