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
            string? previousPath = null;
            foreach (Match citation in Regex.Matches(text, @"`([^`\r\n]+)`", RegexOptions.CultureInvariant))
            {
                var token = citation.Groups[1].Value;
                var parsed = BoardFillCitation.Parse(token);
                if (parsed.IsContinuation)
                {
                    if (previousPath is null)
                    {
                        citationFailures.Add($"{token}: continuation has no preceding file citation");
                        continue;
                    }
                    parsed = parsed with { Path = previousPath };
                }
                else if (!parsed.HasLineSuffix && !token.Contains('/') && !token.Contains('\\') &&
                    !Regex.IsMatch(token, @"\.[a-z0-9]+$", RegexOptions.CultureInvariant)) continue;
                previousPath = parsed.Path;
                var count = repository.TrackedLineCount(mainHead, parsed.Path);
                if (count is null && (parsed.HasLineSuffix || !repository.IsTrackedDirectory(mainHead, parsed.Path)))
                    citationFailures.Add($"{token}: file is not tracked at {mainHead}");
                else if (count is { } lines && parsed.HasLineSuffix && !parsed.InRange(lines))
                    citationFailures.Add($"{token}: line is outside file (line count {count})");
            }
        }
        var bulletedLines = UnfencedLines(SectionText("Acceptance criteria"))
            .Select(line => line.Text)
            .Where(line => line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal))
            .ToArray();
        var numberedCriteria = criteria.Count == 0 || bulletedLines.Length == 0;
        var developerDeferred = criteria.Any(criterion => criterion.Contains("tests: deferred - ", StringComparison.Ordinal) &&
            criterion.TrimEnd().EndsWith("Developer owns; Acceptance executes. TEST-VERIFIABLE.", StringComparison.Ordinal));
        var buildItemCount = SectionText("What to build").ReplaceLineEndings("\n").Split('\n')
            .Count(line => Regex.IsMatch(line.TrimStart(), @"^\d+\.", RegexOptions.CultureInvariant));
        var postLandingCriteria = criteria.Select((criterion, index) =>
                (Index: index + 1, Match: BriefLint.PostLandingPhrase().Match(criterion)))
            .Where(criterion => criterion.Match.Success).ToArray();
        var newPartialFiles = markdown.ReplaceLineEndings("\n").Split('\n')
            .SelectMany(line => BriefLint.NewPartialFilePath().Matches(line).Cast<Match>())
            .Select(match => (Path: match.Groups["path"].Value,
                Sibling: match.Groups["directory"].Value + match.Groups["class"].Value + ".cs"))
            .Distinct()
            .Where(file => repository.TrackedLineCount(mainHead, file.Path) is null &&
                repository.TrackedLineCount(mainHead, file.Sibling) is not null)
            .Select(file => file.Path).ToArray();
        var preChangeCriteria = UnfencedLines(SectionText("Acceptance criteria"))
            .Where(line => BriefLint.NumberedLine().IsMatch(line.Text))
            .Select(line =>
                (Index: BriefLint.NumberedLine().Match(line.Text).Groups["number"].Value,
                    Match: BriefLint.PreChangeFailureCriterion().Match(line.Text)))
            .Where(criterion => criterion.Match.Success).ToArray();
        return
        [
            new("sections", missing.Length == 0, missing.Length == 0 ? "All four sections present." : $"Missing headings: {string.Join(", ", missing)}"),
            new("criteria-present", criteria.Count > 0, criteria.Count > 0 ? $"{criteria.Count} declared criteria." : "Acceptance criteria contains no declared criteria."),
            new("owner-sentence", ownerFailures.Length == 0, ownerFailures.Length == 0 ? "Every declared criterion has an owner sentence and verification class." : $"Missing owner sentence: {string.Join(" | ", ownerFailures)}"),
            new("premise-citations", citationFailures.Count == 0, citationFailures.Count == 0 ? "All premise file citations resolve at main HEAD." : string.Join(" | ", citationFailures)),
            new("numbered-criteria", numberedCriteria, criteria.Count == 0 ? "No declared criteria; criteria-present reports the absence." :
                numberedCriteria ? "No bulleted criteria outside fenced code blocks." : $"Observed bulleted lines: {string.Join(" | ", bulletedLines)}"),
            new("developer-deferred-criterion", criteria.Count == 0 || developerDeferred, criteria.Count == 0 ?
                "No declared criteria; criteria-present reports the absence." : developerDeferred ? "Developer deferred-tests criterion present." :
                $"Observed {criteria.Count} declared criteria; none contains tests: deferred - and ends with Developer owns; Acceptance executes. TEST-VERIFIABLE."),
            new("build-item-count", buildItemCount <= 4, $"Observed {buildItemCount} numbered build items; maximum is 4."),
            new("post-landing-criterion", postLandingCriteria.Length == 0, postLandingCriteria.Length == 0 ?
                "No declared criterion describes a post-landing step." :
                string.Join(" | ", postLandingCriteria.Select(criterion =>
                    $"criterion {criterion.Index} matched \"{criterion.Match.Value}\"")) +
                ". Move the step into prose outside the numbered acceptance criteria."),
            new("new-partial-file", newPartialFiles.Length == 0, newPartialFiles.Length == 0 ?
                "No draft path names a new partial file of an existing class." :
                string.Join(", ", newPartialFiles) +
                ". Extract a separately named type in its own file instead of adding a new partial file of an existing class."),
            new("pre-change-failure-criterion", preChangeCriteria.Length == 0, preChangeCriteria.Length == 0 ?
                "No declared criterion demands a pre-change failure with Acceptance executing." :
                string.Join(" | ", preChangeCriteria.Select(criterion =>
                    $"criterion {criterion.Index} matched \"{criterion.Match.Groups["anchor"].Value}\"")) +
                ". " + BriefLint.PreChangeFailureRemedy)
        ];

        string SectionText(string name)
        {
            var section = headings.FirstOrDefault(heading => heading.Groups[1].Value.Trim()
                .Equals(name, StringComparison.OrdinalIgnoreCase));
            if (section is null) return string.Empty;
            var remainder = markdown[(section.Index + section.Length)..].ReplaceLineEndings("\n");
            var end = UnfencedLines(remainder).Where(line => Headings.IsMatch(line.Text))
                .Select(line => (int?)line.Index).FirstOrDefault() ?? remainder.Length;
            return remainder[..end];
        }
    }

    private static IEnumerable<(string Text, int Index)> UnfencedLines(string text)
    {
        char fenceCharacter = '\0';
        var fenceLength = 0;
        var index = 0;
        foreach (var line in text.ReplaceLineEndings("\n").Split('\n'))
        {
            var lineIndex = index;
            index += line.Length + 1;
            var trimmed = line.TrimStart();
            var markerLength = trimmed.Length > 0 && trimmed[0] is '`' or '~'
                ? trimmed.TakeWhile(character => character == trimmed[0]).Count() : 0;
            if (fenceLength == 0)
            {
                if (markerLength >= 3)
                {
                    fenceCharacter = trimmed[0];
                    fenceLength = markerLength;
                }
                else yield return (trimmed, lineIndex);
            }
            else if (markerLength >= fenceLength && trimmed[0] == fenceCharacter &&
                     string.IsNullOrWhiteSpace(trimmed[markerLength..]))
            {
                fenceLength = 0;
            }
        }
    }
}
