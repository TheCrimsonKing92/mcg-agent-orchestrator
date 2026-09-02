namespace Mcg.AgentOrchestrator.Core;

public sealed record PlannerCandidateDivergenceReceipt(
    int CandidateCount,
    int? SelectedCandidateIndex,
    string SelectionSignal,
    IReadOnlyList<double> CandidateScores,
    IReadOnlyList<PlannerSectionDivergence> Sections,
    IReadOnlyList<string> Diagnostics,
    string SelectionReason = "legacy-selection",
    string? FallbackCause = null,
    IReadOnlyList<PlannerCandidateEvidenceReceipt>? Candidates = null);

public enum PlannerCandidateTerminalState
{
    Succeeded,
    LaunchFailed,
    NonZeroExit,
    TimedOut,
    Cancelled,
    MissingExitArtifact,
    UnreadableTerminalArtifact,
    MalformedTerminalArtifact
}

public enum PlannerCandidateNormalizationState
{
    NotRequired,
    Normalized,
    Empty,
    Malformed,
    Unrecognized,
    Unreadable
}

public enum PlannerCandidateContractVerdict
{
    Valid,
    Invalid,
    NotEvaluated
}

public enum PlannerCandidateUsageState
{
    Reported,
    Unknown
}

public sealed record PlannerStructuralQualityVector(
    int CompleteMappings,
    int ConcreteOwningSeams,
    int FeasibleEvidenceOwners,
    int IntegrationSeams,
    int VerificationClasses,
    int StopConditions);

public sealed record PlannerCandidateUsageReceipt(
    PlannerCandidateUsageState State,
    long? InputTokens,
    long? CachedInputTokens,
    long? OutputTokens,
    string? UnknownReason);

public sealed record PlannerCandidateEvidenceReceipt(
    int CandidateIndex,
    string? CandidateSha256,
    string? ArtifactSha256,
    PlannerCandidateTerminalState TerminalState,
    PlannerCandidateNormalizationState NormalizationState,
    PlannerCandidateContractVerdict ContractVerdict,
    long? ElapsedMilliseconds,
    PlannerCandidateUsageReceipt ProviderUsage,
    PlannerStructuralQualityVector? StructuralQuality,
    string Diagnostic);

public sealed record PlannerSectionDivergence(
    string Section,
    IReadOnlyList<string> AgreedFeatures,
    IReadOnlyList<PlannerCandidateSectionFeatures> Candidates);

public sealed record PlannerCandidateSectionFeatures(
    int CandidateIndex,
    string ContentHash,
    IReadOnlyList<string> DivergentFeatures);
