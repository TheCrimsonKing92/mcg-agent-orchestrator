using System.Text.RegularExpressions;

namespace Mcg.AgentOrchestrator.Core;

// Owns branch substitution in generated scope instructions, without rewriting supplied goal text.
internal static class RoleRequirementIntegrationBranch
{
    private static readonly Regex ScopeTokens = new(
        @"\b(?<branch>main)(?:\.\.\.HEAD|\.\.HEAD)\b|--name-only (?<branch>main) HEAD\b|\bcurrent (?<branch>main)\b|\b(?:Branch|branch)-behind-(?<branch>main)\b",
        RegexOptions.CultureInvariant);

    public static IReadOnlyList<string> Apply(IReadOnlyList<string> lines, string? integrationBranch)
    {
        if (string.IsNullOrWhiteSpace(integrationBranch))
            return lines;

        string[]? rewritten = null;
        for (var index = 0; index < lines.Count; index++)
        {
            // A single pass and evaluator keep inserted branch text literal, even when it contains tokens.
            var line = ScopeTokens.Replace(lines[index], match =>
            {
                var branch = match.Groups["branch"];
                var offset = branch.Index - match.Index;
                return match.Value[..offset] + integrationBranch + match.Value[(offset + branch.Length)..];
            });
            if (string.Equals(line, lines[index], StringComparison.Ordinal))
                continue;

            rewritten ??= lines.ToArray();
            rewritten[index] = line;
        }

        return rewritten is null ? lines : rewritten;
    }
}
