using System.Text.RegularExpressions;

namespace Mcg.AgentOrchestrator.Core;

public static class GoalCommitTitle
{
    public static string Resolve(string? objective, string? taskPurpose)
    {
        var firstLine = (objective ?? string.Empty)
            .Split(['\r', '\n'])
            .FirstOrDefault(line => !string.IsNullOrWhiteSpace(line)) ?? string.Empty;
        var title = Normalize(Regex.Replace(firstLine.Trim(), "^#{1,6} ", string.Empty));
        return title.Length > 0 ? title : Normalize(taskPurpose);
    }

    public static string Normalize(string? value) =>
        Regex.Replace((value ?? string.Empty).Trim(), @"\s+", " ");
}
