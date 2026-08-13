using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class PortfolioClusterer
{
    private static readonly Regex PathRegex = new(
        @"(?<![\w.-])(?:src|tests|scripts|docs|config|\.agents|\.github)[\\/][A-Za-z0-9_.\\/\-]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex LineageRegex = new(
        @"(?:incident|parent|backlog)[\s:#-]+([A-Za-z0-9]{6,32})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static IReadOnlyList<PortfolioClusterSuggestion> BuildSuggestions(
        IReadOnlyCollection<Goal> goals,
        IReadOnlyList<BacklogItem> backlogItems)
    {
        var now = DateTimeOffset.UtcNow;
        var suggestions = new List<PortfolioClusterSuggestion>();
        suggestions.AddRange(BuildBacklogDependencySuggestions(backlogItems, now));
        suggestions.AddRange(BuildSharedFileScopeSuggestions(goals, now));
        suggestions.AddRange(BuildIntakeLineageSuggestions(goals, backlogItems, now));
        return suggestions
            .GroupBy(s => $"{s.Signal}:{string.Join(",", s.GoalIds.Order(StringComparer.Ordinal))}:{string.Join(",", s.BacklogItemIds.Order(StringComparer.Ordinal))}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(suggestion => suggestion.Signal, StringComparer.Ordinal)
            .ThenBy(suggestion => suggestion.Title, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IEnumerable<PortfolioClusterSuggestion> BuildBacklogDependencySuggestions(IReadOnlyList<BacklogItem> backlogItems, DateTimeOffset now)
    {
        foreach (var item in backlogItems)
        {
            var text = $"{item.Title}\n{item.Body}";
            var related = backlogItems
                .Where(candidate => candidate.Id != item.Id && ContainsBacklogReference(text, candidate.Id))
                .Select(candidate => candidate.Id)
                .Append(item.Id)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();
            if (related.Length < 2)
                continue;

            yield return Suggest(
                "backlog-dependency",
                $"Backlog dependency cluster: {item.Title}",
                $"backlog {item.Id[..8]} references {string.Join(", ", related.Where(id => id != item.Id).Select(ShortId))}",
                [],
                related,
                now);
        }
    }

    private static IEnumerable<PortfolioClusterSuggestion> BuildSharedFileScopeSuggestions(IReadOnlyCollection<Goal> goals, DateTimeOffset now)
    {
        var scopedGoals = goals
            .Select(goal => new { Goal = goal, Scopes = InferFileScopes(goal) })
            .Where(item => item.Scopes.Count > 0)
            .ToArray();

        foreach (var group in scopedGoals
            .SelectMany(item => item.Scopes.Select(scope => new { scope, item.Goal }))
            .GroupBy(item => item.scope, StringComparer.OrdinalIgnoreCase))
        {
            var goalIds = group.Select(item => item.Goal.Id.Value).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            if (goalIds.Length < 2)
                continue;

            yield return Suggest(
                "shared-file-scope",
                $"Shared file scope: {group.Key}",
                $"{goalIds.Length} goals reference {group.Key}",
                goalIds,
                [],
                now);
        }
    }

    private static IEnumerable<PortfolioClusterSuggestion> BuildIntakeLineageSuggestions(
        IReadOnlyCollection<Goal> goals,
        IReadOnlyList<BacklogItem> backlogItems,
        DateTimeOffset now)
    {
        var lineageRows = new List<(string Lineage, string? GoalId, string? BacklogItemId)>();
        lineageRows.AddRange(goals
            .Where(goal => !string.IsNullOrWhiteSpace(goal.SourceBacklogItemId))
            .Select(goal => (goal.SourceBacklogItemId!, (string?)goal.Id.Value, (string?)null)));
        lineageRows.AddRange(backlogItems
            .Where(item => !string.IsNullOrWhiteSpace(item.SourceGoalId))
            .Select(item => (item.SourceGoalId!, (string?)null, (string?)item.Id)));
        lineageRows.AddRange(backlogItems.SelectMany(item =>
            InferLineageKeys(item).Select(key => (key, (string?)null, (string?)item.Id))));

        foreach (var group in lineageRows.GroupBy(row => row.Lineage, StringComparer.OrdinalIgnoreCase))
        {
            var goalIds = group.Where(row => row.GoalId is not null).Select(row => row.GoalId!).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            var backlogIds = group.Where(row => row.BacklogItemId is not null).Select(row => row.BacklogItemId!).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            if (goalIds.Length + backlogIds.Length < 2)
                continue;

            yield return Suggest(
                "intake-lineage",
                $"Intake lineage: {group.Key}",
                $"shared source/parent {group.Key}",
                goalIds,
                backlogIds,
                now);
        }
    }

    private static PortfolioClusterSuggestion Suggest(
        string signal,
        string title,
        string evidence,
        IReadOnlyList<string> goalIds,
        IReadOnlyList<string> backlogItemIds,
        DateTimeOffset now)
    {
        var idSeed = $"{signal}:{string.Join(",", goalIds)}:{string.Join(",", backlogItemIds)}";
        var id = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(idSeed))).ToLowerInvariant()[..16];
        return new PortfolioClusterSuggestion(id, signal, title, evidence, goalIds, backlogItemIds, now);
    }

    private static IReadOnlyList<string> InferFileScopes(Goal goal)
    {
        var text = string.Join('\n', goal.Tasks.Select(task => $"{goal.Objective}\n{task.Description}\n{task.VerificationPlan}"));
        return PathRegex.Matches(text)
            .Select(match => match.Value.Replace('\\', '/').TrimEnd('.', ',', ';', ':', ')', ']'))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IEnumerable<string> InferLineageKeys(BacklogItem item)
    {
        var text = $"{item.Title}\n{item.Body}";
        return LineageRegex.Matches(text)
            .Select(match => match.Groups[1].Value)
            .Where(value => !value.Equals(item.Id, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static bool ContainsBacklogReference(string text, string backlogId) =>
        text.Contains(backlogId, StringComparison.OrdinalIgnoreCase) ||
        text.Contains(backlogId[..Math.Min(8, backlogId.Length)], StringComparison.OrdinalIgnoreCase);

    private static string ShortId(string value) => value[..Math.Min(8, value.Length)];
}
