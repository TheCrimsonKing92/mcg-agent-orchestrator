namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed partial record AcceptanceCheckResult
{
    public AcceptanceWithinAttemptRerunEvidence? WithinAttemptRerun { get; init; }
}

public sealed record AcceptanceWithinAttemptRerunEvidence(
    string PartitionId,
    string FailedPredicate,
    string OriginalInvocationId,
    string RetryInvocationId,
    int ExecutedTestCount,
    IReadOnlyList<string> RerunTestResultPaths)
{
    public const string Reason = "within-attempt-rerun-pass";
}
