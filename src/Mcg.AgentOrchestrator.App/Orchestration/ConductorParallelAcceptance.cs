using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record ConductorParallelAcceptanceCandidate(
    Goal Goal,
    int SlotIndex,
    IReadOnlyList<string> ScopePaths,
    IReadOnlyList<string> ResourceKeys,
    IReadOnlyList<string> ConflictScopePaths,
    IReadOnlyList<string> ExcludedConflictScopePaths,
    string? BranchHeadSha = null,
    string? MainHeadSha = null)
{
    public string GoalPrefix => Goal.Id.Value[..8];

    public bool Overlaps(ConductorParallelAcceptanceCandidate other)
    {
        if (ResourceKeys.Intersect(other.ResourceKeys, StringComparer.OrdinalIgnoreCase).Any())
        {
            return true;
        }

        return ConflictScopePaths.Any(left =>
            other.ConflictScopePaths.Any(right => RepositoryPathOverlap.Overlaps(left, right)));
    }

    internal IReadOnlyList<string> GetDocumentationExclusionEvidence(
        ConductorParallelAcceptanceCandidate other)
    {
        if (Overlaps(other) || !OverlapsWithoutDocumentationExclusion(other))
        {
            return [];
        }

        return ExcludedConflictScopePaths
            .Concat(other.ExcludedConflictScopePaths)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public string CandidateKey =>
        $"{Goal.Id.Value}:{BranchHeadSha ?? "unknown-branch"}:{MainHeadSha ?? "unknown-main"}";

    public static ConductorParallelAcceptanceCandidate Create(
        Goal goal,
        int slotIndex,
        IReadOnlyList<string> fileScopes,
        string? branchHeadSha = null,
        string? mainHeadSha = null)
    {
        var scope = RepositoryLandingScopeNormalization.Normalize(
            fileScopes,
            reserveUnknownScope: true);

        return new ConductorParallelAcceptanceCandidate(
            goal,
            slotIndex,
            scope.Paths,
            scope.ResourceKeys,
            scope.ConflictPaths,
            scope.ExcludedConflictPaths,
            branchHeadSha,
            mainHeadSha);
    }

    internal static bool IsDocumentationExcludedFromConflict(string path)
        => RepositoryLandingScopeNormalization.IsDocumentationExcludedFromConflict(path);

    private bool OverlapsWithoutDocumentationExclusion(ConductorParallelAcceptanceCandidate other)
    {
        var resources = RepositoryLandingScopeNormalization.BuildResourceKeys(
            ScopePaths,
            reserveUnknownScope: ScopePaths.Count == 0);
        var otherResources = RepositoryLandingScopeNormalization.BuildResourceKeys(
            other.ScopePaths,
            reserveUnknownScope: other.ScopePaths.Count == 0);
        if (resources.Intersect(otherResources, StringComparer.OrdinalIgnoreCase).Any())
        {
            return true;
        }

        return ScopePaths.Any(left =>
            other.ScopePaths.Any(right => RepositoryPathOverlap.Overlaps(left, right)));
    }

}

internal sealed record ConductorParallelAcceptanceRunResult(
    ConductorParallelAcceptanceCandidate Candidate,
    AcceptanceVerificationSummary? Acceptance,
    ConductorAdvanceResult? EarlyResult,
    Exception? Exception,
    ConductorParallelAcceptanceEarlyOutcome? EarlyOutcome = null,
    FocusedEvidenceRunResult? FocusedEvidence = null)
{
    public static ConductorParallelAcceptanceRunResult Accepted(
        ConductorParallelAcceptanceCandidate candidate,
        AcceptanceVerificationSummary acceptance) =>
        new(candidate, acceptance, null, null);

    public static ConductorParallelAcceptanceRunResult Early(
        ConductorParallelAcceptanceCandidate candidate,
        ConductorAdvanceResult result,
        ConductorParallelAcceptanceEarlyOutcome? outcome = null) =>
        new(candidate, null, result, null, outcome);

    public static ConductorParallelAcceptanceRunResult Fault(
        ConductorParallelAcceptanceCandidate candidate,
        Exception exception) =>
        new(candidate, null, null, exception);

    public static ConductorParallelAcceptanceRunResult Focused(
        ConductorParallelAcceptanceCandidate candidate,
        FocusedEvidenceRunResult focusedEvidence) =>
        new(candidate, null, null, null, FocusedEvidence: focusedEvidence);
}

internal sealed record ConductorParallelAcceptanceEarlyOutcome(
    string Kind,
    GoalLifecycleState State,
    string Detail)
{
    public const string MissingBranchRetiredKind = "missing-branch-retired";
    public const string PreLandingEscalatedKind = "pre-landing-escalated";

    public static ConductorParallelAcceptanceEarlyOutcome MissingBranchRetired(
        GoalLifecycleState state,
        string detail) =>
        new(MissingBranchRetiredKind, state, detail);

    public static ConductorParallelAcceptanceEarlyOutcome PreLandingEscalated(
        GoalLifecycleState state,
        string detail) =>
        new(PreLandingEscalatedKind, state, detail);
}
