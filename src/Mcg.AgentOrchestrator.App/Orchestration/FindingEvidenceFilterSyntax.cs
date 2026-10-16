using System.Text.RegularExpressions;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Owns supported evidence-filter syntax and whether a selector names an expandable class.
internal static class FindingEvidenceFilterSyntax
{
    internal const int MaximumLength = 1024;

    private static readonly Regex BareClassNamePattern = new(
        @"^[A-Za-z_][A-Za-z0-9_.+`]*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex FilterTokenPattern = new(
        @"^(?:FullyQualifiedName(?:!~|~)[A-Za-z_][A-Za-z0-9_.]*|Category\s*!=\s*[A-Za-z_][A-Za-z0-9_.-]*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    internal static bool IsExpandableClassName(string className) =>
        BareClassNamePattern.IsMatch(className) &&
        !className.Contains('+') &&
        !className.Contains('`');

    internal static bool TryGetExpandableClass(
        string filter,
        out string requestedClass,
        out string filterPrefix)
    {
        const string fullyQualifiedNamePrefix = "FullyQualifiedName~";
        filterPrefix = string.Empty;
        requestedClass = filter;
        if (filter.StartsWith(fullyQualifiedNamePrefix, StringComparison.Ordinal))
        {
            filterPrefix = fullyQualifiedNamePrefix;
            requestedClass = filter[fullyQualifiedNamePrefix.Length..];
        }

        return IsExpandableClassName(requestedClass);
    }

    internal static bool IsSupported(string filter)
    {
        if (BareClassNamePattern.IsMatch(filter) ||
            FindingEvidenceRoundPlanner.TryExpandClasses(filter, out _))
        {
            return true;
        }

        var parenthesisDepth = 0;
        foreach (var character in filter)
        {
            if (character == '(')
            {
                parenthesisDepth++;
            }
            else if (character == ')' && --parenthesisDepth < 0)
            {
                return false;
            }
        }
        if (parenthesisDepth != 0)
        {
            return false;
        }

        var tokens = Regex.Split(filter, @"[&|]");
        if (tokens.Length == 0 || tokens.Any(string.IsNullOrWhiteSpace))
        {
            return false;
        }

        var hasPositiveSelection = false;
        foreach (var rawToken in tokens)
        {
            var token = rawToken.Trim().Trim('(', ')').Trim();
            if (token.Length == 0 ||
                token.Contains('(') ||
                token.Contains(')') ||
                !FilterTokenPattern.IsMatch(token))
            {
                return false;
            }

            hasPositiveSelection |= token.Contains("FullyQualifiedName~", StringComparison.OrdinalIgnoreCase);
        }

        return hasPositiveSelection;
    }
}
