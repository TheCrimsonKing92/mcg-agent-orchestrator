using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

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
        var paths = fileScopes
            .Where(scope => !string.IsNullOrWhiteSpace(scope))
            .Select(RepositoryPathOverlap.Normalize)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var conflictPaths = paths
            .Where(path => !IsDocumentationExcludedFromConflict(path))
            .ToArray();
        var excludedConflictPaths = paths
            .Where(IsDocumentationExcludedFromConflict)
            .ToArray();

        var resources = BuildResourceKeys(
            conflictPaths,
            reserveUnknownScope: paths.Length == 0);

        return new ConductorParallelAcceptanceCandidate(
            goal,
            slotIndex,
            paths,
            resources,
            conflictPaths,
            excludedConflictPaths,
            branchHeadSha,
            mainHeadSha);
    }

    internal static bool IsDocumentationExcludedFromConflict(string path)
    {
        var normalized = RepositoryPathOverlap.Normalize(path);
        return normalized.Equals("docs", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("docs/", StringComparison.OrdinalIgnoreCase) ||
            Path.GetExtension(normalized).Equals(".md", StringComparison.OrdinalIgnoreCase);
    }

    private bool OverlapsWithoutDocumentationExclusion(ConductorParallelAcceptanceCandidate other)
    {
        var resources = BuildResourceKeys(ScopePaths, reserveUnknownScope: ScopePaths.Count == 0);
        var otherResources = BuildResourceKeys(
            other.ScopePaths,
            reserveUnknownScope: other.ScopePaths.Count == 0);
        if (resources.Intersect(otherResources, StringComparer.OrdinalIgnoreCase).Any())
        {
            return true;
        }

        return ScopePaths.Any(left =>
            other.ScopePaths.Any(right => RepositoryPathOverlap.Overlaps(left, right)));
    }

    private static string[] BuildResourceKeys(
        IReadOnlyList<string> paths,
        bool reserveUnknownScope)
    {
        if (paths.Count == 0)
        {
            return reserveUnknownScope ? ["ownership:unknown-acceptance-scope"] : [];
        }

        return paths
            .Select(RepositoryOwnershipMap.Classify)
            .Where(path => path.RequiresSerialization)
            .Select(path => $"ownership:{path.ReservationKey}")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}

internal sealed record ConductorParallelAcceptanceRunResult(
    ConductorParallelAcceptanceCandidate Candidate,
    AcceptanceVerificationSummary? Acceptance,
    ConductorAdvanceResult? EarlyResult,
    Exception? Exception,
    ConductorParallelAcceptanceEarlyOutcome? EarlyOutcome = null)
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
