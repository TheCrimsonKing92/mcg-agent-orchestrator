using Mcg.AgentOrchestrator.Core.Conductor;

namespace Mcg.AgentOrchestrator.Core;

public enum UnchangedCandidateAction
{
    Proceed,
    Hold
}

public sealed record UnchangedCandidateFacts(string GoalId)
{
    public string Role { get; init; } = string.Empty;
    public string PriorVerdictTaskId { get; init; } = string.Empty;
    public string PriorVerdictAt { get; init; } = string.Empty;
    public string PriorVerdict { get; init; } = string.Empty;
    public string CandidateIdentity { get; init; } = string.Empty;

    public static UnchangedCandidateFacts From(GoalId goalId, UnchangedCandidateHoldReason reason) =>
        new(goalId.Value)
        {
            Role = reason.Role.ToString(),
            PriorVerdictTaskId = reason.PriorVerdictTaskId.Value,
            PriorVerdictAt = reason.PriorVerdictAt.ToString("O"),
            PriorVerdict = reason.PriorVerdict,
            CandidateIdentity = reason.CandidateIdentity.Canonical
        };

    public IReadOnlyList<PolicyDecisionFact> ToRecordedFacts() => Array.AsReadOnly<PolicyDecisionFact>(
    [
        new("goalId", GoalId),
        new("role", Role),
        new("priorVerdictTaskId", PriorVerdictTaskId),
        new("priorVerdictAt", PriorVerdictAt),
        new("priorVerdict", PriorVerdict),
        new("candidateIdentity", CandidateIdentity)
    ]);

    public static UnchangedCandidateFacts FromRecordedFacts(IReadOnlyList<PolicyDecisionFact> facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        if (facts.Count != 6)
            throw new InvalidOperationException("Unchanged candidate replay requires exactly six named facts.");

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var fact in facts)
        {
            if (fact is null || fact.Name is null || fact.Value is null || !values.TryAdd(fact.Name, fact.Value))
                throw new InvalidOperationException("Invalid or duplicate unchanged candidate fact.");
        }

        string Read(string name) => values.TryGetValue(name, out var value)
            ? value
            : throw new InvalidOperationException($"Missing unchanged candidate fact '{name}'.");

        return new UnchangedCandidateFacts(Read("goalId"))
        {
            Role = Read("role"),
            PriorVerdictTaskId = Read("priorVerdictTaskId"),
            PriorVerdictAt = Read("priorVerdictAt"),
            PriorVerdict = Read("priorVerdict"),
            CandidateIdentity = Read("candidateIdentity")
        };
    }
}

public sealed record UnchangedCandidateDecision(
    UnchangedCandidateAction Action,
    int DiscriminatingRung,
    string DiscriminatingEvidence,
    string Reason,
    UnchangedCandidateFacts Facts)
{
    public PolicyDecisionRecord ToRecord() => new(
        UnchangedCandidatePolicy.StageName, Action.ToString(), DiscriminatingRung,
        DiscriminatingEvidence, Reason, Facts.ToRecordedFacts());
}

public static class UnchangedCandidatePolicy
{
    public const string StageName = "unchanged-candidate";

    public static UnchangedCandidateDecision Evaluate(UnchangedCandidateFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        // Rung 0: no prior verdict holds this candidate.
        if (facts.PriorVerdict.Length == 0)
            return new(UnchangedCandidateAction.Proceed, 0, "proceed",
                "Unchanged candidate check may proceed.", facts);

        // Rung 1: attribute the unchanged-candidate hold using only the supplied facts.
        return new(UnchangedCandidateAction.Hold, 1, "unchanged-candidate",
            $"UNCHANGED_CANDIDATE role={facts.Role} verdict={facts.PriorVerdict} " +
            $"verdictTask={facts.PriorVerdictTaskId} verdictAt={facts.PriorVerdictAt} " +
            $"identity={facts.CandidateIdentity}. Change the candidate, submit operator retry guidance " +
            "for this role, or use human adjudication to re-open it.", facts);
    }
}
