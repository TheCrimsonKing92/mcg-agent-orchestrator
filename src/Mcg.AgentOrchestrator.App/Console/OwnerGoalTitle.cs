using Mcg.AgentOrchestrator.App.Orchestration;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal static class OwnerGoalTitle
{
    internal static string Full(string? objective) => Display(objective);

    internal static string From(string? objective) => Display(objective);

    internal static string Display(string? objective)
    {
        var original = (objective ?? "").Split(['\r', '\n'])
            .Select(line => line.Trim().TrimStart('#').Trim()).FirstOrDefault(line => line.Length > 0) ?? "";
        if (!original.StartsWith(BacklogIntakePlanner.GeneratedHeadingPrefix, StringComparison.Ordinal) ||
            !HasGeneratedScopeBlock(objective)) return original;

        var title = original;
        while (title.StartsWith(BacklogIntakePlanner.GeneratedHeadingPrefix, StringComparison.Ordinal))
            title = title[BacklogIntakePlanner.GeneratedHeadingPrefix.Length..].TrimStart();
        return title.Length == 0 ? original : title;
    }

    private static bool HasGeneratedScopeBlock(string? objective)
    {
        using var reader = new StringReader(objective ?? "");
        var previousWasHeading = false;
        while (reader.ReadLine() is { } line)
        {
            var trimmed = line.Trim();
            if (previousWasHeading && (trimmed == BacklogIntakePlanner.PreciseScopeMarkerLine ||
                trimmed == BacklogIntakePlanner.UnknownScopeMarkerLine)) return true;
            previousWasHeading = trimmed == BacklogIntakePlanner.TargetScopeHeadingLine;
        }
        return false;
    }
}
