namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed record OwnerExperimentDecisionForm(string Outcome, string Evidence, string Action)
{
    internal string? Refusal()
    {
        if (Outcome is not ("confirmed" or "refuted" or "inconclusive"))
            return "outcome: expected confirmed, refuted or inconclusive.";
        if (string.IsNullOrWhiteSpace(Evidence)) return "evidence is required.";
        if (string.IsNullOrWhiteSpace(Action)) return "action is required.";
        return null;
    }

    internal static string DefaultEvidence(string shortId) => $"experiment-show:{shortId}";
}
