namespace Mcg.AgentOrchestrator.Infrastructure;

public static class ExperimentStopTarget
{
    public static int Effective(ExperimentStopRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        return rule.Extensions is { Count: > 0 } extensions ? extensions[^1].NewCount : rule.Count;
    }
}
