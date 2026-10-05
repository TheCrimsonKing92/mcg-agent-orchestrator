namespace Mcg.AgentOrchestrator.Core;

public enum CreatedStageAction
{
    Proceed,
    Hold
}

/// <summary>Observed workspace-create result, including the evidence lease refusal when held.</summary>
public sealed record CreatedStageFacts
{
    public string LeaseUnavailableMessage { get; init; } = string.Empty;
    public string CreatedPath { get; init; } = string.Empty;

    public IReadOnlyList<PolicyDecisionFact> ToRecordedFacts() => Array.AsReadOnly<PolicyDecisionFact>(
    [
        new("lease-unavailable-message", LeaseUnavailableMessage),
        new("created-path", CreatedPath)
    ]);

    public static CreatedStageFacts FromRecordedFacts(IReadOnlyList<PolicyDecisionFact> facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        if (facts.Count != 2)
            throw new InvalidOperationException("Created stage replay requires exactly two named facts.");

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var fact in facts)
        {
            if (fact.Value is null || !values.TryAdd(fact.Name, fact.Value))
                throw new InvalidOperationException($"Invalid or duplicate created stage fact '{fact.Name}'.");
        }

        string Read(string name) => values.TryGetValue(name, out var value)
            ? value
            : throw new InvalidOperationException($"Missing created stage fact '{name}'.");
        return new()
        {
            LeaseUnavailableMessage = Read("lease-unavailable-message"),
            CreatedPath = Read("created-path")
        };
    }
}

public sealed record CreatedStageDecision(
    CreatedStageAction Action,
    int DiscriminatingRung,
    string DiscriminatingEvidence,
    string Reason,
    CreatedStageFacts Facts)
{
    public PolicyDecisionRecord ToRecord() => new(
        CreatedStagePolicy.StageName, Action.ToString(), DiscriminatingRung,
        DiscriminatingEvidence, Reason, Facts.ToRecordedFacts());
}

/// <summary>Selects the Created-stage outcome without owning workspace or lease effects.</summary>
public static class CreatedStagePolicy
{
    public const string StageName = "created";

    public static CreatedStageDecision Evaluate(CreatedStageFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        if (facts.LeaseUnavailableMessage.Length > 0)
            return new(CreatedStageAction.Hold, 1, "workspace-create-evidence-lease", facts.LeaseUnavailableMessage, facts);

        return new(CreatedStageAction.Proceed, 0, "created-proceed", $"Workspace created: {facts.CreatedPath}", facts);
    }
}
