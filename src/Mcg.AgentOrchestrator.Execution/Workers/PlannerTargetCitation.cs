using System.Text.RegularExpressions;

namespace Mcg.AgentOrchestrator.Infrastructure;

// Recognition is syntactic; PlannerOutputContract owns existence and new-file checks.
internal static class PlannerTargetCitation
{
    private static readonly Regex BacktickedCitation = new(@"`(?<citation>[^`\r\n]+)`", RegexOptions.CultureInvariant);
    private static readonly Regex Identifier = new(@"\A[A-Za-z_][A-Za-z0-9_.]+\z", RegexOptions.CultureInvariant);
    private static readonly Regex LegacyCsMarker = new(@"\.cs\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex RepositoryPath = new(
        @"\A(?<path>[^:`#\r\n]+?)(?:(?:::[^\s`]+)|(?::[0-9]+(?:-[0-9]+)?)|(?:#L[0-9]+(?:-L?[0-9]+)?))?\z",
        RegexOptions.CultureInvariant);

    internal static bool HasTargetCitation(string body)
    {
        foreach (Match match in BacktickedCitation.Matches(body))
        {
            var citation = match.Groups["citation"].Value;
            if (Identifier.IsMatch(citation))
            {
                return true;
            }

            if (TryGetRepositoryPath(citation, out var path))
            {
                // Preserve source/test paths as well as the operator-approved,
                // case-sensitive documentation prefixes.
                if (IsDocumentationText(path) &&
                    !HasLegacySourcePrefix(path) && !HasLegacyDocumentationPrefix(path))
                {
                    continue;
                }

                return true;
            }

            // Preserve the named legacy forms within a complete backticked span,
            // without re-running the old unanchored regex over the section body.
            if (HasLegacySourcePrefix(citation.Trim()) || LegacyCsMarker.IsMatch(citation))
            {
                return true;
            }
        }

        return false;
    }

    internal static bool TryGetRepositoryPath(string citation, out string path)
    {
        path = string.Empty;
        var match = RepositoryPath.Match(citation.Trim());
        if (!match.Success)
        {
            return false;
        }

        var candidate = match.Groups["path"].Value;
        var normalized = candidate.Replace('\\', '/');
        if (!normalized.Contains('/') || normalized.StartsWith('/') || candidate.Any(char.IsControl))
        {
            return false;
        }

        var segments = normalized.Split('/');
        var depth = 0;
        foreach (var segment in segments)
        {
            if (segment.Length == 0)
            {
                return false;
            }

            if (segment == "..")
            {
                if (--depth < 0)
                {
                    return false;
                }
            }
            else if (segment != ".")
            {
                depth++;
            }
        }

        var fileName = segments[^1];
        var extensionStart = fileName.LastIndexOf('.');
        if (fileName is "." or ".." || extensionStart <= 0 || extensionStart == fileName.Length - 1)
        {
            return false;
        }

        path = candidate;
        return true;
    }

    private static bool HasLegacySourcePrefix(string citation) =>
        citation.StartsWith("src/", StringComparison.OrdinalIgnoreCase) ||
        citation.StartsWith("src\\", StringComparison.OrdinalIgnoreCase) ||
        citation.StartsWith("tests/", StringComparison.OrdinalIgnoreCase) ||
        citation.StartsWith("tests\\", StringComparison.OrdinalIgnoreCase);

    private static bool IsDocumentationText(string path) =>
        path.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".markdown", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".rst", StringComparison.OrdinalIgnoreCase);

    private static bool HasLegacyDocumentationPrefix(string path)
    {
        var normalized = path.Replace('\\', '/');
        return normalized.StartsWith("docs/", StringComparison.Ordinal) ||
            normalized.StartsWith("scripts/", StringComparison.Ordinal) ||
            normalized.StartsWith("config/", StringComparison.Ordinal);
    }
}
