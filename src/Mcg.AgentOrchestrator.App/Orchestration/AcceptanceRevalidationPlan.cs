namespace Mcg.AgentOrchestrator.App.Orchestration;

internal abstract record AcceptanceRevalidationPlan
{
    internal sealed record Unchanged : AcceptanceRevalidationPlan;

    internal sealed record Revalidate(
        IReadOnlyList<string> LandedPaths,
        IReadOnlyList<string> CandidatePaths,
        IReadOnlyList<string> AffectedChecks,
        IReadOnlyList<string> UnaffectedChecks) : AcceptanceRevalidationPlan;

    internal sealed record FullRegate(string Reason, string? Path = null, string? Kind = null)
        : AcceptanceRevalidationPlan;
}
