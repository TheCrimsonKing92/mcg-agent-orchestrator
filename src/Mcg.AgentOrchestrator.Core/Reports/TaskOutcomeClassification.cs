namespace Mcg.AgentOrchestrator.Core;

public enum TaskOutcomeClass
{
    Success,
    RealFailure,
    Environmental,
    ManufacturedFixed,
    UnknownEra
}

public sealed record TaskOutcomeClassification(string? Rule, TaskOutcomeClass Class)
{
    public static TaskOutcomeClassification Success(string? rule = null) => new(rule, TaskOutcomeClass.Success);
    public static TaskOutcomeClassification UnknownEra(string? rule = null) => new(rule, TaskOutcomeClass.UnknownEra);
}

public static class TaskOutcomeClassifier
{
    private const string ClassifierPrefix = "CLASSIFIER ";
    private const string RulePrefix = "rule=";

    private static readonly HashSet<string> EnvironmentalRules = new(StringComparer.OrdinalIgnoreCase)
    {
        "dirty-dispatch-recovery",
        "preflight-failure",
        "provider-authentication",
        "provider-connectivity",
        "provider-neutral-progress-stall",
        "provider-model-rejection",
        "provider-rate-limit",
        "recoverable-subscription-limit",
        "subscription-limit"
    };

    private static readonly HashSet<string> ManufacturedFixedRules = new(StringComparer.OrdinalIgnoreCase)
    {
        "empty-output-flake",
        "provider-sandbox-1312",
        "retry-round-produced-no-commit-and-no-deferral",
        "sandbox-commit-blocked"
    };

    public static TaskOutcomeClassification Classify(WorkTaskStatus outcome, string? rule)
    {
        var normalizedRule = NormalizeRule(rule);
        if (outcome == WorkTaskStatus.Completed)
        {
            return TaskOutcomeClassification.Success(normalizedRule);
        }

        if (outcome != WorkTaskStatus.Failed)
        {
            return TaskOutcomeClassification.UnknownEra(normalizedRule);
        }

        if (normalizedRule is null)
        {
            return TaskOutcomeClassification.UnknownEra();
        }

        if (EnvironmentalRules.Contains(normalizedRule))
        {
            return new TaskOutcomeClassification(normalizedRule, TaskOutcomeClass.Environmental);
        }

        if (ManufacturedFixedRules.Contains(normalizedRule))
        {
            return new TaskOutcomeClassification(normalizedRule, TaskOutcomeClass.ManufacturedFixed);
        }

        return new TaskOutcomeClassification(normalizedRule, TaskOutcomeClass.RealFailure);
    }

    public static TaskOutcomeClassification FromTimeline(
        IEnumerable<ProgressEvent> timeline,
        TaskId taskId,
        WorkTaskStatus outcome,
        DateTimeOffset? completedAt = null,
        DateTimeOffset? before = null)
    {
        var rule = timeline
            .Select((evt, index) => (evt, index))
            .Where(item => item.evt.TaskId == taskId && item.evt.Kind == ProgressKind.TaskNote)
            .Where(item => completedAt is null || item.evt.OccurredAt >= completedAt.Value)
            .Where(item => before is null || item.evt.OccurredAt <= before.Value)
            .OrderByDescending(item => item.evt.OccurredAt)
            .ThenByDescending(item => item.index)
            .Select(item => TryExtractRule(item.evt.Message))
            .FirstOrDefault(value => value is not null);
        return Classify(outcome, rule);
    }

    public static string? TryExtractRule(string? message)
    {
        if (string.IsNullOrWhiteSpace(message) ||
            !message.Contains(ClassifierPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var ruleIndex = message.IndexOf(RulePrefix, StringComparison.OrdinalIgnoreCase);
        if (ruleIndex < 0)
        {
            return null;
        }

        var start = ruleIndex + RulePrefix.Length;
        var end = start;
        while (end < message.Length)
        {
            var c = message[end];
            if (!(char.IsLetterOrDigit(c) || c == '-'))
            {
                break;
            }

            end++;
        }

        return end > start ? NormalizeRule(message[start..end]) : null;
    }

    public static string FormatClass(TaskOutcomeClass outcomeClass) =>
        outcomeClass switch
        {
            TaskOutcomeClass.Success => "success",
            TaskOutcomeClass.RealFailure => "real-failure",
            TaskOutcomeClass.Environmental => "environmental",
            TaskOutcomeClass.ManufacturedFixed => "manufactured-fixed",
            TaskOutcomeClass.UnknownEra => "unknown-era",
            _ => outcomeClass.ToString()
        };

    public static TaskOutcomeClass ParseClass(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            "success" => TaskOutcomeClass.Success,
            "real-failure" => TaskOutcomeClass.RealFailure,
            "environmental" => TaskOutcomeClass.Environmental,
            "manufactured-fixed" => TaskOutcomeClass.ManufacturedFixed,
            "unknown-era" => TaskOutcomeClass.UnknownEra,
            _ => TaskOutcomeClass.UnknownEra
        };
    }

    private static string? NormalizeRule(string? rule)
    {
        var trimmed = rule?.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed.ToLowerInvariant();
    }
}
