namespace Mcg.AgentOrchestrator.Core;

public sealed record RepositoryLandingScope(
    IReadOnlyList<string> Paths,
    IReadOnlyList<string> ResourceKeys,
    IReadOnlyList<string> ConflictPaths,
    IReadOnlyList<string> ExcludedConflictPaths);

public static class RepositoryLandingScopeNormalization
{
    public const string UnknownAcceptanceScopeResourceKey = "ownership:unknown-acceptance-scope";

    public static RepositoryLandingScope Normalize(
        IEnumerable<string> fileScopes,
        bool reserveUnknownScope = false)
    {
        ArgumentNullException.ThrowIfNull(fileScopes);

        var paths = fileScopes
            .Where(scope => !string.IsNullOrWhiteSpace(scope))
            .Select(RepositoryPathOverlap.Normalize)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var conflictPaths = paths
            .Where(path => !IsDocumentationExcludedFromConflict(path))
            .ToArray();
        var excludedConflictPaths = paths
            .Where(IsDocumentationExcludedFromConflict)
            .ToArray();
        var resourceKeys = BuildResourceKeys(
            conflictPaths,
            reserveUnknownScope && paths.Length == 0);

        return new RepositoryLandingScope(
            paths,
            resourceKeys,
            conflictPaths,
            excludedConflictPaths);
    }

    public static IReadOnlyList<string> BuildResourceKeys(
        IReadOnlyList<string> normalizedPaths,
        bool reserveUnknownScope = false)
    {
        ArgumentNullException.ThrowIfNull(normalizedPaths);
        if (normalizedPaths.Count == 0)
        {
            return reserveUnknownScope ? [UnknownAcceptanceScopeResourceKey] : [];
        }

        return normalizedPaths
            .Select(RepositoryOwnershipMap.Classify)
            .Where(path => path.RequiresSerialization)
            .Select(path => $"ownership:{path.ReservationKey}")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static bool IsDocumentationExcludedFromConflict(string path)
    {
        var normalized = RepositoryPathOverlap.Normalize(path);
        return normalized.Equals("docs", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("docs/", StringComparison.OrdinalIgnoreCase) ||
            Path.GetExtension(normalized).Equals(".md", StringComparison.OrdinalIgnoreCase);
    }
}
