using System.Collections.Concurrent;
using Mcg.AgentOrchestrator.Infrastructure;

internal static class AcceptanceTestClassSourcesMemo
{
    private static readonly ConcurrentDictionary<string, Lazy<IReadOnlyList<AcceptanceTestClassSource>>> Sources =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    public static IReadOnlyList<AcceptanceTestClassSource> Get(string repositoryRoot)
    {
        var root = RepositorySourceSnapshotCache.NormalizeRoot(repositoryRoot);
        return Sources.GetOrAdd(root, key => new Lazy<IReadOnlyList<AcceptanceTestClassSource>>(
            () => AcceptanceTestClassSourceScanner.ScanSources(key),
            LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }
}
