namespace Mcg.AgentOrchestrator.Core;

/// <summary>Propagates complete authoritative answers, bounding entry count rather than ruling text.</summary>
internal static class ClarificationsAndRulingsDigest
{
    internal const string Heading = "## Clarifications and rulings";
    internal const int MaxEntries = 5;
    internal const string Instruction =
        "These answers are authoritative scope and ruling decisions for this goal; if you believe one is wrong, " +
        "raise a clarification or owner question instead of a blocking finding " +
        "(a defect in how an authorized change was implemented is still a finding).";

    internal static IReadOnlyList<TaskBriefSegment> RenderSegments(
        IReadOnlyList<GoalScopedClarificationAnswer> entries,
        int currentBriefVersion)
    {
        var lines = RenderLines(entries, currentBriefVersion);
        return lines.Count == 0 ? [] : [TaskBriefSegment.Fixed(lines)];
    }

    internal static IReadOnlyList<string> RenderLines(
        IReadOnlyList<GoalScopedClarificationAnswer> entries,
        int currentBriefVersion)
    {
        if (entries.Count == 0)
            return [];

        var retained = entries
            .OrderBy(entry => entry.AnsweredAt)
            .ThenBy(entry => entry.RequestId, StringComparer.Ordinal)
            .TakeLast(MaxEntries)
            .ToList();
        var lines = new List<string> { Heading, Instruction };
        foreach (var entry in retained)
        {
            var role = entry.AskingRole?.ToString() ?? "unknown role";
            var answeredUnder = entry.AnsweredBriefVersion is { } version
                ? $"brief v{version}"
                : "an unknown brief version";
            lines.Add(
                $"- {entry.RequestId} (asked by {role} task): " +
                $"{PrerequisiteEvidenceDigest.CollapseToSingleLine(entry.Question)} → " +
                $"{PrerequisiteEvidenceDigest.CollapseToSingleLine(entry.AnswerText)} " +
                $"(answered by {entry.Origin} under {answeredUnder}; current brief v{currentBriefVersion})");
        }

        var omittedCount = entries.Count - retained.Count;
        if (omittedCount > 0)
            lines.Add($"Omitted {omittedCount} older clarification answers over the {MaxEntries}-entry cap.");
        lines.Add(string.Empty);
        return lines;
    }
}
