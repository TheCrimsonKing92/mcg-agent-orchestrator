namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record DefaultStateMoveResult(IReadOnlyList<string> PlanLines, IReadOnlyList<string> Refusals)
{
    public bool Succeeded => Refusals.Count == 0;
    public int ExitCode => Succeeded ? 0 : 1;
}
