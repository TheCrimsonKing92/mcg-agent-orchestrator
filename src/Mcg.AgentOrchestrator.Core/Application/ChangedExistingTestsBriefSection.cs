namespace Mcg.AgentOrchestrator.Core;

public enum ChangedExistingTestClass
{
    NoRuling,
    AllowedByRuling,
    FrozenClassViolation
}

/// <summary>A method that already existed at the reviewer merge base and changed in the candidate.</summary>
public sealed record ChangedExistingTest(
    string TypeName, string MethodName, string File, int StartLine, int EndLine, bool Removed,
    ChangedExistingTestClass Class = ChangedExistingTestClass.NoRuling, string? RulingRequestId = null);

internal static class ChangedExistingTestsBriefSection
{
    internal const string Closing =
        "Each frozen-class violation is a blocking finding with stable id `frozen-fact-scope-<Type.Method>`. Check each `no ruling` entry against any criterion that keeps tests unmodified.";

    internal static IReadOnlyList<string> Render(
        AgentRole role, IReadOnlyList<ChangedExistingTest>? entries, string? diagnostic)
    {
        if (role != AgentRole.Reviewer) return [];
        if (!string.IsNullOrWhiteSpace(diagnostic))
            return ["## Changed existing tests", diagnostic.ReplaceLineEndings(" "), string.Empty];
        if (entries is not { Count: > 0 }) return [];
        var lines = new List<string> { "## Changed existing tests" };
        foreach (var entry in entries.OrderBy(entry => entry.Class != ChangedExistingTestClass.FrozenClassViolation)
                     .ThenBy(entry => entry.File, StringComparer.Ordinal).ThenBy(entry => entry.StartLine)
                     .ThenBy(entry => entry.TypeName, StringComparer.Ordinal)
                     .ThenBy(entry => entry.MethodName, StringComparer.Ordinal).Take(60))
        {
            var classification = entry.Class switch
            {
                ChangedExistingTestClass.AllowedByRuling => $"allowed by ruling {entry.RulingRequestId}",
                ChangedExistingTestClass.FrozenClassViolation => $"frozen-class violation {entry.RulingRequestId}",
                _ => "no ruling"
            };
            lines.Add($"- {entry.TypeName}.{entry.MethodName} ({entry.File}:{entry.StartLine}-{entry.EndLine}) {(entry.Removed ? "removed" : "changed")}: {classification}");
        }
        if (entries.Count > 60) lines.Add($"{entries.Count - 60} more entries not shown.");
        lines.Add(Closing);
        lines.Add(string.Empty);
        return lines;
    }
}
