namespace Mcg.AgentOrchestrator.Core;

public sealed record EffectiveAcceptanceCriteriaCorrection(
    string SupersededCriterion,
    string Correction,
    string Actor,
    DateTimeOffset RecordedAt,
    TaskId? SourceTaskId,
    ProgressKind SourceKind)
{
    private const string WaiverPrefix = "WAIVED: ";

    public bool IsWaiver => Correction.StartsWith(WaiverPrefix, StringComparison.OrdinalIgnoreCase);

    public string? WaiverReason => IsWaiver ? Correction[WaiverPrefix.Length..].Trim() : null;

    public static EffectiveAcceptanceCriteriaCorrection Waiver(
        string criterion,
        string reason,
        string actor,
        DateTimeOffset recordedAt) =>
        new(criterion, $"{WaiverPrefix}{reason}", actor, recordedAt, null, ProgressKind.GoalPolicyDecision);
}
