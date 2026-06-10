namespace Mcg.AgentOrchestrator.Core;

public static class TaskComplexityEstimator
{
    private static readonly string[] ComplexitySignals =
    [
        "comprehensive", "production", "complete implementation",
        "full implementation", "concurrent", "thread-safe",
        "horizontal scaling", "distributed", "real-time",
        "multi-tenant", "end-to-end", "integration",
        "architecture", "design and implement", "system design"
    ];

    private static readonly string[] RiskTokenSignals =
    [
        "auth", "authentication", "authorization", "bypass",
        "credential", "credentials", "secret", "secrets",
        "encryption", "privacy", "permission", "permissions",
        "migration", "rollback", "corruption", "deadlock"
    ];

    private static readonly string[] RiskPhraseSignals =
    [
        "data loss", "race condition", "schema migration",
        "state migration", "breaking change"
    ];

    private static readonly string[] SurfaceSignals =
    [
        "api", "cli", "dashboard", "provider", "subscription",
        "worker", "persistence", "state", "tests", "docs"
    ];

    public static TaskComplexity Estimate(string taskDescription, string goalObjective, AgentRole role)
    {
        if (IsLowImpactDocumentationOrCopyTask(taskDescription) ||
            IsLowImpactInspectionOrReportTask(taskDescription) ||
            IsLowImpactTestOrVerificationTask(taskDescription))
        {
            return TaskComplexity.Simple;
        }

        var taskScore = ScoreText(taskDescription);
        var goalContextScore = Math.Min(ScoreText(goalObjective), 2);
        var score = taskScore + goalContextScore;
        var taskThreshold = RoleTaskScoreThreshold(role);
        var totalThreshold = RoleTotalScoreThreshold(role);

        return taskScore >= taskThreshold && score >= totalThreshold ? TaskComplexity.Complex : TaskComplexity.Simple;
    }

    public static ModelProfile ResolveModel(AgentDefinition agent, TaskComplexity complexity, string taskDescription, string goalObjective)
    {
        if (agent.ComplexModel is null)
        {
            return agent.Model;
        }

        var effective = complexity == TaskComplexity.Auto
            ? Estimate(taskDescription, goalObjective, agent.Role)
            : complexity;

        return effective == TaskComplexity.Complex ? agent.ComplexModel : agent.Model;
    }

    private static int ScoreText(string text)
    {
        var lower = text.ToLowerInvariant();
        var score = 0;

        // Length signals — longer descriptions tend to be more complex tasks
        if (lower.Length > 400) score += 2;
        else if (lower.Length > 200) score += 1;

        // Keyword signals
        foreach (var signal in ComplexitySignals)
        {
            if (lower.Contains(signal, StringComparison.OrdinalIgnoreCase))
            {
                score += 1;
            }
        }

        foreach (var signal in RiskPhraseSignals)
        {
            if (lower.Contains(signal, StringComparison.OrdinalIgnoreCase))
            {
                score += 2;
            }
        }

        // Multiple requirements (counted by conjunctions and list markers)
        // Thresholds are high because commas and "and" are common in normal prose
        var requirementCount = CountOccurrences(lower, " and ") +
                               CountOccurrences(lower, ", ") +
                               CountOccurrences(lower, "; ");
        if (requirementCount >= 10) score += 2;
        else if (requirementCount >= 7) score += 1;

        var tokens = BuildTokenSet(lower);
        foreach (var signal in RiskTokenSignals)
        {
            if (tokens.Contains(signal))
            {
                score += 2;
            }
        }

        var surfaceCount = SurfaceSignals.Count(tokens.Contains);
        if (surfaceCount >= 4) score += 4;
        else if (surfaceCount >= 3) score += 1;

        return score;
    }

    private static bool IsLowImpactDocumentationOrCopyTask(string text)
    {
        var lower = text.ToLowerInvariant();
        var tokens = BuildTokenSet(lower);
        if (HasRiskSignal(lower, tokens) || HasStrongComplexitySignal(lower))
        {
            return false;
        }

        var documentationTask =
            ContainsAny(tokens, "doc", "docs", "documentation", "readme", "changelog") &&
            StartsWithAny(
                lower,
                "add docs",
                "add documentation",
                "clarify docs",
                "clarify documentation",
                "document ",
                "edit docs",
                "fix docs",
                "update docs",
                "update documentation",
                "update readme");
        var copyTask =
            ContainsAny(tokens, "copy", "label", "labels", "text", "tooltip", "tooltips", "wording") &&
            StartsWithAny(lower, "change ", "clarify ", "edit ", "fix ", "rename ", "update ");

        return documentationTask || copyTask;
    }

    private static bool IsLowImpactInspectionOrReportTask(string text)
    {
        var lower = text.ToLowerInvariant();
        var tokens = BuildTokenSet(lower);
        if (HasRiskSignal(lower, tokens) || HasStrongComplexitySignal(lower))
        {
            return false;
        }

        if (ContainsAny(tokens, "implement", "implementation", "fix", "add", "update", "change", "migrate", "build", "create"))
        {
            return false;
        }

        return ContainsAny(tokens, "inspect", "inventory", "list", "report", "summarize", "summary") &&
            StartsWithAny(
                lower,
                "check ",
                "inspect ",
                "inventory ",
                "list ",
                "report ",
                "summarize ");
    }

    private static bool IsLowImpactTestOrVerificationTask(string text)
    {
        var lower = text.ToLowerInvariant();
        var tokens = BuildTokenSet(lower);
        if (HasRiskSignal(lower, tokens) || HasStrongComplexitySignal(lower))
        {
            return false;
        }

        var testTask =
            ContainsAny(tokens, "test", "tests", "coverage", "verification") &&
            StartsWithAny(
                lower,
                "add coverage",
                "add focused test",
                "add focused tests",
                "add regression test",
                "add regression tests",
                "add test",
                "add tests",
                "update test",
                "update tests",
                "verify ");

        return testTask;
    }

    private static bool HasRiskSignal(string lower, HashSet<string> tokens)
    {
        return RiskTokenSignals.Any(tokens.Contains) ||
            RiskPhraseSignals.Any(signal => lower.Contains(signal, StringComparison.OrdinalIgnoreCase));
    }

    private static bool HasStrongComplexitySignal(string lower)
    {
        return ComplexitySignals.Any(signal =>
            !signal.Equals("integration", StringComparison.OrdinalIgnoreCase) &&
            lower.Contains(signal, StringComparison.OrdinalIgnoreCase));
    }

    private static HashSet<string> BuildTokenSet(string text)
    {
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var start = -1;
        for (var index = 0; index <= text.Length; index++)
        {
            if (index < text.Length && char.IsLetterOrDigit(text[index]))
            {
                if (start < 0)
                {
                    start = index;
                }

                continue;
            }

            if (start >= 0)
            {
                tokens.Add(text[start..index]);
                start = -1;
            }
        }

        return tokens;
    }

    private static int RoleTaskScoreThreshold(AgentRole role)
    {
        return role is AgentRole.Tester or AgentRole.Reviewer ? 5 : 3;
    }

    private static int RoleTotalScoreThreshold(AgentRole role)
    {
        return role is AgentRole.Tester or AgentRole.Reviewer ? 6 : 4;
    }

    private static int CountOccurrences(string text, string pattern)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(pattern, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += pattern.Length;
        }
        return count;
    }

    private static bool ContainsAny(HashSet<string> tokens, params string[] values)
    {
        return values.Any(tokens.Contains);
    }

    private static bool StartsWithAny(string text, params string[] values)
    {
        return values.Any(value => text.StartsWith(value, StringComparison.OrdinalIgnoreCase));
    }
}
