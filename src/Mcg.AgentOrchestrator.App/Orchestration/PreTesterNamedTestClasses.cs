using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class PreTesterNamedTestClasses
{
    internal static IReadOnlyList<string> Harvest(string? text)
    {
        if (string.IsNullOrEmpty(text)) return [];
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in Regex.Matches(text, @"[A-Za-z0-9_]+(?:\.[A-Za-z0-9_]+)*"))
        {
            // After the class name, dotted segments identify a method or file suffix.
            var name = match.Value.Split('.').FirstOrDefault(token =>
                token[0] is >= 'A' and <= 'Z' && token.Contains("Tests", StringComparison.Ordinal));
            if (name is not null && seen.Add(name)) names.Add(name);
        }
        return names;
    }

    internal static IReadOnlyList<FindingEvidenceSelection> Select(
        Goal goal, TaskSpec tester, string worktreePath)
    {
        var planner = goal.Tasks.TakeWhile(task => task.Id != tester.Id)
            .LastOrDefault(task => task.RequiredRole == AgentRole.Planner &&
                                   task.Status == WorkTaskStatus.Completed);
        var output = planner?.LastVerification?.AuthoritativeStandardOutput ??
                     planner?.LastVerification?.StandardOutput;
        var names = Harvest(goal.Objective).Concat(Harvest(output))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return DeveloperDeferredTestSelections.ResolveNames(
            worktreePath, names, requireUniqueSourceFile: true).Selections;
    }
}
