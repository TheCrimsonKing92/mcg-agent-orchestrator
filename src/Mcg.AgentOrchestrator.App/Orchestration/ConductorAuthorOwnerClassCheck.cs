using System.Text.RegularExpressions;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class ConductorAuthorOwnerClassCheck
{
    private static readonly OwnerRule[] Rules =
    [
        new("authority-widening", "grant|give|expand|widen|elevate|allow|authorize|self-approve|bypass",
            @"agents?|authors?|workers?|authority|permissions?|access|scope|approval|gates?|allowlists?", "gave|given"),
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
    private static readonly Regex ClauseBoundary = Rule(@"[,:—(]|\b(?:but|and)\b");
    private static readonly Regex ImperativePrefix = Rule(@"^\s*(?:(?:please|just|then|so|instead|permanently)\s+){0,3}$");
    private static readonly Regex ProposalPrefix = Rule(
        @"\b(?:should|may|might|can|could|shall|must|will|['’]ll|let['’]s|let us|propose|recommend|suggest|go ahead and|is (?:fine|ok|okay|acceptable|safe) to|instead(?!\s+of))\s+(?:(?:i|we|you|they|the|agent|author|worker|this|run|goal|just|also|now|simply|safely|permanently|be|to)\s+){0,3}$");
    private static readonly Regex ActorPrefix = Rule(
        @"\b(?:i|we|i['’]ve|we['’]ve|the (?:agent|worker|author)|this (?:run|goal))\s+(?:(?:have|has|had|just|already|now|permanently)\s+){0,3}$");
    private static readonly Regex Actor = Rule(@"\b(?:i|we|the (?:agent|worker|author)|this (?:run|goal))\b");
    private static readonly Regex PassivePrefix = Rule(@"\b(?:is|are|was|were|has been|have been|will be)\s+(?:(?:now|already)\s+){0,2}$");
    private static readonly Regex RunScope = Rule(@"\b(?:now|for this (?:run|goal|task)|in this run|on this branch)\b");
    private static readonly Regex Negation = Rule(@"\b(?:no|not|never|none|neither|nor|without|cannot(?! be undone)|rejected|ruled out)\b|n['’]t\b");
    private static readonly Regex Risk = Rule(@"\b(?:risk|risks|risky|would|could|might)\b");

    internal static string? Evaluate(string question, string answer, IReadOnlyList<string>? evidenceReferences)
    {
        foreach (var text in new[] { question, answer })
        foreach (var rule in Rules)
        foreach (var sentence in SentenceBoundary.Split(text ?? string.Empty))
            if (RequiresOwner(sentence, rule)) return rule.Reason;

        if (evidenceReferences is null || evidenceReferences.Count == 0 ||
            evidenceReferences.Any(reference => string.IsNullOrWhiteSpace(reference) ||
                !Regex.IsMatch(reference, @"^.+:\d+(?:[-:]\d+)?$", RegexOptions.CultureInvariant)))
            return "missing-evidence";
        return null;
    }

    private static bool RequiresOwner(string sentence, OwnerRule rule)
    {
        var hasObject = rule.Objects.IsMatch(sentence);
        var markers = rule.Markers?.Matches(sentence).Cast<Match>().ToArray() ?? [];
        var actions = rule.Actions.Matches(sentence).Cast<Match>()
            .Where(action => hasObject || markers.Any(marker => action.Index >= marker.Index &&
                action.Index + action.Length <= marker.Index + marker.Length)).ToArray();
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
        }

        // These cues describe or reject a candidate, rather than authorizing it. A
        // separate proposal in the sentence already won above; another sentence is
        // evaluated independently, so a description cannot hide a later proposal.
        if (Negation.IsMatch(sentence) || Risk.IsMatch(sentence)) return false;
        if (!Actor.IsMatch(sentence) && actions.Length > 0 &&
            actions.All(action => rule.ThirdPersonActions.IsMatch(action.Value) ||
                PassivePrefix.IsMatch(sentence[..action.Index])))
            return false;

        // Unclassified owner-class language needs an owner, including fragmentary
        // proposals such as "Skipping the acceptance test".
        return true;
    }

    private sealed class OwnerRule
    {
        internal string Reason { get; }
        internal Regex BaseActions { get; }
        internal Regex Actions { get; }
        internal Regex ThirdPersonActions { get; }
        internal Regex Objects { get; }
        internal Regex? Markers { get; }

        internal OwnerRule(string reason, string verbs, string objects, string irregular = "", string? markers = null)
        {
            Reason = reason;
            var bases = verbs.Split('|');
            BaseActions = Rule(@"^(?:" + string.Join('|', bases.Select(VerbPattern)) + @")$");
            ThirdPersonActions = Rule(@"^(?:" + string.Join('|', bases.Select(verb =>
                VerbPattern(verb) + (NeedsEs(verb) ? "es" : "s"))) + @")$");
            var forms = bases.Select(Inflections).ToList();
            if (irregular.Length > 0) forms.Add(irregular);
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
            if (verb.EndsWith('e')) return pattern + @"(?:s|d)?|" + VerbPattern(verb[..^1]) + "ing";
            if (verb == "hard-reset") return pattern + @"(?:s)?|" + pattern + "ting";
            if (NeedsEs(verb)) return pattern + @"(?:es|ed|ing)?";
            if (verb is "skip" or "drop" or "omit")
                return pattern + @"(?:s)?|" + pattern + Regex.Escape(verb[^1..]) + @"(?:ed|ing)";
            return pattern + @"(?:s|ed|ing)?";
        }
    }

    private static Regex Rule(string expression) => new(expression,
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Singleline);
}
