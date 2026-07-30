using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal enum FileScopeProvenance
{
    Explicit,
    Inferred
}

internal sealed record DeclaredFileScope(string Path, FileScopeProvenance Provenance);

internal static class GoalFileScopeInference
{
    private static readonly Regex FileScopeRegex = new(
        @"(?<![\w.-])(?:src|tests|scripts|docs|config|\.agents)[\\/][A-Za-z0-9_.\\/\-]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public static IReadOnlyList<DeclaredFileScope> FromText(string text)
    {
        var scopes = new Dictionary<string, FileScopeProvenance>(StringComparer.OrdinalIgnoreCase);
        var inGeneratedScopeBlock = false;
        foreach (var line in NormalizeNewlines(text).Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Equals(BacklogIntakePlanner.TargetScopeHeadingLine, StringComparison.Ordinal))
            {
                inGeneratedScopeBlock = true;
                continue;
            }

            var provenance = inGeneratedScopeBlock && trimmed.StartsWith("- ", StringComparison.Ordinal)
                ? FileScopeProvenance.Inferred
                : FileScopeProvenance.Explicit;
            if (inGeneratedScopeBlock && provenance == FileScopeProvenance.Explicit)
            {
                inGeneratedScopeBlock = false;
            }

            foreach (Match match in FileScopeRegex.Matches(line))
            {
                var path = NormalizePath(match.Value);
                if (path.Length == 0)
                {
                    continue;
                }

                if (!scopes.TryGetValue(path, out var existing) ||
                    provenance == FileScopeProvenance.Explicit && existing == FileScopeProvenance.Inferred)
                {
                    scopes[path] = provenance;
                }
            }
        }

        return scopes
            .Select(pair => new DeclaredFileScope(pair.Key, pair.Value))
            .OrderBy(scope => scope.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static IReadOnlyList<DeclaredFileScope> FromGoal(
        Goal goal,
        int maximumCharacters,
        out bool truncated) =>
        FromTextParts(GoalTextParts(goal), maximumCharacters, out truncated);

    internal static IReadOnlyList<DeclaredFileScope> FromTextParts(
        IEnumerable<string> parts,
        int maximumCharacters,
        out bool truncated)
    {
        var remaining = maximumCharacters;
        var selected = new List<IReadOnlyList<DeclaredFileScope>>();
        truncated = false;
        foreach (var part in parts)
        {
            if (remaining <= 0)
            {
                if (!string.IsNullOrEmpty(part))
                {
                    truncated = true;
                }

                continue;
            }

            var scanText = part.Length <= remaining ? part : part[..remaining];
            truncated |= scanText.Length != part.Length;
            remaining -= scanText.Length;
            selected.Add(FromText(scanText));
        }

        return Merge(selected);
    }

    private static IEnumerable<string> GoalTextParts(Goal goal)
    {
        yield return goal.Objective;
        foreach (var task in goal.Tasks)
        {
            yield return task.Description;
            if (!string.IsNullOrWhiteSpace(task.VerificationPlan))
            {
                yield return task.VerificationPlan;
            }
        }
    }

    private static IReadOnlyList<DeclaredFileScope> Merge(IEnumerable<IReadOnlyList<DeclaredFileScope>> groups)
    {
        var scopes = new Dictionary<string, FileScopeProvenance>(StringComparer.OrdinalIgnoreCase);
        foreach (var scope in groups.SelectMany(group => group))
        {
            if (!scopes.TryGetValue(scope.Path, out var existing) ||
                scope.Provenance == FileScopeProvenance.Explicit && existing == FileScopeProvenance.Inferred)
            {
                scopes[scope.Path] = scope.Provenance;
            }
        }

        return scopes
            .Select(pair => new DeclaredFileScope(pair.Key, pair.Value))
            .OrderBy(scope => scope.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string NormalizeNewlines(string text) =>
        (text ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    private static string NormalizePath(string path) =>
        path.Replace('\\', '/').TrimEnd('.', ',', ';', ':', ')', ']');
}
