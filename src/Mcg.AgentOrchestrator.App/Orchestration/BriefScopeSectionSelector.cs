using System.Text.RegularExpressions;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class BriefScopeSectionSelector
{
    public static (int Start, int End)? FindScopeRegion(IReadOnlyList<string> lines)
    {
        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];
            if (!line.StartsWith('#') ||
                !line.TrimStart('#').Trim().Equals("Scope", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var end = index + 1;
            for (; end < lines.Count; end++)
            {
                var trimmed = lines[end].Trim();
                if (trimmed.StartsWith('#') ||
                    trimmed.StartsWith("Out of scope", StringComparison.OrdinalIgnoreCase) ||
                    trimmed.StartsWith("Post-landing check", StringComparison.OrdinalIgnoreCase) ||
                    trimmed.StartsWith("Dependencies:", StringComparison.OrdinalIgnoreCase) ||
                    trimmed.StartsWith("Verification ownership", StringComparison.OrdinalIgnoreCase) ||
                    trimmed.Equals(BacklogIntakePlanner.TargetScopeHeadingLine, StringComparison.Ordinal))
                {
                    break;
                }
            }

            return (index + 1, end);
        }

        return null;
    }

    public static bool IsEvidenceOnly(string line, int index, int length)
    {
        var match = line.Substring(index, length);
        return (index > 0 && line[index - 1] is '/' or '\\') ||
            line.AsSpan(index + length).StartsWith("::", StringComparison.Ordinal) ||
            match.IndexOfAny(['*', '?']) >= 0 ||
            match.Split('/', '\\').Any(segment =>
                segment.Length > 0 && segment.All(character => character is >= '0' and <= '9'));
    }

    public static IEnumerable<string> BacktickedPaths(string line)
    {
        foreach (Match match in Regex.Matches(line, @"`([^`]*)`"))
        {
            var path = match.Groups[1].Value.TrimEnd('.', ',', ';', ':', ')');
            if (!path.Contains('/') || path.StartsWith('/') || path.Any(char.IsWhiteSpace) ||
                path.IndexOfAny([':', '\\', '*', '?', '[', ']', '{', '}', '~', '|', '=', '<', '>']) >= 0 ||
                path.Split('/').Any(segment => segment is "." or ".." ||
                    (segment.Length > 0 && segment.All(character => character is >= '0' and <= '9'))))
            {
                continue;
            }

            var name = path[(path.LastIndexOf('/') + 1)..];
            var dot = name.LastIndexOf('.');
            if (path.EndsWith('/') || (dot > 0 && dot < name.Length - 1))
            {
                yield return path;
            }
        }
    }

    public static IReadOnlyList<DeclaredFileScope> KeepResolvable(
        string text,
        IReadOnlyList<DeclaredFileScope> scopes,
        string repositoryRoot)
    {
        var lines = text.ReplaceLineEndings("\n").Split('\n');
        var region = FindScopeRegion(lines);
        return scopes.Where(scope => scope.Provenance != FileScopeProvenance.Explicit ||
            File.Exists(Path.Combine(repositoryRoot, scope.Path)) ||
            Directory.Exists(Path.Combine(repositoryRoot, scope.Path)) ||
            IsNew(scope.Path)).ToArray();

        bool IsNew(string path)
        {
            var candidates = region is { } range
                ? lines.Skip(range.Start).Take(range.End - range.Start)
                : [text];
            foreach (var candidate in candidates)
            {
                var index = candidate.IndexOf(path, StringComparison.OrdinalIgnoreCase);
                while (index >= 0)
                {
                    if (region is not null
                        ? Regex.IsMatch(candidate[..index], @"\bnew\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
                        : GoalFileScopeInference.IsDeclaredNew(candidate, index))
                    {
                        return true;
                    }

                    index = candidate.IndexOf(path, index + path.Length, StringComparison.OrdinalIgnoreCase);
                }
            }

            return false;
        }
    }
}
