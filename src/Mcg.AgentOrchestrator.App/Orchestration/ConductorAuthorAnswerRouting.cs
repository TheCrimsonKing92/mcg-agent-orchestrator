namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record ConductorAuthorAnswerRoute(string? EscalationReason, string SubmittedText);

internal static class ConductorAuthorAnswerRouting
{
    internal static ConductorAuthorAnswerRoute Decide(ConductorAuthorItem item, string answerText,
        IReadOnlyList<string>? evidenceReferences)
    {
        if (!string.Equals(item.ForkKind, AcceptanceCriterionFeasibility.ForkKind, StringComparison.Ordinal))
            return new(ConductorAuthorOwnerClassCheck.Evaluate(item.Question, answerText, evidenceReferences), answerText);

        if (!AcceptanceCriterionFeasibility.TryParseDisposition(answerText, out var disposition) ||
            disposition.Kind != FeasibilityDisposition.ReproducingScenario)
            return new("acceptance-weakening", answerText);

        var canonical = FeasibilityDisposition.ReproducingScenario + ": " + disposition.Value;
        return new(ConductorAuthorOwnerClassCheck.Evaluate(item.Question, canonical, evidenceReferences), canonical);
    }
}
