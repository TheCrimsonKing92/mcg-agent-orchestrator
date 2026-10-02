using System.Text.RegularExpressions;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class ConductorAuthorOwnerClassCheck
{
    private static readonly OwnerRule[] Rules =
    [
        new("authority-widening", "grant|give|expand|widen|elevate|allow|authorize|self-approve|bypass",
            @"agents?|authors?|workers?|authority|permissions?|access|scope|approval|gates?|allowlists?|unrestricted", "gave|given",
            capabilityVerbs: "add|let|permit|have|get|receive|make|be"),
        new("acceptance-weakening", "waive|skip|remove|relax|weaken|ignore|disable|lower|drop|omit|bypass",
            @"acceptance|criterion|criteria|gates?|tests?|verification|requirements?|assertions?"),
        new("spend-beyond-budget", "exceed|over|beyond|increase|raise|ignore|override|unlimited|extra",
            @"budgets?|spend|costs?|credits?|quotas?|limits?", "overrode|overridden"),
        new("irreversible-action", "force-push|hard-reset|purge|delete|destroy",
            @"branches|branch|databases?|repository|repositories|production|data",
            markers: @"force[- ]?push(?:es|ed|ing)?|hard[- ]?reset(?:s|ting)?|purge(?:s|d)?|purging|destroy(?:s|ed|ing)?|permanent(?:ly)? delet(?:e|es|ed|ing)|irreversible|cannot be undone"),
        new("external-disclosure", "send|upload|publish|share|disclose|exfiltrate|post",
            @"external|third[- ]?party|public|secrets?|credentials?|private data|customer data|outside", "sent")
    ];

    private static readonly Regex SentenceBoundary = Rule(@"[.!?;](?:\s+|$)|[\r\n]+");
    private static readonly Regex ClauseBoundary = Rule(@"[,:—(]|\b(?:but|and|since|because|although|though|whereas)\b");
    private static readonly Regex PronounObject = Rule(@"^\s+(?:it|them|that|those|these)\b");
    private static readonly Regex ImperativePrefix = Rule(@"^\s*(?:(?:please|just|then|so|instead|permanently)\s+){0,3}$");
    private static readonly Regex ProposalPrefix = Rule(
        @"\b(?:should|may|might|can|could|shall|must|will|['’]ll|let['’]s|let us|propose|recommend|suggest|go ahead and|(?:is|it['’]s|that['’]s) (?:fine|ok|okay|acceptable|safe) to|instead(?!\s+of))\s+(?:(?:i|we|you|they|the|agent|author|worker|this|run|goal|just|also|now|simply|safely|permanently|be|to)\s+){0,3}$");
    private static readonly Regex ActorPrefix = Rule(
        @"\b(?:i|we|i['’]ve|we['’]ve|the (?:agent|worker|author)|this (?:run|goal))\s+(?:(?:have|has|had|would|just|already|now|permanently)\s+){0,3}$");
    private static readonly Regex Actor = Rule(@"\b(?:i|we|the (?:agent|worker|author)|this (?:run|goal))\b");
    private static readonly Regex PassivePrefix = Rule(@"\b(?:is|are|was|were|has been|have been|will be)\s+(?:(?:now|already)\s+){0,2}$");
    private static readonly Regex RunScope = Rule(@"\b(?:now|for this (?:run|goal|task)|in this run|on this branch)\b");
    private static readonly Regex Negation = Rule(@"\b(?:no|not|never|none|neither|nor|without|cannot(?! be undone)|rejected|ruled out)\b|n['’]t\b");
    private static readonly Regex Risk = Rule(@"\b(?:risk|risks|risky|would|could|might)\b");

    internal static string? Evaluate(string question, string answer, IReadOnlyList<string>? evidenceReferences)
    {
        foreach (var text in new[] { question, answer })
        foreach (var rule in Rules)
        {
            var previousSentence = string.Empty;
            foreach (var sentence in SentenceBoundary.Split(text ?? string.Empty))
            {
                if (string.IsNullOrWhiteSpace(sentence)) continue;
                if (RequiresOwner(sentence, previousSentence, rule)) return rule.Reason;
                previousSentence = sentence;
            }
        }

        if (evidenceReferences is null || evidenceReferences.Count == 0 ||
            evidenceReferences.Any(reference => string.IsNullOrWhiteSpace(reference) ||
                !Regex.IsMatch(reference, @"^.+:\d+(?:[-:]\d+)?$", RegexOptions.CultureInvariant)))
            return "missing-evidence";
        return null;
    }

    private static bool RequiresOwner(string sentence, string previousSentence, OwnerRule rule)
    {
        var hasObject = rule.Objects.IsMatch(sentence);
        var markers = rule.Markers?.Matches(sentence).Cast<Match>().ToArray() ?? [];
        var actions = rule.Actions.Matches(sentence).Cast<Match>()
            .Where(action => hasObject ||
                (rule.Objects.IsMatch(previousSentence) &&
                    PronounObject.IsMatch(sentence[(action.Index + action.Length)..])) ||
                markers.Any(marker => action.Index >= marker.Index &&
                    action.Index + action.Length <= marker.Index + marker.Length))
            // These budget cues are quantities/prepositions, not verbs: "uniform
            // over the result" does not act on the budget mentioned in its subject.
            .Where(action => !Regex.IsMatch(action.Value, @"^(?:over|beyond|unlimited|extra)$",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) ||
                rule.Objects.IsMatch(sentence[(action.Index + action.Length)..]))
            .ToArray();
        if (actions.Length == 0 && markers.Length == 0) return false;

        foreach (var action in actions)
        {
            var prefix = sentence[..action.Index];
            var boundaries = ClauseBoundary.Matches(prefix);
            var clausePrefix = boundaries.Count == 0 ? prefix : prefix[(boundaries[^1].Index + boundaries[^1].Length)..];
            // Negation/rejection governing this action is distinct from a later risk or
            // contrast: "Skip/inconclusive ... not a hard test failure" is still a proposal.
            var sharesNegation = boundaries.Count > 0 &&
                boundaries[^1].Value.Equals("and", StringComparison.OrdinalIgnoreCase) &&
                Negation.IsMatch(prefix[(boundaries.Count > 1
                    ? boundaries[^2].Index + boundaries[^2].Length : 0)..boundaries[^1].Index]);
            if (Negation.IsMatch(clausePrefix) || sharesNegation) continue;
            if ((rule.BaseActions.IsMatch(action.Value) && ImperativePrefix.IsMatch(clausePrefix)) ||
                ProposalPrefix.IsMatch(prefix) || ActorPrefix.IsMatch(prefix) ||
                (PassivePrefix.IsMatch(prefix) && RunScope.IsMatch(sentence)))
                return true;

            var suffixBoundary = ClauseBoundary.Match(sentence, action.Index + action.Length);
            var clause = clausePrefix + sentence[action.Index..(suffixBoundary.Success
                ? suffixBoundary.Index : sentence.Length)];
            if (Risk.IsMatch(clause)) continue;
            if (!Actor.IsMatch(clause) && (rule.ThirdPersonActions.IsMatch(action.Value) ||
                PassivePrefix.IsMatch(prefix))) continue;

            // Unclassified action language fails closed. Negation or risk in an
            // unrelated clause cannot suppress this action.
            return true;
        }

        if (actions.Length > 0) return false;
        // Standalone irreversible markers can also be risk descriptions. Scope
        // their descriptive cues to their clause just as for action verbs.
        foreach (var marker in markers)
        {
            var prefix = sentence[..marker.Index];
            var boundaries = ClauseBoundary.Matches(prefix);
            var start = boundaries.Count == 0 ? 0 : boundaries[^1].Index + boundaries[^1].Length;
            var suffixBoundary = ClauseBoundary.Match(sentence, marker.Index + marker.Length);
            var clause = sentence[start..(suffixBoundary.Success ? suffixBoundary.Index : sentence.Length)];
            if (!Negation.IsMatch(clause) && !Risk.IsMatch(clause)) return true;
        }
        return false;
    }

    private sealed class OwnerRule
    {
        internal string Reason { get; }
        internal Regex BaseActions { get; }
        internal Regex Actions { get; }
        internal Regex ThirdPersonActions { get; }
        internal Regex Objects { get; }
        internal Regex? Markers { get; }

        internal OwnerRule(string reason, string verbs, string objects, string irregular = "", string? markers = null,
            string? capabilityVerbs = null)
        {
            Reason = reason;
            var bases = verbs.Split('|');
            var capabilities = capabilityVerbs?.Split('|') ?? [];
            var allBases = bases.Concat(capabilities).ToArray();
            BaseActions = Rule(@"^(?:" + string.Join('|', allBases.Select(VerbPattern)) + @")$");
            ThirdPersonActions = Rule(@"^(?:" + string.Join('|', allBases.Select(verb =>
                VerbPattern(verb) + (NeedsEs(verb) ? "es" : "s"))) +
                (capabilityVerbs is null ? "" : "|has|is|are|was|were") + @")$");
            var forms = bases.Select(Inflections).ToList();
            if (irregular.Length > 0) forms.Add(irregular);
            if (capabilities.Length > 0)
                // Bind acquisition verbs to a capability phrase, rather than an
                // unrelated object such as "Add a test covering agent permission".
                forms.Add("(?:" + string.Join('|', capabilities.Select(Inflections)) +
                    @")(?=\s+(?:(?:the|a|an|agent|author|worker|agents|authors|workers|have|be|read|write|full|network|root|admin|additional|extra|elevated|broader|wider|more|unrestricted|new)\s+){0,6}(?:unrestricted|permissions?|authority|access|self-approve|bypass)\b)");
            Actions = Rule(@"\b(?:" + string.Join('|', forms) + @")\b");
            Objects = Rule(@"\b(?:" + objects + @")\b");
            Markers = markers is null ? null : Rule(@"\b(?:" + markers + @")\b");
        }

        private static string VerbPattern(string verb) => Regex.Escape(verb).Replace("-", "[- ]?");

        private static bool NeedsEs(string verb) => verb.EndsWith("sh", StringComparison.Ordinal) ||
            verb.EndsWith("ss", StringComparison.Ordinal);

        private static string Inflections(string verb)
        {
            var pattern = VerbPattern(verb);
            if (verb == "give") return @"give(?:s|n)?|giving";
            if (verb == "make") return @"make(?:s)?|made|making";
            if (verb == "have") return @"have|has|had|having";
            if (verb == "be") return @"be|is|are|was|were|been|being";
            if (verb == "get") return @"get(?:s|ting)?|got";
            if (verb == "let") return @"let(?:s|ting)?";
            if (verb.EndsWith('e')) return pattern + @"(?:s|d)?|" + VerbPattern(verb[..^1]) + "ing";
            if (verb == "hard-reset") return pattern + @"(?:s)?|" + pattern + "ting";
            if (NeedsEs(verb)) return pattern + @"(?:es|ed|ing)?";
            if (verb is "skip" or "drop" or "omit" or "permit")
                return pattern + @"(?:s)?|" + pattern + Regex.Escape(verb[^1..]) + @"(?:ed|ing)";
            return pattern + @"(?:s|ed|ing)?";
        }
    }

    private static Regex Rule(string expression) => new(expression,
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Singleline);
}
