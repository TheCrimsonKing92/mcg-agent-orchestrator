using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.CodeAnalysis.CSharp;

// Parallel-safe: temporary roots are private; the real-repository test only reads its shared snapshot.
public sealed class FocusedEvidenceSiblingFamilyPrefilterTests
{
    [Xunit.Fact]
    public void TransitiveFamily_MatchesExhaustiveWithoutParsingUnrelatedFiles()
    {
        using var fixture = new Fixture();
        fixture.Write("Leaf.cs", "public class LeafTests : MidTestBase { [Xunit.Fact] public void Runs() { } }");
        fixture.Write("Mid.cs", "public abstract class MidTestBase : FamilyTestBase { }");
        fixture.Write("DupA.cs", "public class DuplicateFamilyTests : FamilyTestBase { [Xunit.Fact] public void Runs() { } }");
        fixture.Write("DupB.cs", "public class DuplicateFamilyTests : FamilyTestBase { [Xunit.Fact] public void AlsoRuns() { } }");
        var brokenPath = fixture.Write("Broken.cs", """
            public class BrokenFamilyTests : FamilyTestBase { [Xunit.Fact] public void Runs( { } }
            """);
        var unrelatedPaths = Enumerable.Range(0, 40)
            .Select(index => fixture.Write($"Unrelated{index:00}.cs", $"public class Unrelated{index:00} {{ }}"))
            .ToArray();
        var reads = new Dictionary<string, int>(StringComparer.Ordinal);
        var parses = new Dictionary<string, int>(StringComparer.Ordinal);
        var referenceParses = new Dictionary<string, int>(StringComparer.Ordinal);

        var actual = fixture.Resolve(reads, parses);
        var reference = fixture.Resolve(new(StringComparer.Ordinal), referenceParses, parseEveryFamilyFile: true);

        Assert.Equal(new[] { "DuplicateFamilyTests", "LeafTests" }, actual);
        Assert.Equal(reference, actual);
        Assert.All(reads.Values, count => Assert.Equal(1, count));
        Assert.All(parses.Values, count => Assert.Equal(1, count));
        Assert.Equal(1, parses[brokenPath]);
        Assert.All(unrelatedPaths, path =>
        {
            Assert.False(parses.ContainsKey(path), $"Unrelated file parsed: {path}");
            Assert.Equal(1, referenceParses[path]);
        });
    }

    [Xunit.Fact]
    public void FirstPassAndDeclaringRoots_PropagateNamesUntilStable()
    {
        using var fixture = new Fixture();
        fixture.Write("Base.cs", """
            namespace Fixture;
            public abstract class FamilyTestBase { }
            public abstract class LateBase : MidTestBase { }
            public class RequestedTests : FamilyTestBase { [Xunit.Fact] public void Runs() { } }
            """);
        fixture.Write("Leaf.cs", "public class LeafTests : LateBase { [Xunit.Fact] public void Runs() { } }");
        var midPath = fixture.Write("Mid.cs", """
            // RequestedTests makes this a first-pass root.
            public abstract class MidTestBase : FamilyTestBase { }
            """);
        var parses = new Dictionary<string, int>(StringComparer.Ordinal);

        var actual = fixture.Resolve(new(StringComparer.Ordinal), parses);
        var reference = fixture.Resolve(new(StringComparer.Ordinal), new(StringComparer.Ordinal), true);

        Assert.Equal(new[] { "LeafTests" }, actual);
        Assert.Equal(reference, actual);
        Assert.Equal(1, parses[midPath]);
    }

    [Xunit.Fact]
    public void DuplicateIntermediateName_PreservesExhaustiveAmbiguity()
    {
        using var fixture = new Fixture();
        fixture.Write("MidA.cs", "public abstract class MidTestBase : FamilyTestBase { }");
        fixture.Write("MidB.cs", "public abstract class MidTestBase { }");
        fixture.Write("Leaf.cs", "public class LeafTests : MidTestBase { [Xunit.Fact] public void Runs() { } }");
        fixture.Write("Direct.cs", "public class DirectTests : FamilyTestBase { [Xunit.Fact] public void Runs() { } }");

        var actual = fixture.Resolve(new(StringComparer.Ordinal), new(StringComparer.Ordinal));
        var reference = fixture.Resolve(new(StringComparer.Ordinal), new(StringComparer.Ordinal), true);

        Assert.Equal(new[] { "DirectTests" }, actual);
        Assert.Equal(reference, actual);
    }

    [Xunit.Theory]
    [Xunit.InlineData("GoalWorktreeReducedFixtureTests")]
    [Xunit.InlineData("GoalWorktreeTestsAcceptanceRetry")]
    [Xunit.InlineData("GoalWorktreeIsolatedDotnetTests")]
    [Xunit.InlineData("GoalWorktreeAcceptanceContentionTests")]
    [Xunit.InlineData("GoalAcceptanceVerifierTests")]
    [Xunit.InlineData("ConductorDriverTests")]
    public void RealRepository_PrefilterPreservesOrderedSelections(string requestedClass)
    {
        var root = InfrastructureTestSupport.FindRepositoryRoot();
        var snapshot = RepositorySourceSnapshotCache.Get(root);
        const string project = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj";
        var reads = 0;
        var parses = 0;
        IReadOnlyList<string> Resolve(bool parseEveryFamilyFile) =>
            FocusedEvidenceSiblingClassResolver.ResolveSiblingTestClassNames(
                root, project, requestedClass,
                path =>
                {
                    Interlocked.Increment(ref reads);
                    return snapshot.GetText(path);
                },
                (path, _) =>
                {
                    Interlocked.Increment(ref parses);
                    return snapshot.GetSyntaxTree(path);
                }, parseEveryFamilyFile);

        var actual = Resolve(false);
        var defaultReads = reads;
        var defaultParses = parses;
        reads = parses = 0;
        var reference = Resolve(true);

        Assert.True(defaultReads > 0);
        Assert.True(defaultParses > 0);
        Assert.True(parses > 0);
        if (requestedClass == "ConductorDriverTests")
        {
            Assert.Empty(reference);
        }
        else
        {
            Assert.NotEmpty(reference);
        }
        Assert.Equal(reference, actual);
        Assert.Equal(defaultReads, reads);
        if (requestedClass == "GoalWorktreeReducedFixtureTests")
        {
            Assert.Contains("AcceptanceCohortWorkflowTestsMergeTrain", actual);
            Assert.True(defaultParses * 2 < parses,
                $"Prefilter parsed {defaultParses} files; exhaustive parsed {parses}.");
        }
    }

    private sealed class Fixture : IDisposable
    {
        private const string Project = "tests/Fixture.Tests/Fixture.Tests.csproj";
        private string Root { get; } = InfrastructureTestSupport.CreateTempDirectory();

        internal Fixture()
        {
            Write("Fixture.Tests.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            Write("Base.cs", """
                namespace Fixture;
                public abstract class FamilyTestBase { }
                public class RequestedTests : FamilyTestBase { [Xunit.Fact] public void Runs() { } }
                """);
        }

        internal string Write(string relative, string text)
        {
            var path = Path.Combine(Root, "tests", "Fixture.Tests", relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
            return path;
        }

        internal IReadOnlyList<string> Resolve(
            Dictionary<string, int> reads, Dictionary<string, int> parses, bool parseEveryFamilyFile = false) =>
            FocusedEvidenceSiblingClassResolver.ResolveSiblingTestClassNames(
                Root, Project, "RequestedTests",
                path =>
                {
                    reads[path] = reads.GetValueOrDefault(path) + 1;
                    return File.ReadAllText(path);
                },
                (path, text) =>
                {
                    parses[path] = parses.GetValueOrDefault(path) + 1;
                    return CSharpSyntaxTree.ParseText(text, path: path);
                }, parseEveryFamilyFile);

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
