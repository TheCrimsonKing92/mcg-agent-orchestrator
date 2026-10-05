namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class ConductEventOperatorClassifier
{
    internal const string Decision = "decision";
    internal const string Outcome = "outcome";

    internal static string? Classify(string eventKind, string detail)
    {
        if (eventKind is "goal-escalation" or "goal-stalled" or "worker-capacity-stalled")
        {
            return Decision;
        }

        var tokens = detail.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        bool HasToken(string token) => tokens.Contains(token, StringComparer.Ordinal);
        bool StartsWithToken(string token) => detail.StartsWith(token, StringComparison.Ordinal)
            && (detail.Length == token.Length || char.IsWhiteSpace(detail[token.Length]));

        return eventKind switch
        {
            "rebase-automerge" when StartsWithToken("REBASE_CONFLICT_AUTOMERGE") && HasToken("result=merged") && !HasToken("result=refused") => Outcome,
            "rebase-automerge" when StartsWithToken("REBASE_CONFLICT_AUTOMERGE") && HasToken("result=refused") && !HasToken("result=merged") => Decision,
            "cohort-attribution-retracted" when StartsWithToken("COHORT_ATTRIBUTION_RETRACTED") => Decision,
            "host-health" when StartsWithToken("HOST_HEALTH_DEGRADED") => Decision,
            "host-health" when StartsWithToken("HOST_HEALTH_RECOVERED") => Outcome,
            "state-log-divergence" when StartsWithToken("STATE_LOG_DIVERGENCE") =>
                ClassifyStateLogDivergence(tokens),
            "host-health" when StartsWithToken("HOST_HEALTH_FOREGROUND_LOCK_ARMED") => Decision,
            "host-health" when StartsWithToken("HOST_HEALTH_FOREGROUND_LOCK_DISARMED") => Outcome,
            "host-health" when StartsWithToken("HOST_HEALTH_PAGED_POOL_HIGH") => Decision,
            "host-health" when StartsWithToken("HOST_HEALTH_PAGED_POOL_NORMAL") => Outcome,
            "test-impact-degraded" when StartsWithToken("TEST_IMPACT_DEGRADED") => Outcome,
            "test-impact-headroom-low" when StartsWithToken("TEST_IMPACT_HEADROOM_LOW") => Decision,
            "sweep-blocker" => HasToken(ReconcileSweepRemediationCoordinator.AcceptanceQueueOwnerField)
                ? null : Decision,
            "loop-handoff" when StartsWithToken("ACTIVATION_REVERTED")
                || StartsWithToken("ACTIVATION_FAILED_BOTH") => Decision,
            "author" when HasToken("kind=ask-owner") || HasToken("kind=model-failure") => Decision,
            "acceptance" when HasToken("result=failed") || HasToken("result=blocked") => Decision,
            "canary-gate" when HasToken("result=failed") => Decision,
            "acceptance-cohort" when detail.StartsWith("ACCEPTANCE_COHORT ", StringComparison.Ordinal)
                && HasToken("outcome=failed") => Decision,
            "loop-handoff" when StartsWithToken("ACTIVATION_ADOPTED") => Outcome,
            "loop-relaunch" when StartsWithToken("LOOP_RELAUNCH_SCHEDULED") => Outcome,
            "judge-panel" when StartsWithToken("PANEL_CASE") => Outcome,
            "acceptance" when HasToken("result=passed") => Outcome,
            "canary-gate" when HasToken("result=passed") => Outcome,
            "acceptance-cohort" when (detail.StartsWith("ACCEPTANCE_COHORT ", StringComparison.Ordinal)
                && HasToken("outcome=passed"))
                || StartsWithToken("ACCEPTANCE_COHORT_RECONCILED_DEAD") => Outcome,
            "board-fill-draft" when StartsWithToken("BOARD_FILL_DRAFT") => Decision,
            _ => null
        };
    }

    private static string? ClassifyStateLogDivergence(string[] tokens)
    {
        int? Count(string name)
        {
            var matches = tokens.Where(token => token.StartsWith(name + "=", StringComparison.Ordinal)).ToArray();
            return matches.Length == 1 && int.TryParse(matches[0].AsSpan(name.Length + 1),
                System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture,
                out var count) ? count : null;
        }
        var lost = Count("lost");
        var repeated = Count("repeated");
        return lost > 0 || repeated > 0 ? Decision : lost == 0 && repeated == 0 ? Outcome : null;
    }
}
