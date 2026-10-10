namespace Mcg.AgentOrchestrator.Infrastructure;

// Owns file-local extracts for a bounded set of worktrees, never resolved cross-file membership.
internal sealed class AcceptanceTestClassSourceCache
{
    // Retain enough active goal trees to avoid evicting each goal before the next conductor tick.
    internal const int MaxWorktrees = 16;
    private static readonly AcceptanceTestClassSourceCache Shared = new();
    private static readonly AsyncLocal<AcceptanceTestClassSourceCache?> Scoped = new();
    private readonly object _sync = new();
    private readonly Dictionary<string, WorktreeEntry> _worktrees = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _recency = new();
    private long _parsedFileCount;

    internal static AcceptanceTestClassSourceCache Current => Scoped.Value ?? Shared;

    internal static IDisposable Use(AcceptanceTestClassSourceCache cache)
    {
        ArgumentNullException.ThrowIfNull(cache);
        var previous = Scoped.Value;
        Scoped.Value = cache;
        return new Scope(() => Scoped.Value = previous);
    }

    internal long ParsedFileCount
    {
        get { lock (_sync) return _parsedFileCount; }
    }

    internal IReadOnlyList<AcceptanceTestClassSourceScanner.FileExtract> Extract(
        string worktreePath, IReadOnlyList<string> paths,
        Func<string, AcceptanceTestClassSourceScanner.FileExtract> extractFile)
    {
        var root = Path.GetFullPath(worktreePath);
        lock (_sync)
        {
            _worktrees.TryGetValue(root, out var previous);
            var files = new Dictionary<string, CachedFile>(StringComparer.Ordinal);
            var extracts = new AcceptanceTestClassSourceScanner.FileExtract[paths.Count];
            for (var index = 0; index < paths.Count; index++)
            {
                var path = Path.GetFullPath(paths[index]);
                // Stat before reading: never associate old contents with a later write's metadata.
                var info = new FileInfo(path);
                var length = info.Length;
                var lastWriteUtc = info.LastWriteTimeUtc;
                CachedFile? cached = null;
                previous?.Files.TryGetValue(path, out cached);
                if (cached is null || cached.Length != length || cached.LastWriteUtc != lastWriteUtc)
                {
                    var extract = extractFile(path);
                    _parsedFileCount++;
                    cached = new CachedFile(length, lastWriteUtc, extract, (cached?.Parses ?? 0) + 1);
                }
                files.Add(path, cached);
                extracts[index] = cached.Extract;
            }

            // Publish exactly the enumerated paths, dropping removed files even on cache hits.
            if (previous is not null) _recency.Remove(previous.Recency);
            _worktrees[root] = new WorktreeEntry(files, _recency.AddLast(root));
            while (_worktrees.Count > MaxWorktrees)
            {
                _worktrees.Remove(_recency.First!.Value);
                _recency.RemoveFirst();
            }
            return extracts;
        }
    }

    internal void Forget(string worktreePath)
    {
        lock (_sync)
        {
            if (_worktrees.Remove(Path.GetFullPath(worktreePath), out var entry))
                _recency.Remove(entry.Recency);
        }
    }

    internal int ParseCountForTests(string worktreePath, string path)
    {
        lock (_sync)
            return _worktrees.TryGetValue(Path.GetFullPath(worktreePath), out var entry) &&
                entry.Files.TryGetValue(Path.GetFullPath(path), out var file) ? file.Parses : 0;
    }

    internal IReadOnlyCollection<string> CachedPathsForTests(string worktreePath)
    {
        lock (_sync)
            return _worktrees.TryGetValue(Path.GetFullPath(worktreePath), out var entry)
                ? entry.Files.Keys.ToArray() : [];
    }

    private sealed record CachedFile(long Length, DateTime LastWriteUtc,
        AcceptanceTestClassSourceScanner.FileExtract Extract, int Parses);
    private sealed record WorktreeEntry(Dictionary<string, CachedFile> Files, LinkedListNode<string> Recency);

    private sealed class Scope(Action restore) : IDisposable
    {
        public void Dispose() => Interlocked.Exchange(ref _restore, null)?.Invoke();
        private Action? _restore = restore;
    }
}
