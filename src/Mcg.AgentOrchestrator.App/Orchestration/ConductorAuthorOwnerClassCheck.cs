using System.Text.RegularExpressions;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class ConductorAuthorOwnerClassCheck
{
    private static readonly (string Reason, Regex Pattern)[] Rules =
    [
        ("authority-widening", Rule(@"\b(?:grant|give|expand|widen|elevate|allow|authorize|permission|self[- ]?approve|bypass)\b.{0,100}\b(?:agent|author|worker|authority|permission|access|scope|approval|gate)\b|\b(?:agent|author|worker)\b.{0,100}\b(?:self[- ]?approve|bypass|unrestricted|permission|authority)\b")),
        ("acceptance-weakening", Rule(@"\b(?:waive|skip|remove|relax|weaken|ignore|disable|lower|drop|omit|bypass)\b.{0,100}\b(?:acceptance|criterion|criteria|gate|test|verification|requirement)\b|\b(?:acceptance|criterion|criteria|gate|test|verification|requirement)\b.{0,100}\b(?:waive|skip|remove|relax|weaken|ignore|disable|lower|drop|omit|bypass)\b")),
        ("spend-beyond-budget", Rule(@"\b(?:exceed|over|beyond|increase|raise|ignore|override|unlimited|extra)\b.{0,100}\b(?:budget|spend|cost|credit|quota|limit)\b|\b(?:budget|spend|cost|credit|quota|limit)\b.{0,100}\b(?:exceed|over|beyond|increase|raise|ignore|override|unlimited|extra)\b")),
        ("irreversible-action", Rule(@"\b(?:force[- ]?push|hard[- ]?reset|purge|permanent(?:ly)? delete|delete (?:(?:the|all) )?(?:branch|database|repository|production)|destroy|irreversible|cannot be undone)\b")),
        ("external-disclosure", Rule(@"\b(?:send|upload|publish|share|disclose|exfiltrate|post)\b.{0,100}\b(?:external|third[- ]?party|public|secret|credential|private data|customer data|outside)\b|\b(?:secret|credential|private data|customer data)\b.{0,100}\b(?:send|upload|publish|share|disclose|post)\b"))
    ];

    private static readonly Regex SentenceBoundary = Rule(@"[.!?](?=\s|$)|[\r\n]+");
    private static readonly Regex ProposalWords = Rule(
        @"\b(?:should|may|must|could|would|might|can|will|shall|need|needs|ought|instead|propose|proposed|recommend|suggest|prefer|rather|let's|please)\b");
    private static readonly Regex PersonalWords = Rule(@"\b(?:i|we|you|me|us|my|our|your)\b");
    private static readonly Regex CurrentScope = Rule(@"\b(?:now|this (?:run|goal|branch|test|gate))\b");

    // Past tense and participles of the action verbs in main's five patterns.
    private static readonly Regex PastActions = Rule(
        @"\b(?:granted|gave|given|expanded|widened|elevated|allowed|authorized|self[- ]?approved|bypassed|waived|skipped|removed|relaxed|weakened|ignored|disabled|lowered|dropped|omitted|exceeded|increased|raised|overrode|overridden|force[- ]?pushed|hard[- ]?reset|purged|deleted|destroyed|sent|uploaded|published|shared|disclosed|exfiltrated|posted)\b");
    private static readonly Regex ClauseBoundary = Rule(
        @"[;:,(\u2014\u2013]|\s+-\s+|\b(?:so|then|and|but|or|otherwise)\b");
    private static readonly Regex FirstWord = Rule(@"^\W*([a-z]+)");
    private static readonly HashSet<string> ClauseActions = new(
        ("grant|give|expand|widen|elevate|allow|authorize|self-approve|bypass|" +
         "waive|skip|remove|relax|weaken|ignore|disable|lower|drop|omit|" +
         "exceed|increase|raise|override|force-push|hard-reset|purge|delete|destroy|" +
         "send|upload|publish|share|disclose|exfiltrate|post|" +
         "make|set|add|change|use|let|run|provide|assign|extend|turn|mark|treat|accept|" +
         "keep|mute|stub|comment|revert|merge|push|approve")
        .Split('|').SelectMany(verb => new[]
        {
            FirstWord.Match(verb).Groups[1].Value, verb.Replace("-", string.Empty)
        }),
        StringComparer.OrdinalIgnoreCase);

    internal static string? Evaluate(string question, string answer, IReadOnlyList<string>? evidenceReferences)
    {
        foreach (var text in new[] { question, answer })
        {
            var checkedText = BlankDescriptiveSentences(text ?? string.Empty);
            foreach (var (reason, pattern) in Rules)
                if (pattern.IsMatch(checkedText)) return reason;
        }

        if (evidenceReferences is null || evidenceReferences.Count == 0 ||
            evidenceReferences.Any(reference => string.IsNullOrWhiteSpace(reference) ||
                !Regex.IsMatch(reference, @"^.+:\d+(?:[-:]\d+)?$", RegexOptions.CultureInvariant)))
            return "missing-evidence";
        return null;
    }

    private static string BlankDescriptiveSentences(string text)
    {
        var result = text.ToCharArray();
        var start = 0;
        foreach (Match boundary in SentenceBoundary.Matches(text))
        {
            var end = boundary.Index + boundary.Length;
            BlankIfDescriptive(text, result, start, end - start);
            start = end;
        }
        BlankIfDescriptive(text, result, start, text.Length - start);
        return new string(result);
    }

    private static void BlankIfDescriptive(string text, char[] result, int start, int length)
    {
        var sentence = text.Substring(start, length);
        if (!Rules.Any(rule => rule.Pattern.IsMatch(sentence)) ||
            ProposalWords.IsMatch(sentence) || PersonalWords.IsMatch(sentence) ||
            CurrentScope.IsMatch(sentence) || PastActions.IsMatch(sentence))
            return;

        foreach (var clause in ClauseBoundary.Split(sentence))
        {
            var firstWord = FirstWord.Match(clause);
            if (firstWord.Success && ClauseActions.Contains(firstWord.Groups[1].Value))
                return;
        }

        // Keep distances and every non-descriptive sentence intact for main's rules.
        Array.Fill(result, ' ', start, length);
    }

    private static Regex Rule(string expression) => new(expression,
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Singleline);
}
