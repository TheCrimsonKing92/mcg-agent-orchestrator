namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class RemoteLaneTestHostBudget
{
    internal const int PreambleAllowanceSeconds = 300;
    internal const int MinimumSeconds = 60;

    internal static int? Resolve(TimeSpan hostLaneTimeout)
    {
        var total = Math.Floor(hostLaneTimeout.TotalSeconds);
        if (total < 1) return null;
        return (int)Math.Min(int.MaxValue, Math.Min(total, Math.Max(MinimumSeconds, total - PreambleAllowanceSeconds)));
    }
}
