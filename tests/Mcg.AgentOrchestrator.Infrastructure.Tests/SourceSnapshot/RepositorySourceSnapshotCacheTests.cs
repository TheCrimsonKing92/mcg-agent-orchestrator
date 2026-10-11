using System.Collections.Concurrent;
using Microsoft.CodeAnalysis;

// Parallel-safe: unique fixture roots, per-root observer counters, event-gated callers.
public sealed class RepositorySourceSnapshotCacheTests
{
    private const string SourcePath = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Launch.cs";

    [Fact]
    public void FiveIndependentCallersEnumerateReadAndParseOnce()
    {
        using var fixture = new SourceSnapshotFixture();
        fixture.Write(SourcePath, "class C {}");
        var observer = new CountingObserver();
        var trees = new[]
        {
            CallerA(fixture.Root, observer), CallerB(fixture.Root, observer), CallerC(fixture.Root, observer),
            CallerD(fixture.Root, observer), CallerE(fixture.Root, observer)
        };
        Assert.All(trees, tree => Assert.Same(trees[0], tree));
        AssertWork(observer, fixture.Root, SourcePath);
    }

    [Fact]
    public async Task ConcurrentCallersShareOneEnumerationAndOneParsePerFile()
    {
        using var fixture = new SourceSnapshotFixture();
        fixture.Write(SourcePath, "class C {}");
        fixture.Write("src/A/Other.cs", "class D {}");
        var observer = new CountingObserver();
        using var ready = new CountdownEvent(16);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callers = Enumerable.Range(0, 16).Select(_ => Task.Run(async () =>
        {
            ready.Signal();
            await release.Task;
            var snapshot = RepositorySourceSnapshotCache.Get(fixture.Root, observer);
            var trees = snapshot.Files.Select(snapshot.GetSyntaxTree).ToArray();
            return (snapshot, trees);
        })).ToArray();
        bool reachedGate;
        try
        {
            reachedGate = ready.Wait(TimeSpan.FromSeconds(30));
        }
        finally
        {
            release.TrySetResult();
        }
        var results = await Task.WhenAll(callers).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(reachedGate, "Snapshot callers did not reach the release gate.");
        Assert.All(results, result =>
        {
            Assert.Same(results[0].snapshot, result.snapshot);
            for (var index = 0; index < result.trees.Length; index++)
                Assert.Same(results[0].trees[index], result.trees[index]);
        });
        foreach (var path in results[0].snapshot.Files) AssertWork(observer, fixture.Root, path);
    }

    [Fact]
    public void DifferentRootsKeepCountsTreesAndGuardFindingsSeparate()
    {
        using var first = new SourceSnapshotFixture();
        using var second = new SourceSnapshotFixture();
        first.Write(SourcePath, "class A { void M() { var p = new " + "ProcessStartInfo(\"pwsh\"); } }");
        const string secondPath = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Second.cs";
        second.Write(secondPath, "class B { void M() { var p = new " + "ProcessStartInfo(\"pwsh\"); } }");
        var observerA = new CountingObserver();
        var observerB = new CountingObserver();
        var snapshotA = RepositorySourceSnapshotCache.Get(first.Root, observerA);
        var treeA = snapshotA.GetSyntaxTree(SourcePath);
        AssertWork(observerA, first.Root, SourcePath);
        var before = observerA.Counts.OrderBy(pair => pair.Key).ToArray();
        var snapshotB = RepositorySourceSnapshotCache.Get(second.Root, observerB);
        var treeB = snapshotB.GetSyntaxTree(secondPath);

        Assert.NotSame(snapshotA, snapshotB);
        Assert.NotSame(treeA, treeB);
        AssertWork(observerB, second.Root, secondPath);
        Assert.Equal(before, observerA.Counts.OrderBy(pair => pair.Key));
        var findingsA = GuardFindings(first);
        var findingsB = GuardFindings(second);
        Assert.Equal(new[] { "Launch.cs:1" }, findingsA);
        Assert.Equal(new[] { "Second.cs:1" }, findingsB);
        Assert.Equal(before, observerA.Counts.OrderBy(pair => pair.Key));
    }

    [Fact]
    public void NormalizedFullRootSharesSnapshotAndSiblingDoesNot()
    {
        using var fixture = new SourceSnapshotFixture();
        using var sibling = new SourceSnapshotFixture();
        fixture.Write(SourcePath, "class C {}");
        sibling.Write(SourcePath, "class D {}");
        var observer = new CountingObserver();
        var snapshot = RepositorySourceSnapshotCache.Get(fixture.Root, observer);
        Assert.Same(snapshot, RepositorySourceSnapshotCache.Get(fixture.Root + Path.DirectorySeparatorChar, observer));
        Assert.Same(snapshot, RepositorySourceSnapshotCache.Get(fixture.Root.Replace('\\', '/'), observer));
        Assert.Same(snapshot, RepositorySourceSnapshotCache.Get(Path.Combine(fixture.Root, "."), observer));
        if (OperatingSystem.IsWindows())
            Assert.Same(snapshot, RepositorySourceSnapshotCache.Get(fixture.Root.ToUpperInvariant(), observer));
        Assert.NotSame(snapshot, RepositorySourceSnapshotCache.Get(sibling.Root));
        var prefixCollision = fixture.Root + "x";
        Directory.CreateDirectory(prefixCollision);
        try
        {
            var otherObserver = new CountingObserver();
            Assert.NotSame(snapshot, RepositorySourceSnapshotCache.Get(prefixCollision, otherObserver));
            Assert.Equal(1, otherObserver.Count("enumerated", prefixCollision));
        }
        finally { Directory.Delete(prefixCollision); }
        Assert.Equal(1, observer.Count("enumerated", fixture.Root));
    }

    [Fact]
    public void ChangingObserverForExistingRootFailsInsteadOfSilentlyMiscounting()
    {
        using var fixture = new SourceSnapshotFixture();
        var observer = new CountingObserver();
        var snapshot = RepositorySourceSnapshotCache.Get(fixture.Root, observer);
        Assert.Same(snapshot, RepositorySourceSnapshotCache.Get(fixture.Root));
        Assert.Throws<InvalidOperationException>(() => RepositorySourceSnapshotCache.Get(fixture.Root, new CountingObserver()));
    }

    private static SyntaxTree CallerA(string root, ISourceSnapshotObserver observer) =>
        RepositorySourceSnapshotCache.Get(root, observer).GetSyntaxTree(SourcePath);
    private static SyntaxTree CallerB(string root, ISourceSnapshotObserver observer)
    {
        var snapshot = RepositorySourceSnapshotCache.Get(root, observer);
        snapshot.GetText(SourcePath);
        return snapshot.GetSyntaxTree(SourcePath);
    }
    private static SyntaxTree CallerC(string root, ISourceSnapshotObserver observer)
    {
        var snapshot = RepositorySourceSnapshotCache.Get(root, observer);
        snapshot.GetLines(SourcePath);
        return snapshot.GetSyntaxTree(SourcePath);
    }
    private static SyntaxTree CallerD(string root, ISourceSnapshotObserver observer) =>
        RepositorySourceSnapshotCache.Get(root + Path.DirectorySeparatorChar, observer).GetSyntaxTree(SourcePath);
    private static SyntaxTree CallerE(string root, ISourceSnapshotObserver observer) =>
        RepositorySourceSnapshotCache.Get(root, observer).GetSyntaxTree(Path.Combine(root, SourcePath));

    private static string[] GuardFindings(SourceSnapshotFixture fixture) =>
        PowerShellLaunchSourceGuardTests.SourceFiles(fixture.TestProject)
            .SelectMany(path => PowerShellLaunchSourceGuardTests.FindLaunches(
                RepositorySourceSnapshotCache.Get(fixture.Root).GetSyntaxTree(path))
                .Select(finding => $"{Path.GetFileName(path)}:{finding.Line}")).ToArray();

    private static void AssertWork(CountingObserver observer, string root, string path)
    {
        Assert.Equal(1, observer.Count("enumerated", root));
        Assert.Equal(1, observer.Count("read", root, path));
        Assert.Equal(1, observer.Count("parsed", root, path));
    }

    private sealed class CountingObserver : ISourceSnapshotObserver
    {
        public readonly ConcurrentDictionary<string, int> Counts = new(StringComparer.Ordinal);
        public int Count(string action, string root, string path = "") => Counts.GetValueOrDefault(Key(action, root, path));
        public void OnEnumerated(string root) => Increment("enumerated", root, "");
        public void OnTextRead(string root, string relativePath) => Increment("read", root, relativePath);
        public void OnParsed(string root, string relativePath) => Increment("parsed", root, relativePath);
        private void Increment(string action, string root, string path) => Counts.AddOrUpdate(Key(action, root, path), 1, (_, count) => count + 1);
        private static string Key(string action, string root, string path) => $"{action}|{root}|{path}";
    }
}
