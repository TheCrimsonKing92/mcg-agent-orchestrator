namespace Mcg.AgentOrchestrator.Core;

public enum DeferredNoChangeEvidenceAvailability { Green, Red, Pending, Unavailable }

/// <summary>A host-captured context snapshot, never a finding outcome or an evidence store.</summary>
public sealed record DeferredNoChangeEvidenceContext(
    string GoalId,
    string? CurrentCandidateSha,
    IReadOnlyList<DeferredNoChangeEvidenceSnapshot> Receipts);

public sealed record DeferredNoChangeEvidenceSnapshot(
    string DeveloperTaskId,
    string CandidateSha,
    string? AttemptId,
    int? Ordinal,
    DateTimeOffset? StartedAt,
    bool IsCurrent,
    DeferredNoChangeEvidenceAvailability Availability,
    IReadOnlyList<string> Selections,
    IReadOnlyList<string> Declared,
    IReadOnlyList<string> NotRun,
    FindingEvidenceArmReceipt? CandidateArm,
    string? BodyJson,
    string? Diagnostic);
