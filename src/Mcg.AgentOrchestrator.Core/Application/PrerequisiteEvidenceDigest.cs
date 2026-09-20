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
/// The evidence an answer points at: the tokens shown to the later role, and the count of further
/// tokens the per-entry cap could not show. The omitted count is never discarded silently; it is
/// rendered into the entry line so the later role can see that the answer names more than is listed.
/// </summary>
internal sealed record PrerequisiteEvidenceTokens(IReadOnlyList<string> Shown, int OmittedCount);

/// <summary>
/// Renders answered prerequisite-evidence requests for a later same-goal role as bounded reference
/// lines. Pure by contract: it never touches <see cref="System.IO"/>, so a receipt path recorded in
/// an answer is propagated verbatim and is never opened, probed, or expanded at prompt-build time.
/// </summary>
internal static partial class PrerequisiteEvidenceDigest
{
    internal const string Heading = "## Answered Prerequisite Evidence";
    internal const string BudgetNotePrefix = "Budget note: prerequisite evidence trimmed for request ids: ";
    internal const string OmittedTokenSuffixFormat = " (+{0} more not shown)";
    internal const string TrimmedIdOverflowSuffixFormat = " (+{0} more trimmed ids on the task timeline)";
    internal const int SectionCharacterCap = 1_500;

    /// <summary>
    /// Upper bound on the trimmed ids the in-prompt budget note names. Stage 2 can only ever reduce
    /// the section to a single retained entry, so an unbounded note is the one way this section can
    /// exceed its own cap: at roughly a hundred answered requests in one goal the note alone passes
    /// <see cref="SectionCharacterCap"/>, and the segment is Fixed, so it would never be collapsed.
    /// Nothing is dropped silently by this bound - the complete trimmed list still leaves on
    /// <see cref="PrerequisiteEvidenceSection.TrimmedRequestIds"/> and reaches the task timeline
    /// through <see cref="PrerequisiteEvidenceTrimNote"/>, which the suffix points the reader at.
    /// </summary>
    internal const int MaxNamedTrimmedIds = 12;
    internal const int InlineVerbatimMaxChars = 600;
    internal const int SummaryMaxChars = 160;
    internal const int MaxEvidenceTokens = 4;
    internal const int EvidenceTokenMaxChars = 120;

    private const string PathGroup = "path";

    private const string Guidance =
        "Operator answers to prerequisite evidence requests raised earlier in this goal. " +
        "Open the named paths or ids yourself; receipts are referenced, not inlined.";

    private static readonly char[] TokenTrailingPunctuation = ['.', ',', ';', ':', ')', ']', '>', '"', '\''];

    /// <summary>
    /// Renders the section from entries ordered oldest answer first. The character cap is the only
    /// trim authority: nothing is dropped on a count alone, so a handful of short answers all survive
    /// in full. Under real budget pressure the oldest answers are reduced to the floor form first and
    /// only then dropped; every dropped id is named in an in-section budget note.
    /// </summary>
    internal static PrerequisiteEvidenceSection RenderSection(
        IReadOnlyList<PrerequisiteEvidenceEntry> entries,
        int currentBriefVersion)
    {
        if (entries.Count == 0)
        {
            return new PrerequisiteEvidenceSection([], []);
        }

        // Both rendered forms are built once per entry, so the trim stages below re-measure without
        // re-running the evidence pattern over answer text. BuildTaskBrief runs on every dashboard
        // and CLI preview refresh, so a rescan per measurement would be paid there too.
        var retained = entries.Select(entry => Prepare(entry, currentBriefVersion)).ToList();
        var trimmed = new List<string>();
        var floored = new HashSet<string>(StringComparer.Ordinal);

        // Stage 1: reduce full entries to the floor form, oldest first, before dropping anything.
        for (var index = 0; index < retained.Count && Measure(retained, floored, trimmed) > SectionCharacterCap; index++)
        {
            floored.Add(retained[index].RequestId);
        }

        // Stage 2: still over the cap, so drop whole entries oldest first. At least one answer is
        // always retained; a visible budget note carries the ids that did not fit.
        while (retained.Count > 1 && Measure(retained, floored, trimmed) > SectionCharacterCap)
        {
            trimmed.Add(retained[0].RequestId);
            retained.RemoveAt(0);
        }

        return new PrerequisiteEvidenceSection(
            Render(retained, floored, trimmed),
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
    /// Extracts the paths and ids an answer points at, from the recorded answer text only. When more
    /// are named than the per-entry cap shows, openable paths win the cap over bare ids and the
    /// remainder is counted rather than discarded.
    /// </summary>
    internal static PrerequisiteEvidenceTokens ExtractEvidence(string answerText)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var all = new List<(string Text, bool IsPath)>();
        foreach (Match match in EvidenceTokenPattern().Matches(answerText))
        {
            var token = match.Value.TrimEnd(TokenTrailingPunctuation);
            if (token.Length == 0 || !seen.Add(token))
            {
                continue;
            }

            all.Add((
                token.Length <= EvidenceTokenMaxChars ? token : token[..(EvidenceTokenMaxChars - 3)] + "...",
                match.Groups[PathGroup].Success));
        }

        if (all.Count <= MaxEvidenceTokens)
        {
            return new PrerequisiteEvidenceTokens(all.Select(token => token.Text).ToList(), 0);
        }

        // Regex.Matches is leftmost-first, so run ids and hashes stated before a receipt path would
        // otherwise displace the one reference the later role can actually open. Selection prefers
        // paths; rendering stays in answer order.
        var kept = all
            .Select((token, index) => (token.Text, token.IsPath, Index: index))
            .OrderByDescending(token => token.IsPath)
            .Take(MaxEvidenceTokens)
            .OrderBy(token => token.Index)
            .Select(token => token.Text)
            .ToList();
        return new PrerequisiteEvidenceTokens(kept, all.Count - kept.Count);
    }

    internal static IReadOnlyList<string> ExtractEvidenceTokens(string answerText) =>
        ExtractEvidence(answerText).Shown;

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
        var prepared = Prepare(entry, currentBriefVersion);
        return floorFormOnly ? prepared.FloorForm : prepared.FullForm;
    }

    private static RenderedEntry Prepare(PrerequisiteEvidenceEntry entry, int currentBriefVersion)
    {
        var tokens = ExtractEvidence(entry.AnswerText);
        var evidence = tokens.Shown.Count == 0
            ? "evidence: none named in the answer"
            : $"evidence: {string.Join(", ", tokens.Shown)}";
        if (tokens.OmittedCount > 0)
        {
            evidence += string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                OmittedTokenSuffixFormat,
                tokens.OmittedCount);
        }

        var summary = Summarize(entry.AnswerText);
        var answerBody = ShouldInlineVerbatim(entry.AnswerText, tokens.Shown)
            ? CollapseToSingleLine(entry.AnswerText)
            : summary;
        var answeredUnder = entry.AnsweredBriefVersion is { } briefVersion
            ? $"brief v{briefVersion}"
            : "an unknown brief version";

        return new RenderedEntry(
            entry.RequestId,
            $"- {entry.RequestId}: {Summarize(entry.Question)} → {answerBody} | {evidence} " +
                $"(answered under {answeredUnder}; current brief v{currentBriefVersion})",
            // The floor an answer is never trimmed below: id, one-line summary, evidence tokens.
            $"- {entry.RequestId}: {summary} | {evidence}");
    }

    private static List<string> Render(
        IReadOnlyList<RenderedEntry> retained,
        IReadOnlySet<string> floored,
        IReadOnlyList<string> trimmed)
    {
        var lines = new List<string> { Heading, Guidance };
        lines.AddRange(retained.Select(entry =>
            floored.Contains(entry.RequestId) ? entry.FloorForm : entry.FullForm));
        if (trimmed.Count > 0)
        {
            var named = trimmed.Count <= MaxNamedTrimmedIds ? trimmed : trimmed.Take(MaxNamedTrimmedIds).ToList();
            var overflow = trimmed.Count - named.Count;
            lines.Add(
                BudgetNotePrefix + string.Join(", ", named) + "." +
                (overflow == 0
                    ? string.Empty
                    : string.Format(
                        System.Globalization.CultureInfo.InvariantCulture,
                        TrimmedIdOverflowSuffixFormat,
                        overflow)));
        }

        lines.Add(string.Empty);
        return lines;
    }

    private static int Measure(
        IReadOnlyList<RenderedEntry> retained,
        IReadOnlySet<string> floored,
        IReadOnlyList<string> trimmed) =>
        string.Join(Environment.NewLine, Render(retained, floored, trimmed)).Length;

    // Windows absolute paths, labelled run/lane/request ids, bare timestamp run ids, separator-bearing
    // relative paths, and hash-like ids that carry both a digit and a hex letter (so ordinary words
    // never match). The path alternatives are named so the per-entry cap can prefer openable
    // references over bare ids.
    //
    // The relative-path alternative additionally requires a '.', '-' or '_' followed by a word
    // character somewhere in the same run of path characters. A separator alone does not make a
    // path: without that requirement "use and/or the cheaper lane" yields "and/or" and
    // "Recorded 2026/09/13 during the round" yields "2026/09/13", and because the alternative is
    // the named path group such a false positive is treated as openable and wins the per-entry cap
    // over a real hash or run id. Rejecting it in the pattern rather than after matching also keeps
    // the inner id reachable: "goal/bb2d2d5a" now yields "bb2d2d5a" instead of being consumed whole.
    [GeneratedRegex(
        @"(?<path>[A-Za-z]:[\\/][^\s,;""'()<>\[\]]+)" +
        @"|\b(?:run|lane|request|goal|task)[ _-]?ids?\s*[:=]\s*[^\s,;]+" +
        @"|\b[0-9]{8}T[0-9]{4,6}Z\b(?:-[\w.\-]+)?" +
        @"|(?<path>(?<![\w:./\\])(?=[\w.\-/\\]*[.\-_]\w)[\w.\-]+(?:[\\/][\w.\-]+)+)" +
        @"|\b(?:sha\d*:)?(?=[0-9a-fA-F]*[0-9])(?=[0-9a-fA-F]*[a-fA-F])[0-9a-fA-F]{7,64}\b",
        RegexOptions.CultureInvariant)]
    private static partial Regex EvidenceTokenPattern();

    private sealed record RenderedEntry(string RequestId, string FullForm, string FloorForm);
}
