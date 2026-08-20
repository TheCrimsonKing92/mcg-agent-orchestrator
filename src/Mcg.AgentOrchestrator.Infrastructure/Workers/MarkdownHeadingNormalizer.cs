using System.Text.RegularExpressions;

namespace Mcg.AgentOrchestrator.Infrastructure;

/// <summary>
/// grok-cli --output-format plain concatenates the last thinking sentence onto the first ATX heading.
/// Heading contracts require start-of-line match. Insert a newline before mid-line ## headings.
/// The lookbehind excludes # so a start-of-line ## is not treated as inline and demoted to #.
/// </summary>
internal static partial class MarkdownHeadingNormalizer
{
    public static string SeparateInlineAtxHeadings(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        var normalized = text.ReplaceLineEndings("\n");
        return InlineAtxHeading().Replace(normalized, "\n$1");
    }

    [GeneratedRegex(@"(?m)(?<=[^\s#])(#{1,6}[ \t]+\S)")]
    private static partial Regex InlineAtxHeading();
}
