using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.CodeAnalysis.CSharp;

// Parallel-safe: fixtures and memos are private to each test; no Shared state is modified.
public sealed class FocusedEvidenceSiblingClassMemoTests
{
    [Xunit.Fact]
    public void UnchangedFingerprint_SecondCallHasNoReadsOrParses()
    {
        using var fixture = new Fixture();
        var first = fixture.Warm();
        Assert.True(fixture.Parses > 0);
        Assert.All(fixture.ReadsByPath.Values, count => Assert.Equal(1, count));

        var second = fixture.Resolve();

        Assert.Equal(first, second);
        Assert.Equal(0, fixture.Reads);
        Assert.Equal(0, fixture.Parses);
    }

    [Xunit.Fact]
    public void UnrelatedTimestampChange_RecomputesSameOrderedFamily()
    {
        using var fixture = new Fixture();
        var first = fixture.Warm();
        var path = fixture.PathFor("Unrelated01.cs");
        var originalTime = File.GetLastWriteTimeUtc(path);
        File.SetLastWriteTimeUtc(path, originalTime.AddMinutes(1));
        Assert.NotEqual(originalTime, File.GetLastWriteTimeUtc(path));

        var second = fixture.Resolve();

        Assert.True(fixture.Reads > 0);
        Assert.Equal(first, second);
    }

    [Xunit.Fact]
    public void NewDerivedFile_RecomputesAndIncludesNewClass()
    {
        using var fixture = new Fixture();
        fixture.Warm();
        fixture.Write("Third.cs", """
            public sealed class ThirdDerivedTests : FamilyTestBase { [Xunit.Fact] public void Runs() { } }
            """);

        var siblings = fixture.Resolve();

        Assert.True(fixture.Reads > 0);
        Assert.Equal(new[] { "AlphaDerivedTests", "ThirdDerivedTests", "ZetaDerivedTests" }, siblings);
    }

    [Xunit.Fact]
    public void ChangedLengthWithRestoredTimestamp_Recomputes()
    {
        using var fixture = new Fixture();
        var first = fixture.Warm();
        var path = fixture.PathFor("Unrelated01.cs");
        var originalTime = File.GetLastWriteTimeUtc(path);
        var originalLength = new FileInfo(path).Length;
        File.AppendAllText(path, " // changed length");
        File.SetLastWriteTimeUtc(path, originalTime);
        Assert.Equal(originalTime, File.GetLastWriteTimeUtc(path));
        Assert.NotEqual(originalLength, new FileInfo(path).Length);

        var second = fixture.Resolve();

        Assert.True(fixture.Reads > 0);
        Assert.Equal(first, second);
    }

    [Xunit.Fact]
    public void DifferentRequestedClass_ComputesItsOwnSiblings()
    {
        using var fixture = new Fixture();
        var family = fixture.Warm();

        var standalone = fixture.Resolve("StandaloneProbeTests");

        Assert.True(fixture.Reads > 0);
        Assert.Equal(new[] { "StandaloneProbeSiblingTests" }, standalone);
        Assert.False(family.SequenceEqual(standalone));
    }

    [Xunit.Theory]
    [Xunit.InlineData("obj")]
    [Xunit.InlineData("bin")]
    [Xunit.InlineData("nested/OBJ")]
    public void GeneratedFileChange_KeepsWarmEntry(string directory)
    {
        using var fixture = new Fixture();
        var first = fixture.Warm();
        fixture.Write(directory + "/Generated.cs", "public class Generated { }");

        var second = fixture.Resolve();

        Assert.Equal(first, second);
        Assert.Equal(0, fixture.Reads);
        Assert.Equal(0, fixture.Parses);
    }

    [Xunit.Fact]
    public void ProjectFileChange_Recomputes()
    {
        using var fixture = new Fixture();
        var first = fixture.Warm();
        File.AppendAllText(fixture.PathFor("Fixture.Tests.csproj"), "\n<!-- changed -->");

        Assert.Equal(first, fixture.Resolve());
        Assert.True(fixture.Reads > 0);
    }

    [Xunit.Fact]
    public void ReadFailure_ReturnsEmptyAndAllowsSuccessfulRetry()
    {
        using var fixture = new Fixture();

        Assert.Empty(fixture.Resolve(readText: _ => throw new IOException("read failed")));

        fixture.Warm();
        Assert.True(fixture.Parses > 0);
    }

    [Xunit.Fact]
    public void ComputeFailure_RethrowsAndDoesNotStore()
    {
        using var fixture = new Fixture();
        var memo = new FocusedEvidenceSiblingClassMemo(fingerprint: _ => "fixed");
        var calls = 0;
        IReadOnlyList<string> Compute()
        {
            if (++calls == 1)
            {
                throw new IOException("compute failed");
            }

            return new[] { "RecoveredTests" };
        }

        Assert.Throws<IOException>(() => memo.GetOrCompute(
            fixture.Root, Fixture.Project, "RequestedTests", fixture.ProjectDirectory, Compute));
        Assert.Equal(new[] { "RecoveredTests" }, memo.GetOrCompute(
            fixture.Root, Fixture.Project, "RequestedTests", fixture.ProjectDirectory, Compute));
        Assert.Equal(2, calls);
    }

    [Xunit.Fact]
    public void CapacityExceeded_ClearsAllEntries()
    {
        using var fixture = new Fixture();
        var memo = new FocusedEvidenceSiblingClassMemo(capacity: 2, fingerprint: _ => "fixed");
        var calls = 0;
        IReadOnlyList<string> Resolve(string name) => memo.GetOrCompute(
            fixture.Root, Fixture.Project, name, fixture.ProjectDirectory,
            () => { calls++; return new[] { name }; });

        Assert.Equal(new[] { "A" }, Resolve("A"));
        Resolve("B");
        Resolve("A");
        Assert.Equal(2, calls);
        Resolve("C");
        Assert.Equal(3, calls);
        Assert.Equal(new[] { "A" }, Resolve("A"));
        Assert.Equal(4, calls);
        Resolve("B");
        Assert.Equal(5, calls);
    }

    [Xunit.Theory]
    [Xunit.InlineData(0)]
    [Xunit.InlineData(-1)]
    public void NonpositiveCapacity_IsRejected(int capacity)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new FocusedEvidenceSiblingClassMemo(capacity));
    }

    [Xunit.Fact]
    public void NormalizedKey_ReusesAnImmutableSnapshot()
    {
        using var fixture = new Fixture();
        var memo = new FocusedEvidenceSiblingClassMemo(fingerprint: _ => "fixed");
        var mutable = new[] { "OriginalTests" };
        var first = memo.GetOrCompute(fixture.Root, Fixture.Project, " RequestedTests ",
            fixture.ProjectDirectory, () => mutable);
        mutable[0] = "ChangedTests";

        var second = memo.GetOrCompute(Path.Combine(fixture.Root, "."),
            Fixture.Project.Replace('/', '\\'), "RequestedTests", fixture.ProjectDirectory,
            () => throw new InvalidOperationException("normalized key missed"));

        Assert.Equal(new[] { "OriginalTests" }, first);
        Assert.Same(first, second);
        Assert.Throws<NotSupportedException>(() => ((IList<string>)second)[0] = "mutation");
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void MissingProject_BypassesFingerprintAndCanRetry(bool removeDirectory)
    {
        using var fixture = new Fixture();
        var fingerprints = 0;
        var memo = new FocusedEvidenceSiblingClassMemo(fingerprint: _ =>
        {
            fingerprints++;
            return "fixed";
        });
        if (removeDirectory)
        {
            Directory.Delete(fixture.ProjectDirectory, recursive: true);
        }
        else
        {
            File.Delete(fixture.PathFor("Fixture.Tests.csproj"));
        }

        var missing = FocusedEvidenceSiblingClassResolver.ResolveSiblingTestClassNames(
            fixture.Root, Fixture.Project, "RequestedTests", memo, File.ReadAllText,
            (path, text) => CSharpSyntaxTree.ParseText(text, path: path));

        Assert.Empty(missing);
        Assert.Equal(0, fingerprints);
        fixture.Write("Fixture.Tests.csproj", "<Project />");
        fixture.Write("Base.cs", """
            public class RequestedTests { [Xunit.Fact] public void Runs() { } }
            public class RestoredTests { [Xunit.Fact] public void Runs() { } }
            """);
        var restored = FocusedEvidenceSiblingClassResolver.ResolveSiblingTestClassNames(
            fixture.Root, Fixture.Project, "RequestedTests", memo, File.ReadAllText,
            (path, text) => CSharpSyntaxTree.ParseText(text, path: path));
        Assert.Contains("RestoredTests", restored);
        Assert.Equal(1, fingerprints);
    }

    private sealed class Fixture : IDisposable
    {
        internal const string Project = "tests/Fixture.Tests/Fixture.Tests.csproj";
        internal string Root { get; } = InfrastructureTestSupport.CreateTempDirectory();
        internal string ProjectDirectory => Path.Combine(Root, "tests", "Fixture.Tests");
        private readonly FocusedEvidenceSiblingClassMemo memo = new();
        internal int Reads;
        internal int Parses;
        internal Dictionary<string, int> ReadsByPath { get; } = new(StringComparer.Ordinal);

        internal Fixture()
        {
            Write("Fixture.Tests.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            Write("Base.cs", """
                namespace Fixture;
                public abstract class FamilyTestBase { }
                public sealed class RequestedTests : FamilyTestBase { [Xunit.Fact] public void Runs() { } }
                """);
            Write("Alpha.cs", "public class AlphaDerivedTests : FamilyTestBase { [Xunit.Fact] public void Runs() { } }");
            Write("Zeta.cs", "public class ZetaDerivedTests : FamilyTestBase { [Xunit.Fact] public void Runs() { } }");
            Write("Unrelated00.cs", """
                public class StandaloneProbeTests { [Xunit.Fact] public void Runs() { } }
                public class StandaloneProbeSiblingTests { [Xunit.Fact] public void Runs() { } }
                """);
            for (var index = 1; index < 30; index++)
            {
                Write($"Unrelated{index:00}.cs", $"public class Unrelated{index:00} {{ }}");
            }
        }

        internal string PathFor(string relative) => Path.Combine(
            ProjectDirectory, relative.Replace('/', Path.DirectorySeparatorChar));

        internal void Write(string relative, string text)
        {
            var path = PathFor(relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }

        internal IReadOnlyList<string> Warm()
        {
            var siblings = Resolve();
            Assert.Equal(new[] { "AlphaDerivedTests", "ZetaDerivedTests" }, siblings);
            Assert.True(Reads > 0);
            return siblings;
        }

        internal IReadOnlyList<string> Resolve(
            string requestedClass = "RequestedTests", Func<string, string>? readText = null)
        {
            Reads = Parses = 0;
            ReadsByPath.Clear();
            return FocusedEvidenceSiblingClassResolver.ResolveSiblingTestClassNames(
                Root, Project, requestedClass, memo,
                path =>
                {
                    Interlocked.Increment(ref Reads);
                    ReadsByPath[path] = ReadsByPath.GetValueOrDefault(path) + 1;
                    return (readText ?? File.ReadAllText)(path);
                },
                (path, text) =>
                {
                    Interlocked.Increment(ref Parses);
                    return CSharpSyntaxTree.ParseText(text, path: path);
                });
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
