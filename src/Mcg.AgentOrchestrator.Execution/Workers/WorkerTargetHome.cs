namespace Mcg.AgentOrchestrator.Infrastructure;

// The target repository's identity, independent of where install assets are resolved.
public sealed class WorkerTargetHome(bool isOrchestratorHome = true)
{
    public bool IsOrchestratorHome { get; } = isOrchestratorHome;

    public static WorkerTargetHome Home { get; } = new();
    public static WorkerTargetHome NotHome { get; } = new(false);
}
