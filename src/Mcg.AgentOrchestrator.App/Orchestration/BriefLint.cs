using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

public enum BriefLintSeverity { BlocksDispatch, BlocksCliStart, Advisory }

public sealed record BriefLintFinding(string Kind, BriefLintSeverity Severity, string Message, string Remedy)
{
    public string SeverityToken => Severity switch
    {
        BriefLintSeverity.BlocksDispatch => "blocks-dispatch",
        BriefLintSeverity.BlocksCliStart => "blocks-cli-start",
        BriefLintSeverity.Advisory => "advisory",
        _ => throw new ArgumentOutOfRangeException(nameof(Severity))
    };
}

/// <summary>Pure early warnings about brief text; findings never authorize or block execution.</summary>
public static partial class BriefLint
{
    internal const string PreChangeFailureRemedy =
        "Make the pre-change half a Reviewer reading, or a committed negative-control test that runs on the candidate.";

    public static IReadOnlyList<BriefLintFinding> Lint(string briefText)
    {
        ArgumentNullException.ThrowIfNull(briefText);
        var text = briefText.ReplaceLineEndings("\n");
        var findings = new List<(int Offset, BriefLintFinding Finding)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string kind, BriefLintSeverity severity, int offset, int length, string remedy, bool deduplicate = true)
        {
            var matched = text.Substring(offset, length);
            if (!deduplicate || seen.Add($"{kind}:{matched}"))
                findings.Add((offset, new(kind, severity,
                    $"Matched \"{Whitespace().Replace(matched, " ")}\" near \"{Context(text, offset, length)}\".", remedy)));
        }

        for (var offset = WorkerTargetTextRules.FindGitDirectoryReference(text); offset >= 0;
             offset = WorkerTargetTextRules.FindGitDirectoryReference(text, offset + 4))
            Add("git-directory-reference", BriefLintSeverity.BlocksDispatch, offset, 4,
                "Rephrase incidental Git-directory references; Git metadata belongs to the conductor.");

        var skillOffset = WorkerTargetTextRules.FindUnscopedSkillDefinition(text);
        if (skillOffset >= 0)
            Add("skill-definition-file", BriefLintSeverity.BlocksDispatch, skillOffset, "SKILL.md".Length,
                "Include the exact .agents/skills path when a repository skill is the intended target.");

        var inAcceptanceCriteria = false;
        var lineOffset = 0;
        foreach (var line in text.Split('\n'))
        {
            var heading = SectionHeading().Match(line);
            if (heading.Success)
                inAcceptanceCriteria = heading.Groups[1].Value == "##" &&
                    heading.Groups[2].Value.Trim().StartsWith("Acceptance criteria", StringComparison.OrdinalIgnoreCase);
            else if (inAcceptanceCriteria && NumberedLine().IsMatch(line))
            {
                var match = PostLandingPhrase().Match(line);
                if (match.Success)
                    Add("post-landing-criterion", BriefLintSeverity.BlocksDispatch,
                        lineOffset + match.Index, match.Length,
                        "Move the step into prose outside the numbered acceptance criteria.", deduplicate: false);
                var preChange = PreChangeFailureCriterion().Match(line);
                if (preChange.Success)
                {
                    var anchor = preChange.Groups["anchor"];
                    Add("pre-change-failure-criterion", BriefLintSeverity.BlocksDispatch,
                        lineOffset + anchor.Index, anchor.Length, PreChangeFailureRemedy, deduplicate: false);
                }
            }
            lineOffset += line.Length + 1;
        }

        var tokens = GoalReadinessPreflight.EnumerateTokens(text).ToArray();
        foreach (var word in GoalReadinessPreflight.FindHighRiskSignals(text))
        {
            var token = tokens.First(token => text.AsSpan(token.Start, token.Length).Equals(word, StringComparison.OrdinalIgnoreCase));
            Add("readiness-high-risk-word", BriefLintSeverity.BlocksCliStart, token.Start, token.Length,
                "Rephrase incidental risk language, or satisfy readiness and confirm the intended high-risk start.");
        }

        // The normalizer inserts newlines; align its output with LF input to locate only actual splits.
        var normalized = MarkdownHeadingNormalizer.SeparateInlineAtxHeadings(text);
        for (int source = 0, output = 0; output < normalized.Length; output++)
        {
            if (source < text.Length && normalized[output] == text[source])
            {
                source++;
                continue;
            }

            var end = source;
            while (end < text.Length && text[end] == '#') end++;
            Add("inline-heading-split", BriefLintSeverity.Advisory, source, end - source,
                "Put the heading on its own line or rephrase the inline hash sequence before copying it into worker output.");
        }

        if (!TestRemovalBullet().IsMatch(text))
        {
            foreach (Match match in RemovalLanguage().Matches(text))
                Add("missing-test-removal-bullet", BriefLintSeverity.Advisory, match.Index, match.Length,
                    "Declare each removed test as a - test-removal: Class.Method bullet in the acceptance criteria.");
        }

        return findings.OrderBy(item => item.Finding.Severity).ThenBy(item => item.Offset)
            .Select(item => item.Finding).ToArray();
    }

    private static string Context(string text, int offset, int length)
    {
        var start = Math.Max(0, offset - 20);
        var end = Math.Min(text.Length, offset + length + 20);
        return (start > 0 ? "…" : "") + Whitespace().Replace(text[start..end], " ") +
            (end < text.Length ? "…" : "");
    }

    [GeneratedRegex(@"after this goal lands|after the goal lands|after landing|post-landing|once this goal has landed|once the goal has landed", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    internal static partial Regex PostLandingPhrase();

    [GeneratedRegex(@"\b(?<path>(?<directory>src/(?:[A-Za-z0-9_.-]+/)+)(?<class>[A-Z]\w*)\.[A-Za-z_]\w*\.cs)\b[^\r\n]{0,40}?(?i:\b(?:new file|newly added|new|added|create|creates|created)\b)", RegexOptions.CultureInvariant)]
    internal static partial Regex NewPartialFilePath();

    [GeneratedRegex(@"^(?=.*\b(?:fail|fails|failing|red)\b)(?=.*\b(?:Acceptance\s+(?:executes|runs)|assigned\s+to\s+Acceptance)\b)(?!.*\bnegative[ -]control\b.*\bcandidate\b)(?!.*\bcandidate\b.*\bnegative[ -]control\b).*?\b(?<anchor>pre-change(?![-\w])|prechange(?![-\w])|before the change|prior code|prior implementation|old code|unchanged code)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    internal static partial Regex PreChangeFailureCriterion();

    [GeneratedRegex(@"^\s*(#{1,2})[ \t]+(.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex SectionHeading();

    [GeneratedRegex(@"^\s*\d+[.)]\s", RegexOptions.CultureInvariant)]
    private static partial Regex NumberedLine();

    [GeneratedRegex(@"^\s*- test-removal:", RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TestRemovalBullet();

    [GeneratedRegex(@"\b(?:remove|removes|removed|removing|drop|drops|dropped|dropping)\b[\s\S]{0,40}?\b(?:tests?|facts?)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RemovalLanguage();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
