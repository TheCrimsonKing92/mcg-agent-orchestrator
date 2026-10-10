namespace Mcg.AgentOrchestrator.Core;

internal static class RoleRequirementManifestPath
{
    private const string HomeManifestPath = "config/acceptance-manifest.json";

    public static IReadOnlyList<string> Apply(IReadOnlyList<string> lines, string? manifestPath)
    {
        if (string.IsNullOrWhiteSpace(manifestPath) ||
            string.Equals(manifestPath, HomeManifestPath, StringComparison.Ordinal))
            return lines;

        return lines.Select(line => line.Replace(HomeManifestPath, manifestPath, StringComparison.Ordinal)).ToArray();
    }
}
