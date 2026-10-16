using System.Collections.Concurrent;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Each execution root/registry pair owns one route, valid only for its registry stamp.
internal static class GoalOperationRunEventStoreLocator
{
    private static readonly ConcurrentDictionary<(string Execution, string Registry), CachedRoute> Routes = new();

    internal static string Resolve(string executionDirectory, OrchestratorProjectRegistry registry) =>
        Resolve(executionDirectory, registry, static value => value.ListProjects());

    internal static string Resolve(
        string executionDirectory,
        OrchestratorProjectRegistry registry,
        Func<OrchestratorProjectRegistry, IReadOnlyList<OrchestratorProject>> listProjects)
    {
        var executionRoot = DefaultProjectStateLocation.CanonicalRoot(executionDirectory);
        var registryPath = Path.GetFullPath(registry.RegistryPath);
        var route = Routes.GetOrAdd((Key(executionRoot), Key(registryPath)), static _ => new CachedRoute());

        // Serialize refreshes for this pair only, including concurrent first callers.
        lock (route)
        {
            RegistryStamp stamp;
            try
            {
                var file = new FileInfo(registryPath);
                file.Refresh();
                stamp = file.Exists
                    ? new RegistryStamp(true, file.LastWriteTimeUtc, file.Length)
                    : default;
            }
            catch
            {
                return LegacyPath(executionDirectory);
            }

            if (route.Path is not null && route.Stamp == stamp)
                return route.Path;

            string path;
            try
            {
                var matches = listProjects(registry)
                    .Where(project => DefaultProjectStateLocation.SameRoot(project.RootDirectory, executionRoot))
                    .Take(2)
                    .ToArray();
                path = matches.Length switch
                {
                    1 => matches[0].ResolveWorkspace().RunEventStorePath,
                    0 => OrchestratorWorkspace.ForDirectory(executionRoot, executionRoot, null, registry).RunEventStorePath,
                    _ => LegacyPath(executionDirectory)
                };
            }
            catch
            {
                // Cache failed reads too; a repaired registry's new stamp retries resolution.
                path = LegacyPath(executionDirectory);
            }

            route.Stamp = stamp;
            route.Path = path;
            return path;
        }
    }

    private static string Key(string path) => OperatingSystem.IsWindows() ? path.ToUpperInvariant() : path;

    private static string LegacyPath(string executionDirectory) =>
        Path.Combine(Path.GetFullPath(executionDirectory), ".orchestrator", "run-events.db");

    private readonly record struct RegistryStamp(bool Exists, DateTime LastWriteTimeUtc, long Length);

    private sealed class CachedRoute
    {
        internal RegistryStamp Stamp;
        internal string? Path;
    }
}
