namespace Mcg.AgentOrchestrator.Core;

public sealed record PlannerCandidateDivergenceReceipt(
    int CandidateCount,
    int SelectedCandidateIndex,
    string SelectionSignal,
    IReadOnlyList<double> CandidateScores,
    IReadOnlyList<PlannerSectionDivergence> Sections,
    IReadOnlyList<string> Diagnostics);

public sealed record PlannerSectionDivergence(
    string Section,
    IReadOnlyList<string> AgreedFeatures,
    IReadOnlyList<PlannerCandidateSectionFeatures> Candidates);

public sealed record PlannerCandidateSectionFeatures(
    int CandidateIndex,
    string ContentHash,
    IReadOnlyList<string> DivergentFeatures);
