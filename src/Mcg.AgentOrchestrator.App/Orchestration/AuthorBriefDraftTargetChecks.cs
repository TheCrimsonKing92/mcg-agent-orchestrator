using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Validates declared changes against the same immutable main-head snapshot as premise citations.
internal static class AuthorBriefDraftTargetChecks
{
    private const RegexOptions Words = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    private static readonly Regex Citations = new(@"`([^`\r\n]+)`", RegexOptions.CultureInvariant);
    private static readonly Regex Heading = new(@"^\s*(#{1,6})\s+(.+?)\s*#*\s*$");
    private static readonly Regex ListItem = new(@"^\s*(?:[-+*]|\d+[.)])\s+");
    // Consume comments and ordinary strings before looking for constructors, so examples in either
    // cannot masquerade as live rows. Constructors may span lines and have trailing comments.
    private static readonly Regex CeilingEntries = new(
        """
        //[^\r\n]*|/\*[\s\S]*?\*/|\bnew\s+SourceSizeCeiling\s*\(\s*"(?<path>[^"\r\n]+)"\s*,|@"(?:""|[^"])*"|"(?:\\.|[^"\\])*"
        """,
        RegexOptions.CultureInvariant);

    internal static IReadOnlyList<AuthorBriefDraftCheck> Run(string markdown, string mainHead,
        IAuthorBriefDraftRepository repository)
    {
        var lines = ProseLines(markdown).ToArray();
        return [Check("modification-targets", ModificationFailures,
                "All modification targets resolve at main HEAD or are explicitly new."),
            Check("ratchet-rows", RatchetFailures, "All cited ratchet rows exist or are explicitly added.")];

        AuthorBriefDraftCheck Check(string name, Func<IEnumerable<string>> inspect, string success)
        {
            try
            {
                var failures = inspect().ToArray();
                return new(name, failures.Length == 0, failures.Length == 0 ? success : string.Join(" | ", failures));
            }
            catch (InvalidOperationException exception)
            {
                return new(name, false, $"Validation could not be completed at {mainHead}: {exception.Message}");
            }
        }

        IEnumerable<string> ModificationFailures()
        {
            var reported = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in TargetItems(lines))
            {
                var previousEnd = 0;
                var citations = Citations.Matches(item).Cast<Match>().ToArray();
                for (var index = 0; index < citations.Length; index++)
                {
                    var citation = citations[index];
                    var prefix = item[previousEnd..citation.Index];
                    previousEnd = citation.Index + citation.Length;
                    var suffixEnd = index + 1 < citations.Length ? citations[index + 1].Index : item.Length;
                    var suffix = item[previousEnd..suffixEnd];
                    var path = Candidate(citation.Groups[1].Value, prefix, suffix);
                    if (path is null) continue;
                    if (!SafePath(path))
                    {
                        if (reported.Add(path)) yield return $"{path}: not tracked at {mainHead} (invalid repository path)";
                        continue;
                    }
                    if (repository.TrackedLineCount(mainHead, path) is not null ||
                        repository.IsTrackedDirectory(mainHead, path) || NewPath(prefix, suffix, path)) continue;
                    if (reported.Add(path)) yield return $"{path}: not tracked at {mainHead}";
                }
            }
        }

        bool NewPath(string prefix, string suffix, string path)
        {
            // A new row is metadata, not a declaration that the underlying target is new.
            var declaration = Regex.Replace(prefix,
                @"\b(?:new|create(?:\s+(?:a|an|the))?)\s+(?:ratchet\s+|SourceSizeRatchet\s+)?rows?\b", "", Words);
            if (Affirmative(declaration, @"\b(?:new|create)\b")) return true;
            if (!Affirmative(suffix, @"^\s*[,(:-]?\s*(?:(?:the\s+)?file\s+is\s+(?:a\s+)?new\b|(?:is\s+)?(?:a\s+)?new\s+file\b)"))
                return false;
            var separator = path.LastIndexOf('/');
            return separator > 0 && repository.IsTrackedDirectory(mainHead, path[..separator]);
        }

        IEnumerable<string> RatchetFailures()
        {
            var targets = new List<string>();
            foreach (var line in lines)
            {
                if (!Regex.IsMatch(line, @"\b(?:ratchet|SourceSizeRatchet)\b", Words) ||
                    !Regex.IsMatch(line, @"\brows?\b", Words)) continue;
                foreach (Match citation in Citations.Matches(line))
                {
                    var path = Candidate(citation.Groups[1].Value, "", "");
                    if (path is null || !path.StartsWith("src/", StringComparison.Ordinal) ||
                        path == SourceSizeRatchet.SourcePath) continue;
                    var start = line.LastIndexOf(';', citation.Index);
                    var end = line.IndexOf(';', citation.Index);
                    var clause = Citations.Replace(line[(start + 1)..(end < 0 ? line.Length : end)], "");
                    if (Affirmative(clause,
                        @"\b(?:add|added|adding)\s+(?:(?:a|an|the|new|missing|another|ratchet|SourceSizeRatchet)\s+)*rows?\b|" +
                        @"\brows?\s+(?:(?:for|of)\s+)?(?:(?:is|are|will\s+be|must\s+be|should\s+be|being)\s+)?added\b|" +
                        @"\bnew\s+(?:(?:ratchet|SourceSizeRatchet)\s+)?rows?\b")) continue;
                    targets.Add(path);
                }
            }
            if (targets.Count == 0) yield break;
            var text = repository.TrackedFileText(mainHead, SourceSizeRatchet.SourcePath);
            if (text is null)
            {
                yield return $"{SourceSizeRatchet.SourcePath} is not readable at {mainHead}; validation could not be completed.";
                yield break;
            }
            var rows = CeilingEntries.Matches(text).Cast<Match>()
                .Where(match => match.Groups["path"].Success)
                .Select(match => match.Groups["path"].Value).ToHashSet(StringComparer.Ordinal);
            foreach (var path in targets.Distinct(StringComparer.Ordinal))
                if (!rows.Contains(path))
                    yield return $"{path}: no SourceSizeCeiling entry in {SourceSizeRatchet.SourcePath} at {mainHead}";
        }
    }

    private static bool Affirmative(string text, string pattern)
    {
        foreach (Match match in Regex.Matches(text, pattern, Words))
        {
            var prefix = text[..match.Index];
            if (!Regex.IsMatch(prefix, @"\b(?:no|not|never|without|don't|isn't)\b[^.;\n]*$", Words) &&
                !Regex.IsMatch(match.Value, @"\b(?:no|not|never|without)\b", Words)) return true;
        }
        return false;
    }

    private static string? Candidate(string token, string prefix, string suffix)
    {
        var path = BoardFillCitation.Parse(token).Path.TrimEnd('/');
        if (path.Length == 0 || path.Any(char.IsWhiteSpace) || token.Contains("://") ||
            path.IndexOfAny(['*', '?', '<', '>']) >= 0 || path.Split('/').Contains("...")) return null;
        var explicitName = token.StartsWith("./", StringComparison.Ordinal) || token.EndsWith('/') ||
            Regex.IsMatch(prefix, @"\b(?:path|directory|folder|file)\s*$", Words) ||
            Regex.IsMatch(suffix, @"^\s*(?:directory|folder|file)\b", Words);
        return path.Contains('/') || path.Contains('\\') ||
            Regex.IsMatch(path, @"\.[a-z0-9]+$", RegexOptions.CultureInvariant) || explicitName ? path : null;
    }

    private static bool SafePath(string path) => !path.StartsWith('/') &&
        path.IndexOfAny([':', '\\']) < 0 && !path.Split('/').Any(segment => segment is "" or "." or "..");

    private static IEnumerable<string> TargetItems(IReadOnlyList<string> lines)
    {
        var section = "";
        var sectionLevel = 0;
        var inScope = false;
        var item = "";
        foreach (var line in lines)
        {
            var heading = Heading.Match(line);
            var label = (heading.Success ? heading.Groups[2].Value : line.Trim()).Trim('*', '_', ' ', ':');
            var level = heading.Success ? heading.Groups[1].Length : 0;
            var scopeLabel = section == "scope" &&
                (label.Equals("In scope", StringComparison.OrdinalIgnoreCase) ||
                 label.Equals("Out of scope", StringComparison.OrdinalIgnoreCase));
            if (scopeLabel || (heading.Success && (sectionLevel == 0 || level <= sectionLevel)))
            {
                if (item.Length > 0) yield return item;
                item = "";
                if (scopeLabel) inScope = label.Equals("In scope", StringComparison.OrdinalIgnoreCase);
                else
                {
                    section = label.ToLowerInvariant();
                    sectionLevel = level;
                    inScope = false;
                }
                continue;
            }
            if (heading.Success)
            {
                if (item.Length > 0) yield return item;
                item = section == "what to build" ? line : "";
                if (section == "scope") inScope = false;
                continue;
            }
            if (section != "what to build" && !(section == "scope" && inScope)) continue;
            var list = ListItem.IsMatch(line);
            var continuation = line.Length > 0 && char.IsWhiteSpace(line[0]);
            if (list || !continuation || string.IsNullOrWhiteSpace(line))
            {
                if (item.Length > 0) yield return item;
                item = "";
            }
            if (section == "scope" && !list && item.Length == 0)
            {
                if (!string.IsNullOrWhiteSpace(line)) inScope = false;
                continue;
            }
            item += (item.Length == 0 ? "" : "\n") + line;
        }
        if (item.Length > 0) yield return item;
    }

    private static IEnumerable<string> ProseLines(string markdown)
    {
        char fence = '\0';
        var length = 0;
        foreach (var line in markdown.ReplaceLineEndings("\n").Split('\n'))
        {
            var trimmed = line.TrimStart();
            var marker = trimmed.Length > 0 && trimmed[0] is '`' or '~'
                ? trimmed.TakeWhile(character => character == trimmed[0]).Count() : 0;
            if (length == 0)
            {
                if (marker >= 3) { fence = trimmed[0]; length = marker; yield return ""; }
                else yield return line;
            }
            else if (marker >= length && trimmed[0] == fence &&
                     string.IsNullOrWhiteSpace(trimmed[marker..])) length = 0;
        }
    }
}
