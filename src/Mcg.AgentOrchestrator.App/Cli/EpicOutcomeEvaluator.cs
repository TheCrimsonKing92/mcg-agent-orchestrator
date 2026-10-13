using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal enum EpicOutcomeState { Unknown, Unmet, Proven }
internal sealed record EpicOutcomeEvidenceReference(string ReceiptId, long BarVersion, EpicOutcomeSource Source,
    string Actor, OperatorActorKind ActorKind, string Channel, DateTimeOffset ObservedAt);
internal sealed record EpicOutcomeRequirementProgress(string Id, string Statement, string Authority,
    EpicOutcomeState State, long ObservationSequence, IReadOnlyList<string> ReceiptIds, IReadOnlyList<string> Reasons,
    IReadOnlyList<EpicOutcomeEvidenceReference> Evidence);
internal sealed record EpicOutcomeProjection(EpicOutcomeState State, long BarVersion, string? DisplayText,
    IReadOnlyList<string> Reasons, IReadOnlyList<EpicOutcomeRequirementProgress> Requirements)
{
    internal static EpicOutcomeProjection Unevaluated { get; } = new(EpicOutcomeState.Unknown, 0, null, ["no-definition"], []);
    internal string Work => State switch
    {
        EpicOutcomeState.Unmet => "outcome unmet",
        EpicOutcomeState.Proven => "outcome proven",
        _ => "outcome evidence required"
    };
    internal string Render() => $"Outcome: {State.ToString().ToLowerInvariant()}; bar version {BarVersion}; {Work}" +
        (Reasons.Count == 0 ? "" : $"; reasons: {string.Join(", ", Reasons)}");
}

// Deterministic policy evaluation; receipt I/O is supplied by the reader, never by a formatter.
internal static class EpicOutcomeEvaluator
{
    internal static EpicOutcomeProjection Evaluate(EpicOutcomeHistory? history,
        Func<EpicOutcomeObservation, string?> receiptProblem)
    {
        history ??= EpicOutcomeHistory.Empty;
        var current = history.Current;
        if (current is null) return EpicOutcomeProjection.Unevaluated;
        var stale = history.Observations.Any(observation => observation.BarVersion != history.Version);
        if (current.Requirements.Count == 0)
            return new(EpicOutcomeState.Unknown, history.Version, current.DisplayText,
                stale ? ["no-definition", "stale-version"] : ["no-definition"], []);
        try { EpicOutcomeContract.Validate(new EpicOutcomeDefinition(1, current.Version - 1, current.DisplayText!, current.Requirements)); }
        catch (ArgumentException) { return new(EpicOutcomeState.Unknown, history.Version, current.DisplayText, ["invalid-definition"], []); }
        var requirements = current.Requirements.Select(requirement => EvaluateRequirement(requirement, history, receiptProblem)).ToArray();
        var reasons = requirements.SelectMany(requirement => requirement.Reasons).Distinct(StringComparer.Ordinal).ToList();
        if (stale && requirements.Any(requirement => requirement.ObservationSequence == 0)) reasons.Add("stale-version");
        var state = requirements.All(requirement => requirement.State == EpicOutcomeState.Proven) ? EpicOutcomeState.Proven :
            requirements.Any(requirement => requirement.State == EpicOutcomeState.Unmet) ? EpicOutcomeState.Unmet : EpicOutcomeState.Unknown;
        return new(state, history.Version, current.DisplayText, reasons, requirements);
    }

    private static EpicOutcomeRequirementProgress EvaluateRequirement(EpicOutcomeRequirement requirement,
        EpicOutcomeHistory history, Func<EpicOutcomeObservation, string?> receiptProblem)
    {
        var observations = history.Observations.Where(observation => observation.BarVersion == history.Version &&
            observation.RequirementId == requirement.Id).ToArray();
        var sequence = observations.Length == 0 ? 0 : observations.Max(observation => observation.Sequence);
        var superseded = observations.SelectMany(observation => observation.SupersedesReceiptIds).ToHashSet(StringComparer.Ordinal);
        var active = observations.Where(observation => !superseded.Contains(observation.ReceiptId)).ToArray();
        var reasons = new List<string>();
        if (observations.Length == 0) reasons.Add("unevaluated");
        else if (active.Length != 1) reasons.Add("ambiguous-provenance");
        foreach (var observation in active)
        {
            if (string.IsNullOrWhiteSpace(observation.Actor) || string.IsNullOrWhiteSpace(observation.Channel) ||
                !Enum.IsDefined(observation.ActorKind) || observation.Source.Kind != requirement.SourceKind ||
                observation.Source.Authority != requirement.Authority)
                reasons.Add("ambiguous-provenance");
            var problem = receiptProblem(observation);
            if (problem is not null) reasons.Add(problem);
        }
        var state = reasons.Count != 0 ? EpicOutcomeState.Unknown : active[0].Met ? EpicOutcomeState.Proven : EpicOutcomeState.Unmet;
        return new(requirement.Id, requirement.Statement, requirement.Authority, state, sequence,
            active.Select(observation => observation.ReceiptId).ToArray(), reasons.Distinct(StringComparer.Ordinal).ToArray(),
            active.Select(observation => new EpicOutcomeEvidenceReference(observation.ReceiptId, observation.BarVersion,
                observation.Source, observation.Actor, observation.ActorKind, observation.Channel, observation.ObservedAt)).ToArray());
    }
}
