namespace Mcg.AgentOrchestrator.Infrastructure;

// Bounds host waiting for a remote lane; executor test-host budgets keep the full check timeout.
internal static class RemoteLaneDeadline
{
    internal static readonly TimeSpan Floor = TimeSpan.FromMinutes(5);
    internal const double Multiplier = 1.5;

    internal static TimeSpan Resolve(RemoteLaneOfferDecision? decision, TimeSpan checkTimeout)
    {
        if (decision is null ||
            (decision.Inputs.LocalSamples < RemoteLaneOfferPolicy.MinimumSamples &&
             decision.Inputs.RemoteSamples < RemoteLaneOfferPolicy.MinimumSamples) ||
            !double.IsFinite(decision.RemoteSeconds) || decision.RemoteSeconds < 0)
            return checkTimeout;

        var seconds = Math.Max(Floor.TotalSeconds, Multiplier * decision.RemoteSeconds);
        return seconds >= checkTimeout.TotalSeconds ? checkTimeout : TimeSpan.FromSeconds(seconds);
    }
}
