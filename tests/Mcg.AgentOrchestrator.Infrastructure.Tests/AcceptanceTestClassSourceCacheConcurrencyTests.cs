using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;
using FileExtract = Mcg.AgentOrchestrator.Infrastructure.AcceptanceTestClassSourceScanner.FileExtract;

// Parallel-safe: private caches, unique temp roots and test-owned event gates only.
public sealed class AcceptanceTestClassSourceCacheConcurrencyTests
{
    [Fact]
    public async Task DifferentRoots_BlockedExtract_DoesNotBlockOtherRoot()
    {
        using var tree = new TestTree();
        var (rootA, filesA) = tree.WriteRoot("a", 1);
        var (rootB, filesB) = tree.WriteRoot("b", 1);
        var cache = new AcceptanceTestClassSourceCache();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var first = Task.Run(() => cache.Extract(rootA, filesA, _ =>
        {
            entered.SetResult();
            release.Wait();
            return new FileExtract([], []);
        }));
        Task<IReadOnlyList<FileExtract>>? second = null;
        try
        {
            await TestHangGuard.WaitAsync(entered.Task, "root A entered its extract callback");
            second = Task.Run(() => cache.Extract(rootB, filesB, _ => new FileExtract([], [])));
            await TestHangGuard.WaitAsync(second, "root B published while root A remained blocked");
            Assert.False(first.IsCompleted);
        }
        finally
        {
            release.Set();
            await TestHangGuard.WaitAsync(first, "root A completed after release");
            if (second is not null) await TestHangGuard.WaitAsync(second, "root B completed");
        }

        Assert.Equal(1, cache.ParseCountForTests(rootA, filesA[0]));
        Assert.Equal(1, cache.ParseCountForTests(rootB, filesB[0]));
        Assert.Equal(2, cache.ParsedFileCount);
        Assert.Equal(filesA[0], Assert.Single(cache.CachedPathsForTests(rootA)));
        Assert.Equal(filesB[0], Assert.Single(cache.CachedPathsForTests(rootB)));
    }

    [Fact]
    public async Task SameRoot_OverlappingCalls_ReuseExtractsLikeSequentialCalls()
    {
        using var tree = new TestTree();
        var (root, paths) = tree.WriteRoot("same", 3);
        var cache = new AcceptanceTestClassSourceCache();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var first = Task.Run(() => cache.Extract(root, paths, path =>
        {
            entered.TrySetResult();
            release.Wait();
            return Extract(path);
        }));
        Task<IReadOnlyList<FileExtract>>? second = null;
        try
        {
            await TestHangGuard.WaitAsync(entered.Task, "first same-root extract entered");
            second = Task.Run(() =>
            {
                secondStarted.SetResult();
                return cache.Extract(root, paths, Extract);
            });
            await TestHangGuard.WaitAsync(secondStarted.Task, "second same-root call started");
        }
        finally
        {
            release.Set();
            await TestHangGuard.WaitAsync(first, "first same-root extract completed");
            if (second is not null) await TestHangGuard.WaitAsync(second, "second same-root extract completed");
        }

        Assert.NotNull(second);
        var firstExtracts = await first;
        var secondExtracts = await second;
        Assert.Equal(firstExtracts.ToArray(), secondExtracts.ToArray());
        var sequential = new AcceptanceTestClassSourceCache();
        var controlFirst = sequential.Extract(root, paths, Extract);
        var controlSecond = sequential.Extract(root, paths, Extract);
        Assert.Equal(controlFirst.ToArray(), controlSecond.ToArray());
        Assert.Equal(controlFirst.SelectMany(file => file.Constants),
            firstExtracts.SelectMany(file => file.Constants));
        Assert.Equal(3, cache.ParsedFileCount);
        Assert.Equal(sequential.ParsedFileCount, cache.ParsedFileCount);
        foreach (var path in paths)
        {
            Assert.Equal(1, cache.ParseCountForTests(root, path));
            Assert.Equal(sequential.ParseCountForTests(root, path), cache.ParseCountForTests(root, path));
        }
    }

    private static FileExtract Extract(string path) => new(
        [new AcceptanceTestClassSourceScanner.ConstantExtract(path, path)], []);

    private sealed class TestTree : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "mcg-cache-concurrency", Guid.NewGuid().ToString("N"));

        internal (string Root, string[] Paths) WriteRoot(string name, int count)
        {
            var root = Path.Combine(_root, name);
            Directory.CreateDirectory(root);
            var paths = Enumerable.Range(0, count).Select(index => Path.Combine(root, $"File{index}.cs")).ToArray();
            foreach (var path in paths) File.WriteAllText(path, "class Sample {}");
            return (root, paths);
        }

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
