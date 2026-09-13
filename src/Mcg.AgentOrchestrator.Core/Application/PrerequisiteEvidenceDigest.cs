using System.Text.RegularExpressions;

namespace Mcg.AgentOrchestrator.Core;

internal sealed record PrerequisiteEvidenceEntry(
    string RequestId,
    string Question,
    string AnswerText,
    int? AnsweredBriefVersion);

internal sealed record PrerequisiteEvidenceSection(
    IReadOnlyList<string> Lines,
    IReadOnlyList<string> TrimmedRequestIds);

/// <summary>
/// Renders answered prerequisite-evidence requests for a later same-goal role as bounded reference
/// lines. Pure by contract: it never touches <see cref="System.IO"/>, so a receipt path recorded in
/// an answer is propagated verbatim and is never opened, probed, or expanded at prompt-build time.
/// </summary>
internal static partial class PrerequisiteEvidenceDigest
{
    internal const string Heading = "## Answered Prerequisite Evidence";
    internal const string BudgetNotePrefix = "Budget note: prerequisite evidence trimmed for request ids: ";
    internal const int SectionCharacterCap = 1_500;
    internal const int MaxEntries = 4;
    internal const int InlineVerbatimMaxChars = 600;
    internal const int SummaryMaxChars = 160;
    internal const int MaxEvidenceTokens = 4;
    internal const int EvidenceTokenMaxChars = 120;

    private const string Guidance =
        "Operator answers to prerequisite evidence requests raised earlier in this goal. " +
        "Open the named paths or ids yourself; receipts are referenced, not inlined.";

    private static readonly char[] TokenTrailingPunctuation = ['.', ',', ';', ':', ')', ']', '>', '"', '\''];

    /// <summary>
    /// Renders the section from entries ordered oldest answer first. Trimming removes the oldest
    /// answers first and never reduces a retained entry below request id, one-line summary, and
    /// evidence tokens; every trimmed id is named in an in-section budget note.
    /// </summary>
    internal static PrerequisiteEvidenceSection RenderSection(
        IReadOnlyList<PrerequisiteEvidenceEntry> entries,
        int currentBriefVersion)
    {
        if (entries.Count == 0)
        {
            return new PrerequisiteEvidenceSection([], []);
        }

        // Oldest-first trimming: the entry cap drops the oldest answers, keeping the newest.
        var retained = entries.Skip(Math.Max(0, entries.Count - MaxEntries)).ToList();
        var trimmed = entries.Take(Math.Max(0, entries.Count - MaxEntries))
            .Select(entry => entry.RequestId)
            .ToList();
        var floored = new HashSet<string>(StringComparer.Ordinal);

        // Stage 1: reduce full entries to the floor form, oldest first, before dropping anything.
        for (var index = 0; index < retained.Count && Measure(retained, floored, trimmed, currentBriefVersion) > SectionCharacterCap; index++)
        {
            floored.Add(retained[index].RequestId);
        }

        // Stage 2: still over the cap, so drop whole entries oldest first. At least one answer is
        // always retained; a visible budget note carries the ids that did not fit.
        while (retained.Count > 1 && Measure(retained, floored, trimmed, currentBriefVersion) > SectionCharacterCap)
        {
            trimmed.Add(retained[0].RequestId);
            retained.RemoveAt(0);
        }

        return new PrerequisiteEvidenceSection(
            Render(retained, floored, trimmed, currentBriefVersion),
            trimmed);
    }

    internal static string Summarize(string value)
    {
        var collapsed = CollapseToSingleLine(value);
        return collapsed.Length <= SummaryMaxChars
            ? collapsed
            : collapsed[..(SummaryMaxChars - 3)] + "...";
    }

    /// <summary>
    /// Single-line rendering is what stops answer text forging a brief section heading or a typed
    /// projection boundary: an embedded "## " can never start a line.
    /// </summary>
    internal static string CollapseToSingleLine(string value)
    {
        var collapsed = value.Trim().ReplaceLineEndings(" ");
        while (collapsed.Contains("  ", StringComparison.Ordinal))
        {
            collapsed = collapsed.Replace("  ", " ", StringComparison.Ordinal);
        }

        return collapsed;
    }

    /// <summary>
    /// Extracts the paths and ids an answer points at, from the recorded answer text only.
    /// </summary>
    internal static IReadOnlyList<string> ExtractEvidenceTokens(string answerText)
    {
        var tokens = new List<string>();
        foreach (Match match in EvidenceTokenPattern().Matches(answerText))
        {
            var token = match.Value.TrimEnd(TokenTrailingPunctuation);
            if (token.Length == 0 || tokens.Contains(token, StringComparer.Ordinal))
            {
                continue;
            }

            tokens.Add(token.Length <= EvidenceTokenMaxChars
                ? token
                : token[..(EvidenceTokenMaxChars - 3)] + "...");
            if (tokens.Count == MaxEvidenceTokens)
            {
                break;
            }
        }

        return tokens;
    }

    /// <summary>
    /// Short answers that name nothing the worker could open are more useful verbatim than
    /// summarized. Anything longer, or anything carrying a reference, degrades to reference form.
    /// </summary>
    internal static bool ShouldInlineVerbatim(string answerText, IReadOnlyList<string> evidenceTokens) =>
        evidenceTokens.Count == 0 && answerText.Trim().Length <= InlineVerbatimMaxChars;

    internal static string RenderEntry(
        PrerequisiteEvidenceEntry entry,
        bool floorFormOnly,
        int currentBriefVersion)
    {
        var tokens = ExtractEvidenceTokens(entry.AnswerText);
        var evidence = tokens.Count == 0
            ? "evidence: none named in the answer"
            : $"evidence: {string.Join(", ", tokens)}";
        if (floorFormOnly)
        {
            // The floor an answer is never trimmed below: id, one-line summary, evidence tokens.
            return $"- {entry.RequestId}: {Summarize(entry.AnswerText)} | {evidence}";
        }

        var answerBody = ShouldInlineVerbatim(entry.AnswerText, tokens)
            ? CollapseToSingleLine(entry.AnswerText)
            : Summarize(entry.AnswerText);
        var answeredUnder = entry.AnsweredBriefVersion is { } briefVersion
            ? $"brief v{briefVersion}"
            : "an unknown brief version";
        return $"- {entry.RequestId}: {Summarize(entry.Question)} → {answerBody} | {evidence} " +
            $"(answered under {answeredUnder}; current brief v{currentBriefVersion})";
    }

    private static List<string> Render(
        IReadOnlyList<PrerequisiteEvidenceEntry> retained,
        IReadOnlySet<string> floored,
        IReadOnlyList<string> trimmed,
        int currentBriefVersion)
    {
        var lines = new List<string> { Heading, Guidance };
        lines.AddRange(retained.Select(entry =>
            RenderEntry(entry, floored.Contains(entry.RequestId), currentBriefVersion)));
        if (trimmed.Count > 0)
        {
            lines.Add(BudgetNotePrefix + string.Join(", ", trimmed) + ".");
        }

        lines.Add(string.Empty);
        return lines;
    }

    private static int Measure(
        IReadOnlyList<PrerequisiteEvidenceEntry> retained,
        IReadOnlySet<string> floored,
        IReadOnlyList<string> trimmed,
        int currentBriefVersion) =>
        string.Join(Environment.NewLine, Render(retained, floored, trimmed, currentBriefVersion)).Length;

    // Windows absolute paths, labelled run/lane/request ids, separator-bearing relative paths, and
    // hash-like ids that carry both a digit and a hex letter (so ordinary words never match).
    [GeneratedRegex(
        @"[A-Za-z]:[\\/][^\s,;""'()<>\[\]]+" +
        @"|\b(?:run|lane|request|goal|task)[ _-]?ids?\s*[:=]\s*[^\s,;]+" +
        @"|(?<![\w:./\\])[\w.\-]+(?:[\\/][\w.\-]+)+" +
        @"|\b(?:sha\d*:)?(?=[0-9a-fA-F]*[0-9])(?=[0-9a-fA-F]*[a-fA-F])[0-9a-fA-F]{7,64}\b",
        RegexOptions.CultureInvariant)]
    private static partial Regex EvidenceTokenPattern();
}
