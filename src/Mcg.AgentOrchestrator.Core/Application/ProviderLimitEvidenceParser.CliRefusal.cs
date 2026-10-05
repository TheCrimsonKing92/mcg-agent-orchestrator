using System.Text.RegularExpressions;

namespace Mcg.AgentOrchestrator.Core;

public static partial class ProviderLimitEvidenceParser
{
    // Bare stdout is diagnostic evidence only when the entire failed invocation refused work.
    // Do not relax IsProviderLimitEvidenceLine: it also consumes arbitrary worker output.
    public static IReadOnlyList<string> GetCliRefusalLines(int exitCode, IEnumerable<string> standardOutputLines)
    {
        if (exitCode == 0)
        {
            return [];
        }

        var lines = standardOutputLines.Where(line => !string.IsNullOrWhiteSpace(line)).ToArray();
        return lines.Length > 0 && lines.All(line =>
            !char.IsWhiteSpace(line[0]) &&
            !LooksLikeSourceLocationEcho(line) &&
            CliRefusalShape().IsMatch(line))
                ? lines.Distinct(StringComparer.Ordinal).ToArray()
                : [];
    }

    // The caller must first establish refusal-only stdout via GetCliRefusalLines.
    public static bool IsCliRefusalLimitLine(string line) => ContainsLimitText(line);

    [GeneratedRegex(@"^(?:You['\u2019]ve hit your (?:weekly|session|daily|usage) limit\b|Failed to authenticate\b|API Error:?\s*[1-5]\d{2}\b)", RegexOptions.CultureInvariant)]
    private static partial Regex CliRefusalShape();
}
