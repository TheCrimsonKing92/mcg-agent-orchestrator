namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class BoardFillVerifierBudget
{
    public static TimeSpan For(int bulletCount)
    {
        var minutes = 2d + Math.Max(0, bulletCount);
        return TimeSpan.FromMinutes(Math.Min(minutes, 12));
    }
}
