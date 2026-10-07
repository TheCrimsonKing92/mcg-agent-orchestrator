namespace Mcg.AgentOrchestrator.Core;

public enum PreReviewEvidenceHoldAction
{
    Proceed,
    Hold
}

/// <summary>Observed pre-review hold inputs; empty values denote inputs not read on this path.</summary>
public sealed record PreReviewEvidenceHoldFacts(string GoalId)
{
    public string Condition { get; init; } = string.Empty;
    public string AttemptId { get; init; } = string.Empty;
    public string AttemptOutcome { get; init; } = string.Empty;
    public string HoldReason { get; init; } = string.Empty;

    public IReadOnlyList<PolicyDecisionFact> ToRecordedFacts() => Array.AsReadOnly<PolicyDecisionFact>(
    [
        new("goalId", GoalId),
        new("condition", Condition),
        new("attemptId", AttemptId),
        new("attemptOutcome", AttemptOutcome),
        new("holdReason", HoldReason)
    ]);

    public static PreReviewEvidenceHoldFacts FromRecordedFacts(IReadOnlyList<PolicyDecisionFact> facts)
    {
        if (facts is null || facts.Count != 5)
            throw new InvalidOperationException("Pre-review evidence hold replay requires exactly five named facts.");

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var fact in facts)
        {
            if (fact is null || fact.Name is null || fact.Value is null || !values.TryAdd(fact.Name, fact.Value))
                throw new InvalidOperationException("Invalid or duplicate pre-review evidence hold fact.");
        }

        string Read(string name) => values.TryGetValue(name, out var value)
            ? value
            : throw new InvalidOperationException($"Missing pre-review evidence hold fact '{name}'.");

        var condition = Read("condition");
        if (condition is not ("" or "writer-busy" or "attempt-running" or "did-not-run"))
            throw new InvalidOperationException($"Invalid pre-review evidence hold condition '{condition}'.");

        return new PreReviewEvidenceHoldFacts(Read("goalId"))
        {
            Condition = condition,
            AttemptId = Read("attemptId"),
            AttemptOutcome = Read("attemptOutcome"),
            HoldReason = Read("holdReason")
        };
    }
}

public sealed record PreReviewEvidenceHoldDecision(
    PreReviewEvidenceHoldAction Action,
    int DiscriminatingRung,
    string DiscriminatingEvidence,
    string Reason,
    PreReviewEvidenceHoldFacts Facts)
{
    public PolicyDecisionRecord ToRecord() => new(
        PreReviewEvidenceHoldPolicy.StageName, Action.ToString(), DiscriminatingRung,
        DiscriminatingEvidence, Reason, Facts.ToRecordedFacts());
}

/// <summary>Attributes pre-review evidence holds from observed facts without owning effects.</summary>
public static class PreReviewEvidenceHoldPolicy
{
    public const string StageName = "pre-review-evidence";

    public static PreReviewEvidenceHoldDecision Evaluate(PreReviewEvidenceHoldFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        return facts.Condition switch
        {
            "" => new(PreReviewEvidenceHoldAction.Proceed, 0, "proceed", "Pre-review evidence may proceed.", facts),
            "writer-busy" => new(PreReviewEvidenceHoldAction.Hold, 1, "artifact-writer-busy", facts.HoldReason, facts),
            "attempt-running" => new(PreReviewEvidenceHoldAction.Hold, 2, "attempt-running", facts.HoldReason, facts),
            "did-not-run" => new(PreReviewEvidenceHoldAction.Hold, 3, "attempt-did-not-run", facts.HoldReason, facts),
            _ => throw new InvalidOperationException($"Invalid pre-review evidence hold condition '{facts.Condition}'.")
        };
    }
}
