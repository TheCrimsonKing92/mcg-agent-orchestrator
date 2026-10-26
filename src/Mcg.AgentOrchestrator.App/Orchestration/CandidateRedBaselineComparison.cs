namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record CandidateRedBaselineComparison(
    CandidateRedBaselineVerdict Verdict,
    IReadOnlyList<string> CandidateFailures,
    IReadOnlyList<string> BaselineFailures,
    IReadOnlyList<string> CandidateOnlyFailures);
