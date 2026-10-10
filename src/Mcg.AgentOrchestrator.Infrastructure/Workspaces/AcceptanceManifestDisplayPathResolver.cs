namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class AcceptanceManifestDisplayPathResolver
{
    internal static string Resolve(string worktreePath, string? projectHomeDirectory = null) =>
        WorkerAcceptanceManifestDisplayPath.Render(worktreePath,
            AcceptanceManifestLocator.Resolve(worktreePath, projectHomeDirectory));
}
