namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class AcceptanceManifestLocator
{
    private const string ManifestFileName = "acceptance-manifest.json";

    internal static string Resolve(string worktreePath, string? projectHomeDirectory = null)
    {
        if (!string.IsNullOrWhiteSpace(projectHomeDirectory))
        {
            var projectPath = Path.Combine(projectHomeDirectory, ManifestFileName);
            if (File.Exists(projectPath))
                return projectPath;
        }

        var trackedPath = Path.Combine(worktreePath, "config", ManifestFileName);
        return File.Exists(trackedPath)
            ? trackedPath
            : Path.Combine(worktreePath, ".orchestrator", ManifestFileName);
    }
}
