namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record ConductorAuthorAnswerRoute(string? EscalationReason, string SubmittedText,
    IReadOnlyList<string>? GroundedCitations = null);

internal static class ConductorAuthorAnswerRouting
{
    internal static ConductorAuthorAnswerRoute Decide(ConductorAuthorItem item, string answerText,
        IReadOnlyList<string>? evidenceReferences, string? brief = null, IReadOnlyList<string>? briefCitations = null)
    {
        if (!string.Equals(item.ForkKind, AcceptanceCriterionFeasibility.ForkKind, StringComparison.Ordinal))
            return Check(item, answerText, evidenceReferences, brief, briefCitations);

        if (!AcceptanceCriterionFeasibility.TryParseDisposition(answerText, out var disposition) ||
            disposition.Kind != FeasibilityDisposition.ReproducingScenario)
            return new("acceptance-weakening", answerText);

        var canonical = FeasibilityDisposition.ReproducingScenario + ": " + disposition.Value;
        return Check(item, canonical, evidenceReferences, brief, briefCitations);
    }

    private static ConductorAuthorAnswerRoute Check(ConductorAuthorItem item, string text,
        IReadOnlyList<string>? evidenceReferences, string? brief, IReadOnlyList<string>? briefCitations)
    {
        var reason = ConductorAuthorOwnerClassCheck.Evaluate(item.Question, text, evidenceReferences);
        return reason is not null && ConductorAuthorBriefGrounding.TryGround(brief, text, briefCitations, reason, out var citations)
            ? new(null, text, citations)
            : new(reason, text);
    }
}
