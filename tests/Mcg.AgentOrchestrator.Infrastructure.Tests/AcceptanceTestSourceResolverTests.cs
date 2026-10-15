using Mcg.AgentOrchestrator.Infrastructure;

// Virtual filesystem per fixture; no shared process, environment, or repository state.
public sealed class AcceptanceTestSourceResolverTests
{
    [Fact]
    public void ThousandsOfDistinctAndRepeatedIdentitiesReadEachSourceOnce()
    {
        var files = new SourceFiles();
        files.Add("tests/A/Many.cs", string.Join('\n', Enumerable.Range(0, 3000).Select(i => $"class Test{i} {{}}")));
        files.Add("tests/A/Partial.cs", "partial class Test0 {} class Test2999 {}");
        files.Add("tests/A/bin/Generated.cs", "class Test0 {}");
        files.Add("tests/A/OBJ/Generated.cs", "class Test0 {}");
        var request = files.Request();
        for (var repeat = 0; repeat < 2; repeat++)
        for (var i = 0; i < 3000; i++)
        {
            var expected = i is 0 or 2999 ? new[] { "tests/A/Many.cs", "tests/A/Partial.cs" } : ["tests/A/Many.cs"];
            Assert.Equal(expected, request.ResolveSourcePaths("tests/A/A.csproj", $"N.Test{i}.Fact[cleanup ({i})]"));
        }
        Assert.Equal(1, request.EnumerationCount);
        Assert.Equal(2, request.ReadCount);
        Assert.Equal(2, files.Reads.Count);
        Assert.All(files.Reads.Values, count => Assert.Equal(1, count));
    }

    [Fact]
    public void RoslynAndLiteralTextSemanticsPreserveEveryOrderedMatch()
    {
        var files = new SourceFiles();
        files.Add("tests/A/Z.cs", "namespace First { partial class Shared {} class Outer { class Nested {} } } // Fake");
        files.Add("tests/A/A.cs", "namespace Second; partial class Shared<T> {} interface Contract {} record Record {} struct Value {}");
        files.Add("tests/A/Escaped.cs", @"class \u0053hared {} class @Keyword {} // Shared");
        files.Add("tests/A/ExcludedEscape.cs", @"class \u0048idden {}");
        var request = files.Request();
        Assert.Equal(new[] { "tests/A/A.cs", "tests/A/Escaped.cs", "tests/A/Z.cs" },
            request.ResolveSourcePaths("tests/A/A.csproj", "Namespace.Shared<int>.Fact(x: 1)"));
        Assert.Equal(new[] { "tests/A/Z.cs" }, request.ResolveSourcePaths(null, "Outer.Nested.Fact"));
        Assert.Equal(new[] { "tests/A/A.cs" }, request.ResolveSourcePaths(null, "N.Contract.Fact"));
        Assert.Equal(new[] { "tests/A/A.cs" }, request.ResolveSourcePaths(null, "N.Record.Fact"));
        Assert.Equal(new[] { "tests/A/A.cs" }, request.ResolveSourcePaths(null, "N.Value.Fact"));
        Assert.Equal(new[] { "tests/A/Escaped.cs" }, request.ResolveSourcePaths(null, "N.Keyword.Fact"));
        Assert.Empty(request.ResolveSourcePaths(null, "N.Hidden.Fact"));
        Assert.Empty(request.ResolveSourcePaths(null, "N.Fake.Fact"));
        Assert.Equal(4, request.ReadCount); // Overlapping project and fallback scopes reuse file reads.
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("unknown test")]
    [InlineData(".")]
    public void MissingAndMalformedIdentitiesDoNotEnumerate(string? identity)
    {
        var files = new SourceFiles();
        var request = files.Request();
        Assert.Empty(request.ResolveSourcePaths(null, identity));
        Assert.Equal(0, request.EnumerationCount);
        Assert.Equal(0, request.ReadCount);
        Assert.Empty(new AcceptanceTestSourceResolver.Request(null).ResolveSourcePaths(null, "N.Test.Fact"));
    }

    [Fact]
    public void ProjectsAndRootsStayIsolatedAndNextRequestReadsChangedCandidate()
    {
        var files = new SourceFiles();
        files.Add("tests/A/Tests.cs", "class Shared {}");
        files.Add("tests/B/Tests.cs", "class Shared {}");
        files.Add("src/Production.cs", "class Shared {}");
        files.Add("outside/Tests.cs", "class Shared {}");
        var request = files.Request();
        Assert.Equal(new[] { "tests/A/Tests.cs" }, request.ResolveSourcePaths("tests/A/A.csproj", "N.Shared.Fact"));
        Assert.Equal(new[] { "tests/B/Tests.cs" }, request.ResolveSourcePaths("tests/B/B.csproj", "N.Shared.Fact"));
        Assert.Equal(new[] { "src/Production.cs", "tests/A/Tests.cs", "tests/B/Tests.cs" },
            request.ResolveSourcePaths("../outside/Outside.csproj", "N.Shared.Fact"));
        Assert.Equal(3, request.ReadCount);
        files.Add("tests/A/Tests.cs", "class Replacement {}");
        Assert.NotEmpty(request.ResolveSourcePaths("tests/A/A.csproj", "N.Shared.Fact"));
        var next = files.Request();
        Assert.Empty(next.ResolveSourcePaths("tests/A/A.csproj", "N.Shared.Fact"));
        Assert.NotEmpty(next.ResolveSourcePaths("tests/A/A.csproj", "N.Replacement.Fact"));
        Assert.Equal(1, next.ReadCount);
        var otherRoot = new SourceFiles();
        otherRoot.Add("tests/A/Tests.cs", "class Other {}");
        var other = otherRoot.Request();
        Assert.Empty(other.ResolveSourcePaths("tests/A/A.csproj", "N.Shared.Fact"));
        Assert.NotEmpty(other.ResolveSourcePaths("tests/A/A.csproj", "N.Other.Fact"));
        Assert.Equal(1, other.ReadCount);
    }

    [Fact]
    public void UnreadableFilesAreMemoizedWhileReadableSiblingsRemainEligible()
    {
        var files = new SourceFiles();
        files.Add("tests/A/Denied.cs", "class Shared {}");
        files.Add("tests/A/Good.cs", "class Shared {}");
        var request = files.Request(read: path => path.EndsWith("Denied.cs", StringComparison.Ordinal)
            ? throw new UnauthorizedAccessException() : files.Read(path));
        for (var i = 0; i < 1000; i++)
            Assert.Equal(new[] { "tests/A/Good.cs" }, request.ResolveSourcePaths("tests/A/A.csproj", "N.Shared.Fact"));
        Assert.Equal(2, request.ReadCount);
        Assert.Equal(1, request.EnumerationCount);
    }

    [Fact]
    public void LateEnumerationFailureNeverPublishesPartialMatchesOrRetries()
    {
        var files = new SourceFiles();
        files.Add("tests/A/Good.cs", "class Shared {}");
        var request = files.Request(enumerate: _ => FailAfter(files.FullPath("tests/A/Good.cs")));
        for (var i = 0; i < 1000; i++) Assert.Empty(request.ResolveSourcePaths("tests/A/A.csproj", "N.Shared.Fact"));
        Assert.Equal(1, request.EnumerationCount);
        Assert.Equal(1, request.ReadCount);
    }

    private static IEnumerable<string> FailAfter(string path)
    {
        yield return path;
        throw new IOException("enumeration interrupted");
    }

    [Fact]
    public void FailureInSecondFallbackRootInvalidatesEarlierMatches()
    {
        var files = new SourceFiles();
        files.Add("tests/A/Good.cs", "class Shared {}");
        files.Add("src/Good.cs", "class Shared {}");
        var request = files.Request(enumerate: directory => directory.EndsWith("src", StringComparison.Ordinal)
            ? FailAfter(files.FullPath("src/Good.cs")) : [files.FullPath("tests/A/Good.cs")]);
        Assert.Empty(request.ResolveSourcePaths(null, "N.Shared.Fact"));
        Assert.Empty(request.ResolveSourcePaths(null, "N.Shared.Fact"));
        Assert.Equal(2, request.EnumerationCount);
        Assert.Equal(2, request.ReadCount);
    }

    private sealed class SourceFiles
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "source-index-" + Guid.NewGuid().ToString("N"));
        private readonly Dictionary<string, string> _files = new(StringComparer.OrdinalIgnoreCase);
        internal Dictionary<string, int> Reads { get; } = new(StringComparer.OrdinalIgnoreCase);
        internal string FullPath(string relative) => Path.GetFullPath(Path.Combine(_root, relative));
        internal void Add(string relative, string source) => _files[FullPath(relative)] = source;
        internal string Read(string path)
        {
            Reads[path] = Reads.GetValueOrDefault(path) + 1;
            return _files[path];
        }
        internal AcceptanceTestSourceResolver.Request Request(
            Func<string, IEnumerable<string>>? enumerate = null, Func<string, string>? read = null) => new(_root,
                enumerate ?? (directory => _files.Keys.Where(path => path.StartsWith(
                    Path.TrimEndingDirectorySeparator(directory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))),
                read ?? Read,
                directory => directory == _root || _files.Keys.Any(path => path.StartsWith(
                    Path.TrimEndingDirectorySeparator(directory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)));
    }
}
