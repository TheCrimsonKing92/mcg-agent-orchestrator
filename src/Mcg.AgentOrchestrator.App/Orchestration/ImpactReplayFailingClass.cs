namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record ImpactReplayFailingClass(string ClassName, string? Lane, bool? WouldRun)
{
    public bool IsMiss => Lane is not null && WouldRun == false;
}
