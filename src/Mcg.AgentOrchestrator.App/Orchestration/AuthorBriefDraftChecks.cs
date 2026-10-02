using System.Globalization;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record AuthorBriefDraftCheck(string Name, bool Passed, string Detail);

internal static class AuthorBriefDraftChecks
{
    private static readonly string[] Sections = ["Measured premise", "What to build", "Acceptance criteria", "Scope"];
    private static readonly Regex Headings = new(@"^##[ \t]+([^\r\n]+)", RegexOptions.Multiline | RegexOptions.CultureInvariant);
    private static readonly Regex Owner = new(
        @"\b[A-Za-z]+(?: [A-Za-z]+)? owns;\s*[A-Za-z]+(?: [A-Za-z]+)? executes\.\s*(?:TEST-VERIFIABLE|REAL-WORLD-DEPENDENT)\.?\s*$",
        RegexOptions.CultureInvariant);

    internal static IReadOnlyList<AuthorBriefDraftCheck> Run(string markdown, string mainHead,
        IAuthorBriefDraftRepository repository)
    {
        var headings = Headings.Matches(markdown).Cast<Match>().ToArray();
        var missing = Sections.Where(section => !headings.Any(heading =>
            heading.Groups[1].Value.Trim().Equals(section, StringComparison.OrdinalIgnoreCase))).ToArray();
        var criteria = AcceptanceCriteriaParser.ParseDeclared(markdown);
        var ownerFailures = criteria.Where(criterion => !Owner.IsMatch(criterion)).ToArray();
        var citationFailures = new List<string>();
        var premise = headings.FirstOrDefault(heading => heading.Groups[1].Value.Trim()
            .Equals("Measured premise", StringComparison.OrdinalIgnoreCase));
        if (premise is not null)
        {
            var end = headings.FirstOrDefault(heading => heading.Index > premise.Index)?.Index ?? markdown.Length;
            var text = markdown[(premise.Index + premise.Length)..end];
            foreach (Match citation in Regex.Matches(text, @"`([^`\r\n]+)`", RegexOptions.CultureInvariant))
            {
                var token = citation.Groups[1].Value;
                var lineSuffix = Regex.Match(token, @":([0-9]+)$", RegexOptions.CultureInvariant);
                var path = lineSuffix.Success ? token[..lineSuffix.Index] : token;
                if (!path.Contains('/') && !path.Contains('\\') &&
                    !Regex.IsMatch(path, @"\.[a-z0-9]+$", RegexOptions.CultureInvariant)) continue;
                if (path.StartsWith("./", StringComparison.Ordinal)) path = path[2..];
                var count = repository.TrackedLineCount(mainHead, path);
                if (count is null)
                    citationFailures.Add($"{token}: file is not tracked at {mainHead}");
                else if (lineSuffix.Success && (!int.TryParse(lineSuffix.Groups[1].Value,
                    NumberStyles.None, CultureInfo.InvariantCulture, out var line) || line < 1 || line > count))
                    citationFailures.Add($"{token}: line is outside file (line count {count})");
            }
        }
        return
        [
            new("sections", missing.Length == 0, missing.Length == 0 ? "All four sections present." : $"Missing headings: {string.Join(", ", missing)}"),
            new("criteria-present", criteria.Count > 0, criteria.Count > 0 ? $"{criteria.Count} declared criteria." : "Acceptance criteria contains no declared criteria."),
            new("owner-sentence", ownerFailures.Length == 0, ownerFailures.Length == 0 ? "Every declared criterion has an owner sentence and verification class." : $"Missing owner sentence: {string.Join(" | ", ownerFailures)}"),
            new("premise-citations", citationFailures.Count == 0, citationFailures.Count == 0 ? "All premise file citations resolve at main HEAD." : string.Join(" | ", citationFailures))
        ];
    }
}
