using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed record RemoteLaneTestHostBudgetDecision(
    int? Seconds, string Source, int? HostBudgetSeconds, double? RemoteMedianSeconds,
    int RemoteSamples, double Multiplier, int FloorSeconds);

internal static class RemoteLaneTestHostBudget
{
    internal const int PreambleAllowanceSeconds = 300;
    internal const int MinimumSeconds = 60;
    internal const double HistoryMultiplier = 3.0;
    internal const int HistoryFloorSeconds = 600;

    internal static RemoteLaneTestHostBudgetDecision Decide(
        TimeSpan hostLaneTimeout, RemoteExecutorLaneSeconds? history)
    {
        var hostBudget = Resolve(hostLaneTimeout);
        var samples = history?.Samples ?? 0;
        // Non-finite medians cannot be written by the ledger's JSON serializer.
        double? median = history is not null && double.IsFinite(history.MedianSeconds)
            ? history.MedianSeconds : null;
        var useHistory = hostBudget.HasValue && samples >= RemoteLaneOfferPolicy.MinimumSamples && median is > 0;
        var seconds = useHistory
            ? (int)Math.Min(hostBudget!.Value, Math.Max(HistoryFloorSeconds, Math.Ceiling(HistoryMultiplier * median!.Value)))
            : hostBudget;
        return new(seconds, useHistory ? "remote-history" : "host-budget", hostBudget,
            median, samples, HistoryMultiplier, HistoryFloorSeconds);
    }

    internal static int? Resolve(TimeSpan hostLaneTimeout)
    {
        var total = Math.Floor(hostLaneTimeout.TotalSeconds);
        if (total < 1) return null;
        return (int)Math.Min(int.MaxValue, Math.Min(total, Math.Max(MinimumSeconds, total - PreambleAllowanceSeconds)));
    }
}
