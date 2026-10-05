using System.Text.RegularExpressions;

namespace Mcg.AgentOrchestrator.Core;

/// <summary>Positive evidence that every current rework finding is a recognized mechanical correction.</summary>
public sealed record MechanicalReworkClassification(bool IsMechanical, string? RuleId, IReadOnlyList<string> Ids);

public static class MechanicalReworkClassifier
{
    private const string StructuralPrefix = "developer-completion structural pre-check failed: ";
    private static readonly Regex RatchetLine = new(
        @"^(?<path>\S+) has \d+ lines, exceeding the recorded ceiling of \d+\.",
        RegexOptions.CultureInvariant);
    private static readonly MechanicalReworkClassification NotMechanical = new(false, null, []);

    public static MechanicalReworkClassification Classify(Goal goal, TaskSpec task)
    {
        ArgumentNullException.ThrowIfNull(goal);
        ArgumentNullException.ThrowIfNull(task);
        if (task.RequiredRole != AgentRole.Developer) return NotMechanical;
        var previous = task.DispatchHistory.LastOrDefault();
        if (previous is null) return NotMechanical;
        var anchor = -1;
        for (var i = goal.Timeline.Count - 1; i >= 0; i--)
            if (goal.Timeline[i].TaskId == task.Id && goal.Timeline[i].Kind == ProgressKind.TaskDispatchRecorded)
            {
                anchor = i;
                break;
            }
        var retry = goal.Timeline.Skip(anchor + 1).LastOrDefault(e => e.TaskId == task.Id &&
            e.Kind == ProgressKind.TaskRetried && (anchor >= 0 || e.OccurredAt > previous.DispatchedAt));
        if (retry is null) return NotMechanical;
        var message = retry.Message;
        if (message.StartsWith(StructuralPrefix, StringComparison.Ordinal))
        {
            var remainder = message[StructuralPrefix.Length..];
            if (remainder.StartsWith("config/acceptance-manifest.json: ", StringComparison.Ordinal))
                return new(true, "structural-manifest", ["config/acceptance-manifest.json"]);
            var lines = remainder.Split('\n').Select(line => line.TrimEnd('\r')).Where(line => line.Length > 0).ToArray();
            if (lines.Length == 0) return NotMechanical;
            var matches = lines.Select(line => RatchetLine.Match(line)).ToArray();
            if (matches.Any(match => !match.Success)) return NotMechanical;
            return Mechanical("structural-ratchet", matches.Select(match => match.Groups["path"].Value));
        }
        if (!message.StartsWith("auto-review-retry round ", StringComparison.Ordinal)) return NotMechanical;
        var findings = RetryContextFingerprintFactory.GetOpenBlockingFindings(goal);
        if (findings.Count == 0 || findings.Any(f =>
                f.Category is not (FindingCategory.SpecCompliance or FindingCategory.Correctness or FindingCategory.CodeQuality) ||
                !(f.Description.Contains("SourceSizeRatchet", StringComparison.OrdinalIgnoreCase) ||
                  f.Description.Contains("exceeding the recorded ceiling", StringComparison.OrdinalIgnoreCase))))
            return NotMechanical;
        return Mechanical("review-ratchet", findings.Select(f => f.StableId));
    }

    private static MechanicalReworkClassification Mechanical(string rule, IEnumerable<string> ids)
    {
        var values = ids.Distinct(StringComparer.Ordinal).ToArray();
        return values.Any(id => string.IsNullOrEmpty(id) || id.Any(c => char.IsWhiteSpace(c) || c is ',' or ';'))
            ? NotMechanical : new(true, rule, values);
    }
}
