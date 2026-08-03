namespace Mcg.AgentOrchestrator.Core;

public sealed record EffectiveAcceptanceCriteriaCorrection(
    string SupersededCriterion,
    string Correction,
    string Actor,
    DateTimeOffset RecordedAt,
    TaskId? SourceTaskId,
    ProgressKind SourceKind,
    bool IsWaiver = false)
{
    private const string WaiverPrefix = "WAIVED: ";

    public string? WaiverReason => IsWaiver ? Correction[WaiverPrefix.Length..].Trim() : null;

    public static EffectiveAcceptanceCriteriaCorrection Waiver(
        string criterion,
        string reason,
        string actor,
        DateTimeOffset recordedAt) =>
        new(criterion, $"{WaiverPrefix}{reason}", actor, recordedAt, null, ProgressKind.GoalPolicyDecision, IsWaiver: true);
}
