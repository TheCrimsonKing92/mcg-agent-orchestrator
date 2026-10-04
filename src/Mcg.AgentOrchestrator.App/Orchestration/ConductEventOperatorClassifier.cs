namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class ConductEventOperatorClassifier
{
    internal const string Decision = "decision";
    internal const string Outcome = "outcome";

    internal static string? Classify(string eventKind, string detail)
    {
        if (eventKind is "goal-escalation" or "goal-stalled")
        {
            return Decision;
        }

        var tokens = detail.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        bool HasToken(string token) => tokens.Contains(token, StringComparer.Ordinal);
        bool StartsWithToken(string token) => detail.StartsWith(token, StringComparison.Ordinal)
            && (detail.Length == token.Length || char.IsWhiteSpace(detail[token.Length]));

        return eventKind switch
        {
            "host-health" when StartsWithToken("HOST_HEALTH_DEGRADED") => Decision,
            "host-health" when StartsWithToken("HOST_HEALTH_RECOVERED") => Outcome,
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
            _ => null
        };
    }
}
