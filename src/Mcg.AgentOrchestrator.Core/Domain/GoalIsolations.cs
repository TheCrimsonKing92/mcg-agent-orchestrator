namespace Mcg.AgentOrchestrator.Core;

public static class GoalIsolations
{
    public const string ValidModesText = "solo, none";

    public static bool TryParse(string? text, out GoalIsolation isolation)
    {
        isolation = GoalIsolation.None;
        switch (text?.Trim().ToLowerInvariant())
        {
            case "none": return true;
            case "solo": isolation = GoalIsolation.Solo; return true;
            default: return false;
        }
    }

    public static GoalIsolation ParseOrDefault(string? text) =>
        TryParse(text, out var isolation) ? isolation : GoalIsolation.None;

    public static string Format(GoalIsolation isolation) => isolation switch
    {
        GoalIsolation.None => "none",
        GoalIsolation.Solo => "solo",
        _ => throw new ArgumentOutOfRangeException(nameof(isolation))
    };
}
