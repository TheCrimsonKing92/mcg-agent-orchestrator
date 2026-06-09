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

    public static TaskComplexity Estimate(string taskDescription, string goalObjective, AgentRole role)
    {
        var taskScore = ScoreText(taskDescription);
        var goalContextScore = Math.Min(ScoreText(goalObjective), 2);
        var score = taskScore + goalContextScore;

        return taskScore >= 3 && score >= 4 ? TaskComplexity.Complex : TaskComplexity.Simple;
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

        // Multiple requirements (counted by conjunctions and list markers)
        // Thresholds are high because commas and "and" are common in normal prose
        var requirementCount = CountOccurrences(lower, " and ") +
                               CountOccurrences(lower, ", ") +
                               CountOccurrences(lower, "; ");
        if (requirementCount >= 10) score += 2;
        else if (requirementCount >= 7) score += 1;

        return score;
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
}
