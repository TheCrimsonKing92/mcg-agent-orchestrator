using Mcg.AgentOrchestrator.App.Orchestration;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

// Console wording and visibility are separate from conductor classifications used by other consumers.
internal static class OwnerConsoleActivityPresentation
{
    internal static string? Classify(OwnerConductEvent item)
    {
        var tag = ConductEventOperatorClassifier.Classify(item.EventKind, item.Detail);
        if (tag is null) return null;
        if (item.EventKind == "state-log-divergence")
        {
            // Zero lost/repeated entries are diagnostic bookkeeping, including by-design entries.
            // Require positive counts before presenting a divergence as an owner concern.
            if (!PositiveCount(item.Detail, "lost") && !PositiveCount(item.Detail, "repeated")) return null;
        }
        return tag;
    }

    internal static string Phrase(OwnerConductEvent item, string tag)
    {
        var tokens = item.Detail.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        bool Has(string value) => tokens.Contains(value, StringComparer.Ordinal);
        var phrase = item.EventKind switch
        {
            "goal-escalation" => "escalated",
            "goal-stalled" => "goal stalled",
            "worker-capacity-stalled" => "waiting for worker capacity",
            "acceptance" or "canary-gate" => Has("result=passed") ? "gate passed" :
                Has("result=blocked") ? "gate blocked" : "gate failed",
            "acceptance-cohort" => Has("outcome=failed") ? "group gate failed" : "group gate completed",
            "author" => Has("kind=ask-owner") ? "needs your input" : "author could not complete",
            "host-health" => tag == ConductEventOperatorClassifier.Decision ? "host needs attention" : "host recovered",
            "remote-executor-health" => tag == ConductEventOperatorClassifier.Decision ? "remote worker needs attention" : "remote worker recovered",
            "state-log-divergence" => "goal history needs attention",
            "train-receipt-held" => "landing held",
            "train-receipt-released" => "landing hold released",
            "rebase-automerge" => Has("result=merged") ? "changes merged" : "merge needs attention",
            "cohort-attribution-retracted" => "group verification needs review",
            "test-impact-degraded" => "test selection reduced",
            "test-impact-headroom-low" => "test capacity running low",
            "sweep-blocker" => "cleanup blocked",
            "loop-handoff" => tag == ConductEventOperatorClassifier.Decision ? "conductor handoff needs attention" : "conductor handoff completed",
            "loop-relaunch" => "conductor restart scheduled",
            "judge-panel" => "review completed",
            "failure-clusters" => "failure summary updated",
            "board-fill-draft" => "new work proposed",
            "board-fill-filed" => "new work filed",
            _ => tag == ConductEventOperatorClassifier.Decision ? "needs your attention" : "completed"
        };
        var reason = Field(tokens, "reason") ?? Field(tokens, "blocker");
        // Retain an unstructured human explanation, never dump the structured diagnostic payload.
        if (reason is null)
            reason = string.Join(" ", tokens.Where(token => !token.Contains('=') &&
                !token.Contains('_') && token.Any(char.IsLower)));
        reason = reason.Replace('_', ' ').Replace('-', ' ').Trim();
        return reason.Length == 0 ? phrase : $"{phrase}: {reason}";
    }

    internal static string Line(OwnerConsoleActivityItem item) =>
        $"{item.Timestamp.ToLocalTime():HH:mm:ss} {item.GoalPrefix}" +
        (string.IsNullOrEmpty(item.GoalTitle) ? "" : $" {item.GoalTitle}") +
        $" {item.Phrase}";

    private static bool PositiveCount(string detail, string name)
    {
        var values = detail.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Where(token => token.StartsWith(name + "=", StringComparison.Ordinal)).ToArray();
        return values.Length == 1 && int.TryParse(values[0].AsSpan(name.Length + 1), out var count) && count > 0;
    }

    private static string? Field(string[] tokens, string name)
    {
        var index = Array.FindIndex(tokens, token => token.StartsWith(name + "=", StringComparison.Ordinal));
        if (index < 0) return null;
        return string.Join(" ", new[] { tokens[index][(name.Length + 1)..] }
            .Concat(tokens.Skip(index + 1).TakeWhile(token => !token.Contains('='))));
    }
}
