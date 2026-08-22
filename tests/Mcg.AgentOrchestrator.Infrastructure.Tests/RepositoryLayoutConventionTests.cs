using System.Runtime.CompilerServices;

public sealed class RepositoryLayoutConventionTests
{
    [Fact]
    public void CurrentRepositoryHasNoUngrandfatheredViolations()
    {
        Assert.Equal(
            RepositoryLayoutConventions.SeededMultiPublicTypeFiles.Count,
            RepositoryLayoutConventions.SeededMultiPublicTypeFiles.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(
            RepositoryLayoutConventions.SeededDottedFileNames.Count,
            RepositoryLayoutConventions.SeededDottedFileNames.Distinct(StringComparer.Ordinal).Count());

        AssertNoViolations(RepositoryLayoutConventions.Evaluate(
            RepositoryRoot(),
            RepositoryLayoutConventions.SeededMultiPublicTypeFiles,
            RepositoryLayoutConventions.SeededDottedFileNames));
    }

    [Fact]
    public void NewMultiTypeFileIsRejectedWithEveryTypeName()
    {
        using var fixture = RepositoryFixture.Create();
        fixture.Write("tests/Synthetic/NewPair.cs", "public class FirstTest { } public sealed class SecondTest { }");

        var violation = Assert.Single(RepositoryLayoutConventions.Evaluate(fixture.Root, [], []));

        Assert.Equal("tests/Synthetic/NewPair.cs", violation.RelativePath);
        Assert.Contains("FirstTest", violation.Message, StringComparison.Ordinal);
        Assert.Contains("SecondTest", violation.Message, StringComparison.Ordinal);
        Assert.Contains("Move each public type to its own dot-free file", violation.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NewDottedFileIsRejectedWithDerivedFilterAndDeclaredType()
    {
        using var fixture = RepositoryFixture.Create();
        fixture.Write("tests/Synthetic/Parent.Suffix.cs", "public sealed class ParentSuffix { }");

        var violation = Assert.Single(RepositoryLayoutConventions.Evaluate(fixture.Root, [], []));

        Assert.Equal("tests/Synthetic/Parent.Suffix.cs", violation.RelativePath);
        Assert.Contains("FullyQualifiedName~Parent.Suffix", violation.Message, StringComparison.Ordinal);
        Assert.Contains("ParentSuffix", violation.Message, StringComparison.Ordinal);
        Assert.Contains("dot-free stem", violation.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RemovingActiveEntryFailsAndRemovingCompliantEntryPasses()
    {
        using var fixture = RepositoryFixture.Create();
        const string relativePath = "tests/Synthetic/LegacyPair.cs";
        fixture.Write(relativePath, "public class FirstTest { } public sealed class SecondTest { }");

        AssertNoViolations(RepositoryLayoutConventions.Evaluate(fixture.Root, [relativePath], []));
        Assert.Contains(
            RepositoryLayoutConventions.Evaluate(fixture.Root, [], []),
            violation => violation.Rule == "multiple-top-level-public-types");

        fixture.Write(relativePath, "public sealed class LegacyPair { }");
        var staleViolation = Assert.Single(RepositoryLayoutConventions.Evaluate(fixture.Root, [relativePath], []));
        Assert.Contains("inventory may only shrink", staleViolation.Message, StringComparison.Ordinal);
        AssertNoViolations(RepositoryLayoutConventions.Evaluate(fixture.Root, [], []));
    }

    [Fact]
    public void MatchingSingleClassSplitPassesWithoutInventoryEntry()
    {
        using var fixture = RepositoryFixture.Create();
        fixture.Write("tests/Synthetic/ExtractedBehaviorTests.cs", "public sealed class ExtractedBehaviorTests { }");

        AssertNoViolations(RepositoryLayoutConventions.Evaluate(fixture.Root, [], []));
    }

    [Fact]
    public void NestedPrivateHelpersDoNotCountAsTopLevelPublicTypes()
    {
        using var fixture = RepositoryFixture.Create();
        fixture.Write(
            "tests/Synthetic/OuterTests.cs",
            "public sealed class OuterTests { private sealed class Helper { } }");

        AssertNoViolations(RepositoryLayoutConventions.Evaluate(fixture.Root, [], []));
    }

    [Fact]
    public void GeneratedAndArtifactTreesAreNotEnforced()
    {
        using var fixture = RepositoryFixture.Create();
        fixture.Write("tests/Synthetic/Generated.Pair.g.cs", "public class First { } public class Second { }");
        fixture.Write("tests/Synthetic/obj/Generated.Pair.cs", "public class Third { } public class Fourth { }");
        fixture.Write("tests/Synthetic/artifacts/Artifact.Pair.cs", "public class Fifth { } public class Sixth { }");

        AssertNoViolations(RepositoryLayoutConventions.Evaluate(fixture.Root, [], []));
    }

    private static void AssertNoViolations(IReadOnlyList<RepositoryLayoutViolation> violations)
    {
        Assert.True(
            violations.Count == 0,
            "Repository layout convention violations:" + Environment.NewLine +
            string.Join(Environment.NewLine, violations.Select(violation => violation.Message)));
    }

    private static string RepositoryRoot([CallerFilePath] string sourcePath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "..", ".."));

    private sealed class RepositoryFixture : IDisposable
    {
        private RepositoryFixture(string root)
        {
            Root = root;
        }

        public string Root { get; }

        public static RepositoryFixture Create()
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                nameof(RepositoryLayoutConventionTests),
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "tests"));
            return new RepositoryFixture(root);
        }

        public void Write(string relativePath, string source)
        {
            var path = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, source);
        }

        public void Dispose()
        {
            Directory.Delete(Root, recursive: true);
        }
    }
}
