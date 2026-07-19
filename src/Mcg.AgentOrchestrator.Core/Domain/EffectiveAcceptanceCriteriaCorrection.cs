namespace Mcg.AgentOrchestrator.Core;

public sealed record EffectiveAcceptanceCriteriaCorrection(
    string SupersededCriterion,
    string Correction,
    string Actor,
    DateTimeOffset RecordedAt,
    TaskId? SourceTaskId,
    ProgressKind SourceKind);
