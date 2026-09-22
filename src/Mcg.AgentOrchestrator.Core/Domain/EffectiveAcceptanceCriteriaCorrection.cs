namespace Mcg.AgentOrchestrator.Core;

public sealed record EffectiveAcceptanceCriteriaCorrection(
    string SupersededCriterion,
    string Correction,
    string Actor,
    DateTimeOffset RecordedAt,
    TaskId? SourceTaskId,
    ProgressKind SourceKind,
    bool IsWaiver = false,
    string? CapturedAcceptanceCriteriaHash = null,
    IReadOnlyList<CriterionDisposition>? Dispositions = null)
{
    private const string WaiverPrefix = "WAIVED: ";

    public string? WaiverReason => IsWaiver
        ? (Correction.StartsWith(WaiverPrefix, StringComparison.Ordinal)
            ? Correction[WaiverPrefix.Length..]
            : Correction).Trim()
        : null;

    public static EffectiveAcceptanceCriteriaCorrection Waiver(
        string criterion,
        string reason,
        string actor,
        DateTimeOffset recordedAt,
        IReadOnlyList<CriterionDisposition>? dispositions = null) =>
        new(
            criterion,
            $"{WaiverPrefix}{reason}",
            actor,
            recordedAt,
            null,
            ProgressKind.GoalPolicyDecision,
            IsWaiver: true,
            Dispositions: dispositions);
}

public sealed record CriterionDisposition(string Criterion, string Disposition);

public sealed record CriterionDispositionRequest(string CriterionReference, string Disposition);
