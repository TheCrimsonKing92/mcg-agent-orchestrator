using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class OwnerProtectedLandingPaths
{
    private const string ManifestPath = "config/acceptance-manifest.json";

    internal static bool Contains(IReadOnlyList<string>? landingPaths)
    {
        foreach (var path in landingPaths ?? [])
        {
            if (string.IsNullOrWhiteSpace(path))
                continue;
            if (RepositoryChangeClassifier.IsOwnerProtectedPolicyPath(path))
                return true;

            var normalized = path.Replace('\\', '/');
            while (normalized.StartsWith("./", StringComparison.Ordinal) ||
                   normalized.StartsWith("/", StringComparison.Ordinal))
            {
                normalized = normalized.StartsWith("./", StringComparison.Ordinal)
                    ? normalized[2..]
                    : normalized[1..];
            }
            if (normalized.Equals(ManifestPath, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
