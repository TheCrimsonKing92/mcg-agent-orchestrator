namespace Mcg.AgentOrchestrator.Infrastructure;

// The target repository's identity, independent of where install assets are resolved.
public sealed class WorkerTargetHome(bool isOrchestratorHome = true, Func<string, string>? acceptanceManifestPath = null)
{
    public bool IsOrchestratorHome { get; } = isOrchestratorHome;
    public Func<string, string>? AcceptanceManifestPath { get; } = acceptanceManifestPath;
    public static WorkerTargetHome Home { get; } = new();
    public static WorkerTargetHome NotHome { get; } = new(false);
}
