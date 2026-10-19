namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class ConductorSuccessorExitRetryPolicy
{
    internal abstract record Decision;
    internal sealed record Retry(TimeSpan Delay) : Decision;
    internal sealed record NoRetry(string Reason) : Decision;

    internal static Decision Decide(bool exitedBeforeReadiness, int? exitCode, bool deliberateStop,
        string failedHead, ISet<string> retriedHeads, TimeSpan? configuredDelay)
    {
        if (!exitedBeforeReadiness) return new NoRetry("not-before-readiness");
        if (exitCode is null or 0) return new NoRetry("no-failure-exit-code");
        if (deliberateStop) return new NoRetry("deliberate-stop");
        if (configuredDelay is null) return new NoRetry("disabled");
        if (retriedHeads.Any(head => head.Equals(failedHead, StringComparison.OrdinalIgnoreCase)))
            return new NoRetry("head-already-retried");
        return new Retry(configuredDelay.Value);
    }
}
