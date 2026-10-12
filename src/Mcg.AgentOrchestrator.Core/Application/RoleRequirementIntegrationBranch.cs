using System.Text.RegularExpressions;

namespace Mcg.AgentOrchestrator.Core;

// Owns branch substitution in generated scope instructions, without rewriting supplied goal text.
internal static class RoleRequirementIntegrationBranch
{
    private static readonly Regex ScopeTokens = new(
        @"\bmain(?:\.\.\.HEAD|\.\.HEAD)\b|--name-only main HEAD\b|\bcurrent main\b|\b(?:Branch|branch)-behind-main\b",
        RegexOptions.CultureInvariant);

    public static IReadOnlyList<string> Apply(IReadOnlyList<string> lines, string? integrationBranch)
    {
        if (string.IsNullOrWhiteSpace(integrationBranch) ||
            string.Equals(integrationBranch, "main", StringComparison.Ordinal))
            return lines;

        string[]? rewritten = null;
        for (var index = 0; index < lines.Count; index++)
        {
            // A single pass and evaluator keep inserted branch text literal, even when it contains tokens.
            var line = ScopeTokens.Replace(lines[index], match =>
                match.Value.Replace("main", integrationBranch, StringComparison.Ordinal));
            if (string.Equals(line, lines[index], StringComparison.Ordinal))
                continue;

            rewritten ??= lines.ToArray();
            rewritten[index] = line;
        }

        return rewritten is null ? lines : rewritten;
    }
}
