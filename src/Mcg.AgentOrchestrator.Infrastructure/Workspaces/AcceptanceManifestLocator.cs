namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class AcceptanceManifestLocator
{
    private const string ManifestFileName = "acceptance-manifest.json";

    internal static string Resolve(string worktreePath, string? projectHomeDirectory = null)
    {
        var locations = SearchedLocations(worktreePath, projectHomeDirectory);
        return locations.FirstOrDefault(File.Exists) ?? locations[^1];
    }

    internal static IReadOnlyList<string> SearchedLocations(string worktreePath, string? projectHomeDirectory = null)
    {
        var locations = new List<string>();
        if (!string.IsNullOrWhiteSpace(projectHomeDirectory))
            locations.Add(Path.Combine(projectHomeDirectory, ManifestFileName));

        locations.Add(Path.Combine(worktreePath, "config", ManifestFileName));
        locations.Add(Path.Combine(worktreePath, ".orchestrator", ManifestFileName));
        return locations.AsReadOnly();
    }
}
