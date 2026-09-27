using System.Text.RegularExpressions;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class ConductorAuthorOwnerClassCheck
{
    private static readonly (string Reason, Regex Pattern)[] Rules =
    [
        ("authority-widening", Rule(@"\b(?:grant|give|expand|widen|elevate|allow|authorize|permission|self[- ]?approve|bypass)\b.{0,100}\b(?:agent|author|worker|authority|permission|access|scope|approval|gate)\b|\b(?:agent|author|worker)\b.{0,100}\b(?:self[- ]?approve|bypass|unrestricted|permission|authority)\b")),
        ("acceptance-weakening", Rule(@"\b(?:waive|skip|remove|relax|weaken|ignore|disable|lower|drop|omit|bypass)\b.{0,100}\b(?:acceptance|criterion|criteria|gate|test|verification|requirement)\b|\b(?:acceptance|criterion|criteria|gate|test|verification|requirement)\b.{0,100}\b(?:waive|skip|remove|relax|weaken|ignore|disable|lower|drop|omit|bypass)\b")),
        ("spend-beyond-budget", Rule(@"\b(?:exceed|over|beyond|increase|raise|ignore|override|unlimited|extra)\b.{0,100}\b(?:budget|spend|cost|credit|quota|limit)\b|\b(?:budget|spend|cost|credit|quota|limit)\b.{0,100}\b(?:exceed|over|beyond|increase|raise|ignore|override|unlimited|extra)\b")),
        ("irreversible-action", Rule(@"\b(?:force[- ]?push|hard[- ]?reset|purge|permanent(?:ly)? delete|delete (?:the )?(?:branch|database|repository|production)|destroy|irreversible|cannot be undone)\b")),
        ("external-disclosure", Rule(@"\b(?:send|upload|publish|share|disclose|exfiltrate|post)\b.{0,100}\b(?:external|third[- ]?party|public|secret|credential|private data|customer data|outside)\b|\b(?:secret|credential|private data|customer data)\b.{0,100}\b(?:send|upload|publish|share|disclose|post)\b"))
    ];

    internal static string? Evaluate(string question, string answer, IReadOnlyList<string>? evidenceReferences)
    {
        foreach (var text in new[] { question, answer })
        foreach (var (reason, pattern) in Rules)
            if (pattern.IsMatch(text ?? string.Empty)) return reason;

        if (evidenceReferences is null || evidenceReferences.Count == 0 ||
            evidenceReferences.Any(reference => string.IsNullOrWhiteSpace(reference) ||
                !Regex.IsMatch(reference, @"^.+:\d+(?:[-:]\d+)?$", RegexOptions.CultureInvariant)))
            return "missing-evidence";
        return null;
    }

    private static Regex Rule(string expression) => new(expression,
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Singleline);
}
