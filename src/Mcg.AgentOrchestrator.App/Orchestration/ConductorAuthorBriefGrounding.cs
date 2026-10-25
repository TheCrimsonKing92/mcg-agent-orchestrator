using System.Text.RegularExpressions;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class ConductorAuthorBriefGrounding
{
    internal static bool TryGround(string? brief, string answerText, IReadOnlyList<string>? briefCitations,
        string escalationReason, out IReadOnlyList<string> citations)
    {
        citations = [];
        if (escalationReason is not ("missing-evidence" or "acceptance-weakening" or "authority-widening") ||
            string.IsNullOrWhiteSpace(brief) || briefCitations is not { Count: > 0 }) return false;

        var normalizedBrief = Normalize(brief);
        var normalizedAnswer = Normalize(answerText);
        var normalizedCitations = briefCitations.Select(citation => Normalize(citation)).ToArray();
        if (normalizedCitations.Any(citation => citation.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length < 5 ||
                !normalizedBrief.Contains(citation, StringComparison.Ordinal) ||
                !normalizedAnswer.Contains(citation, StringComparison.Ordinal))) return false;

        var briefTokens = Tokens(normalizedBrief).ToHashSet(StringComparer.Ordinal);
        // Only explicit scope declarations grant directory membership; a mention alone does not.
        var scopedDirectories = brief.Split('\n').Select(line => line.Trim())
            .Where(line => line.StartsWith("Scope:", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("In scope:", StringComparison.OrdinalIgnoreCase))
            .SelectMany(line => Tokens(Normalize(line[(line.IndexOf(':') + 1)..])))
            .Where(token => token.EndsWith("/", StringComparison.Ordinal)).ToArray();
        foreach (var word in normalizedAnswer.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var path = word.TrimEnd('.').Replace('\\', '/');
            if (!IsPath(path) || briefTokens.Contains(path)) continue;
            if (path.Split('/').Any(segment => segment is "." or "..") ||
                !scopedDirectories.Any(directory => path.StartsWith(directory, StringComparison.Ordinal))) return false;
        }

        citations = normalizedCitations;
        return true;
    }

    private static string Normalize(string text) => Regex.Replace(text, @"\s+", " ").Trim();

    private static IEnumerable<string> Tokens(string text) =>
        text.Split([' ', '`', '"', '\'', '(', ')', '[', ']', ',', ';'], StringSplitOptions.RemoveEmptyEntries)
            .Select(token => token.TrimEnd(':', '.').Replace('\\', '/'));

    private static bool IsPath(string word) =>
        new[] { "src/", "tests/", "config/", "docs/", "scripts/" }
            .Any(prefix => word.StartsWith(prefix, StringComparison.Ordinal)) ||
        new[] { ".cs", ".csproj", ".json", ".ps1", ".md", ".props", ".targets" }
            .Any(extension => word.EndsWith(extension, StringComparison.Ordinal));
}
