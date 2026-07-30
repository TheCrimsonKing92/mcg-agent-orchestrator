using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record ConductorParallelAcceptanceCandidate(
    Goal Goal,
    int SlotIndex,
    IReadOnlyList<string> ScopePaths,
    IReadOnlyList<string> ResourceKeys,
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

        return ScopePaths.Any(left => other.ScopePaths.Any(right => RepositoryPathOverlap.Overlaps(left, right)));
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

        var resources = paths.Length == 0
            ? new[] { "ownership:unknown-acceptance-scope" }
            : paths
                .Select(RepositoryOwnershipMap.Classify)
                .Where(path => path.RequiresSerialization)
                .Select(path => $"ownership:{path.ReservationKey}")
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();

        return new ConductorParallelAcceptanceCandidate(goal, slotIndex, paths, resources, branchHeadSha, mainHeadSha);
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
