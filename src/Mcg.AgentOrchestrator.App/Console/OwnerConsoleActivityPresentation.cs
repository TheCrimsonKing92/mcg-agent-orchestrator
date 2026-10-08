using Mcg.AgentOrchestrator.App.Orchestration;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

// Console wording and visibility are separate from conductor classifications used by other consumers.
internal static class OwnerConsoleActivityPresentation
{
    internal static string? Classify(OwnerConductEvent item)
    {
        if (item.EventKind == "goal-lifecycle")
            return item.Detail.Split(' ')[0] is "TaskDispatched" or "TaskCompleted" or "TaskFailed" or "FindingEvidenceRequestRecorded"
                ? ConductEventOperatorClassifier.Outcome : null;
        if (item.EventKind is "acceptance" or "canary-gate" &&
            item.Detail.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Contains("result=started"))
            return ConductEventOperatorClassifier.Outcome;
        if (item.EventKind == "loop-relaunch" && item.GoalId is not null &&
            item.Detail.StartsWith("LOOP_RELAUNCH_NOT_REQUIRED", StringComparison.Ordinal))
            return ConductEventOperatorClassifier.Outcome;
        if (item.EventKind == "loop-handoff" && item.Detail.StartsWith("LOOP_HANDOFF_FAILED", StringComparison.Ordinal))
            return ConductEventOperatorClassifier.Decision;
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
        if (item.EventKind == "goal-lifecycle")
        {
            var role = Field(tokens, "role") ?? "task";
            return tokens.FirstOrDefault() switch
            {
                "TaskDispatched" or "TaskProcessStarted" => $"{role} started",
                "TaskCompleted" or "TaskVerified" => $"{role} finished: passed",
                "TaskFailed" => $"{role} finished: failed",
                "FindingEvidenceRequestRecorded" => $"{role} finished: finding raised",
                "GoalLanded" => "landed",
                _ => System.Text.RegularExpressions.Regex.Replace(tokens.FirstOrDefault() ?? "event", "(?<=[a-z])(?=[A-Z])", " ").ToLowerInvariant()
            };
        }
        if (item.EventKind == "loop-relaunch") return "landed";
        if (item.EventKind == "loop-handoff" && Has("ACTIVATION_ADOPTED")) return "conductor restarted on the new build";
        if (item.EventKind == "loop-handoff" && Has("LOOP_HANDOFF_FAILED"))
            return "conductor restart failed: " + (Field(tokens, "reason") ?? Field(tokens, "phase") ?? "unknown").Replace('_', ' ').Replace('-', ' ');
        // These emitters carry commands, questions and evidence for the detail dialog.
        // The activity line describes the owner's next action without copying that payload.
        if (item.EventKind == "goal-escalation")
        {
            switch (tokens.FirstOrDefault())
            {
                case "owner-review-hold": return "needs your approval";
                case "author-owner-question":
                case "steward-owner-question": return "needs your input";
            }
        }
        var phrase = item.EventKind switch
        {
            "goal-escalation" => "escalated",
            "goal-stalled" => "goal stalled",
            "worker-capacity-stalled" => "waiting for worker capacity",
            "acceptance" or "canary-gate" => Has("result=started") ? "gate started" : Has("result=passed") ? "gate passed" :
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
            "loop-handoff" => "conductor handoff needs attention",
            "judge-panel" => "review completed",
            "failure-clusters" => "failure summary updated",
            "board-fill-draft" => "new work proposed",
            "board-fill-filed" => "new work filed",
            _ => tag == ConductEventOperatorClassifier.Decision ? "needs your attention" : "completed"
        };
        var reason = Field(tokens, "reason") ?? Field(tokens, "blocker") ?? string.Empty;
        reason = reason.Replace('_', ' ').Replace('-', ' ').Trim();
        return reason.Length == 0 ? phrase : $"{phrase}: {reason}";
    }

    internal static string Line(OwnerConsoleActivityItem item)
    {
        // Reserve room for the event phrase in the single-line activity pane.
        const int maxTitleLength = 40;
        var title = item.GoalTitle.Length > maxTitleLength
            ? item.GoalTitle[..(maxTitleLength - 1)] + "…" : item.GoalTitle;
        return $"{item.Timestamp.ToLocalTime():HH:mm:ss}" + (string.IsNullOrEmpty(item.GoalPrefix) ? "" : $" {item.GoalPrefix}") +
            (string.IsNullOrEmpty(title) ? "" : $" {title}") + $" {item.Phrase}";
    }

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
