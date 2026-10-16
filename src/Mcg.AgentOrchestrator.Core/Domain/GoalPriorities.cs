namespace Mcg.AgentOrchestrator.Core;

public static class GoalPriorities
{
    public const string ValidLevelsText = "low, normal, high, critical";

    public static bool TryParse(string? text, out GoalPriority priority)
    {
        priority = GoalPriority.Normal;
        switch (text?.ToLowerInvariant())
        {
            case "low": priority = GoalPriority.Low; return true;
            case "normal": return true;
            case "high": priority = GoalPriority.High; return true;
            case "critical": priority = GoalPriority.Critical; return true;
            default: return false;
        }
    }

    public static GoalPriority ParseOrDefault(string? text) =>
        TryParse(text, out var priority) ? priority : GoalPriority.Normal;

    public static string Format(GoalPriority priority) => priority switch
    {
        GoalPriority.Low => "low",
        GoalPriority.Normal => "normal",
        GoalPriority.High => "high",
        GoalPriority.Critical => "critical",
        _ => throw new ArgumentOutOfRangeException(nameof(priority))
    };
}
