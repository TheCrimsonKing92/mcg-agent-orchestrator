namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed partial record AcceptanceCheckResult
{
    public AcceptanceLaneRerunEvidence? LaneRerun { get; init; }
}

public sealed record AcceptanceLaneRerunEvidence(
    string FirstInvocationId,
    IReadOnlyList<string> FirstTestResultPaths,
    IReadOnlyList<string> FirstFailingTestIdentities,
    string FirstPredicate,
    string RerunInvocationId,
    IReadOnlyList<string> RerunTestResultPaths,
    IReadOnlyList<string> RerunFailingTestIdentities,
    string? RerunPredicate,
    string Outcome)
{
    public const string Flake = "flake";
    public const string ConfirmedFailure = "confirmed-failure";
}
