namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal static class OwnerGoalTitle
{
    internal static string From(string? objective)
    {
        if (string.IsNullOrWhiteSpace(objective)) return string.Empty;
        foreach (var line in objective.Split(['\r', '\n'], StringSplitOptions.None))
        {
            var title = line.Trim();
            if (title.Length == 0) continue;
            title = title.TrimStart('#').Trim();
            return title.Length > 100 ? title[..97] + "..." : title;
        }
        return string.Empty;
    }
}
