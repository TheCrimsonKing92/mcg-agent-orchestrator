using System.Collections.Concurrent;

internal static class RepositorySourceSnapshotCache
{
    private static readonly ConcurrentDictionary<string, Lazy<RepositorySourceSnapshot>> Snapshots =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    internal static string NormalizeRoot(string root) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));

    public static RepositorySourceSnapshot Get(string repositoryRoot, ISourceSnapshotObserver? observer = null)
    {
        var root = NormalizeRoot(repositoryRoot);
        var snapshot = Snapshots.GetOrAdd(root, key => new Lazy<RepositorySourceSnapshot>(
            () => new RepositorySourceSnapshot(key, observer ?? NoOpObserver.Instance),
            LazyThreadSafetyMode.ExecutionAndPublication)).Value;
        if (observer is not null && !ReferenceEquals(observer, snapshot.Observer))
            throw new InvalidOperationException("A repository snapshot is already bound to a different observer.");
        return snapshot;
    }

    private sealed class NoOpObserver : ISourceSnapshotObserver
    {
        public static readonly NoOpObserver Instance = new();
        public void OnEnumerated(string root) { }
        public void OnTextRead(string root, string relativePath) { }
        public void OnParsed(string root, string relativePath) { }
    }
}
