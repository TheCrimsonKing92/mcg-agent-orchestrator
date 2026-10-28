namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class AcceptanceInvocationEvidenceProvenance
{
    internal static (AcceptanceCheckResult Result, bool Retried) Bind(
        (AcceptanceCheckResult Result, bool Retried) result,
        string? attemptId,
        int runOrdinal)
    {
        // Direct invocations (including solution-wide coverage) bypass the partition
        // cache wrapper, so bind fresh evidence to its actual owner here as well.
        return (result.Result with
        {
            TestResultAttemptId = attemptId ?? result.Result.TestResultAttemptId,
            TestResultRunOrdinal = runOrdinal
        }, result.Retried);
    }
}
