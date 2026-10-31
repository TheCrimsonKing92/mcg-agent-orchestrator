namespace Mcg.AgentOrchestrator.Infrastructure;

// A repository-local pkg/mod subtree contains downloaded modules, not project source.
internal static class GoModuleCacheTree
{
    internal static bool IsCacheSubtree(string directory) =>
        string.Equals(Path.GetFileName(directory), "mod", StringComparison.Ordinal) &&
        string.Equals(Path.GetFileName(Path.GetDirectoryName(directory)), "pkg", StringComparison.Ordinal);
}
