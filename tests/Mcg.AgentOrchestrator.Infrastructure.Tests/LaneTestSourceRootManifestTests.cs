using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: temporary trees and cache scopes are private; the repository test only reads source.
public sealed class LaneTestSourceRootManifestTests
{
    [Fact]
    public void DeclaredRootOutsideMcgNames_ScannerAndInventoryReturnClassWithCollection()
    {
        using var tree = new TestTree(Manifest("suites/LaneProbe"));
        tree.Write("suites/LaneProbe/Names.cs", """
            internal static class ProbeNames { internal const string Lane = "ProbeLane"; }
            [Xunit.CollectionDefinition(ProbeNames.Lane, DisableParallelization = true)]
            public class ProbeDefinition { }
            """);
        tree.Write("suites/LaneProbe/ProbeLaneTests.cs", """
            [Xunit.Collection(ProbeNames.Lane)]
            public class ProbeLaneTests { [Xunit.Fact] public void Runs() { } }
            """);
        tree.Write("suites/LaneProbe/bin/IgnoredTests.cs", Consumer("BinTests"));
        tree.Write("suites/LaneProbe/obj/IgnoredTests.cs", Consumer("ObjTests"));
        tree.Write("elsewhere/OutsideTests.cs", Consumer("OutsideTests"));

        var descriptor = Assert.Single(AcceptanceTestClassSourceScanner.Scan(tree.Root));
        Assert.Equal("ProbeLaneTests", descriptor.FullName);
        Assert.Equal("ProbeLane", descriptor.Collection);
        var source = Assert.Single(AcceptanceTestClassSourceScanner.ScanSources(tree.Root));
        Assert.Equal(descriptor.FullName, source.FullName);
        Assert.Equal(["suites/LaneProbe/ProbeLaneTests.cs"], source.SourcePaths);

        var calls = new List<string[]>();
        var inventory = AcceptanceTestInventorySource.Read(tree.Root, "trunk", (_, args) =>
        {
            calls.Add(args);
            Assert.Equal("grep", args[0]);
            return "";
        });

        Assert.Equal(descriptor, Assert.Single(inventory.Classes));
        Assert.Equal("ProbeLane", Assert.Single(inventory.DisabledCollections));
        var grep = Assert.Single(calls);
        Assert.Equal([":(literal)suites/LaneProbe"], grep.Skip(Array.IndexOf(grep, "--") + 1));
    }

    [Theory]
    [InlineData("{\"engine\":{}}")]
    [InlineData("{\"engine\":{\"laneTestSourceRoots\":[]}}")]
    [InlineData(null)]
    public void UndeclaredRoots_ScannerEmptyAndInventoryThrows(string? manifest)
    {
        using var tree = new TestTree(manifest);
        tree.Write("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/SampleTests.cs", Consumer("SampleTests"));
        tree.Write("tests/Mcg.AgentOrchestrator.TestSupport/SupportTests.cs", Consumer("SupportTests"));

        Assert.Empty(AcceptanceTestClassSourceScanner.Scan(tree.Root));
        Assert.Empty(AcceptanceTestClassSourceScanner.ScanSources(tree.Root));
        var error = Assert.Throws<InvalidDataException>(() => AcceptanceTestInventorySource.Read(
            tree.Root, "trunk", (_, _) => throw new InvalidOperationException("Git must not run without declared roots.")));
        Assert.Contains("laneTestSourceRoots", error.Message, StringComparison.Ordinal);
        Assert.Contains("manifest declares no lane test source roots", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("C:/tests/x")]
    [InlineData("C:tests/x")]
    [InlineData("/tests/x")]
    [InlineData("//server/share")]
    [InlineData("\\\\server\\share")]
    [InlineData("\\tests\\x")]
    [InlineData("tests/../x")]
    [InlineData("tests\\..\\x")]
    [InlineData("tests/./x")]
    [InlineData("tests//x")]
    [InlineData("tests/x//")]
    [InlineData(".")]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(null)]
    public void InvalidRootEntry_ManifestParseRejects(string? entry)
    {
        var json = JsonSerializer.Serialize(new { engine = new { laneTestSourceRoots = new[] { entry } } });

        var error = Assert.Throws<InvalidDataException>(() => AcceptanceGateEngineSettings.Parse(json));

        Assert.Contains("laneTestSourceRoots", error.Message, StringComparison.Ordinal);
        Assert.Contains($"'{entry ?? "null"}'", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("tests/Mcg.AgentOrchestrator.TestSupport", "tests/Mcg.AgentOrchestrator.TestSupport")]
    [InlineData("tests\\Mcg.AgentOrchestrator.TestSupport\\", "tests/Mcg.AgentOrchestrator.TestSupport")]
    [InlineData("suites/has..dots/", "suites/has..dots")]
    public void ValidRootEntry_ManifestParseAccepts(string entry, string normalized)
    {
        using var tree = new TestTree(Manifest(entry));

        var settings = AcceptanceGateEngineSettings.Parse(Manifest(entry));

        Assert.Equal(entry, Assert.Single(settings.LaneTestSourceRoots));
        Assert.Equal(normalized, Assert.Single(LaneTestSourceRootResolver.Resolve(tree.Root)));
    }

    [Fact]
    public void NullRootList_ManifestParse_RejectsInvalidDeclaration()
    {
        var error = Assert.Throws<InvalidDataException>(() =>
            AcceptanceGateEngineSettings.Parse("""{"engine":{"laneTestSourceRoots":null}}"""));

        Assert.Contains("laneTestSourceRoots", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\"suites\"")]
    [InlineData("{}")]
    [InlineData("[42]")]
    public void WrongRootListShape_ManifestParse_PropagatesJsonError(string value)
    {
        Assert.Throws<JsonException>(() => AcceptanceGateEngineSettings.Parse(
            "{\"engine\":{\"laneTestSourceRoots\":" + value + "}}"));
    }

    [Fact]
    public void OverlappingRoots_Discovery_DeduplicatesAndOrdersFilesGlobally()
    {
        using var tree = new TestTree(Manifest("suites/nested", "suites\\", "suites"));
        tree.Write("suites/ZTests.cs", Consumer("ZTests"));
        tree.Write("suites/nested/ATests.cs", Consumer("ATests"));
        var expected = new[] { tree.PathOf("suites/ZTests.cs"), tree.PathOf("suites/nested/ATests.cs") }
            .Order(StringComparer.Ordinal).ToArray();
        var cache = new AcceptanceTestClassSourceCache();
        using var scope = AcceptanceTestClassSourceCache.Use(cache);

        Assert.Equal(expected, LaneTestSourceRootResolver.SourceFiles(tree.Root, LaneTestSourceRootResolver.Resolve(tree.Root)));
        Assert.Equal(["ATests", "ZTests"], AcceptanceTestClassSourceScanner.Scan(tree.Root).Select(item => item.FullName));
        Assert.Equal(2, cache.ParsedFileCount);

        tree.Write("config/acceptance-manifest.json", Manifest("suites", "suites/nested"));

        Assert.Equal(expected, LaneTestSourceRootResolver.SourceFiles(tree.Root, LaneTestSourceRootResolver.Resolve(tree.Root)));
        Assert.Equal(["ATests", "ZTests"], AcceptanceTestClassSourceScanner.ScanSources(tree.Root).Select(item => item.FullName));
        Assert.Equal(2, cache.ParsedFileCount);
    }

    [Fact]
    public void CrossRootConstants_CollectionResolution_UsesCombinedSourceSet()
    {
        using var tree = new TestTree(Manifest("suites/tests", "support"));
        tree.Write("support/Names.cs", """
            internal static class SharedNames { internal const string Lane = "SharedLane"; }
            [Xunit.CollectionDefinition(SharedNames.Lane, DisableParallelization = true)]
            public class SharedDefinition { }
            """);
        tree.Write("suites/tests/SharedTests.cs", """
            [Xunit.Collection(SharedNames.Lane)]
            public class SharedTests { [Xunit.Fact] public void Runs() { } }
            """);

        Assert.Equal("SharedLane", Assert.Single(AcceptanceTestClassSourceScanner.Scan(tree.Root)).Collection);
        var inventory = AcceptanceTestInventorySource.Read(tree.Root, "trunk", (_, _) => "");
        Assert.Equal("SharedLane", Assert.Single(inventory.Classes).Collection);
        Assert.Equal("SharedLane", Assert.Single(inventory.DisabledCollections));
    }

    [Fact]
    public void ChangedManifest_NextOperation_ReloadsRootsAndClearsObsoleteCache()
    {
        using var tree = new TestTree(Manifest("first"));
        tree.Write("first/FirstTests.cs", Consumer("FirstTests"));
        tree.Write("second/SecondTests.cs", Consumer("SecondTests"));
        var cache = new AcceptanceTestClassSourceCache();
        using var scope = AcceptanceTestClassSourceCache.Use(cache);
        Assert.Equal("FirstTests", Assert.Single(AcceptanceTestClassSourceScanner.Scan(tree.Root)).FullName);

        tree.Write("config/acceptance-manifest.json", Manifest("second"));

        Assert.Equal("SecondTests", Assert.Single(AcceptanceTestClassSourceScanner.ScanSources(tree.Root)).FullName);
        Assert.Equal("SecondTests", Assert.Single(AcceptanceTestInventorySource.Read(tree.Root, "trunk", (_, _) => "").Classes).FullName);
        Assert.Equal(tree.PathOf("second/SecondTests.cs"), Assert.Single(cache.CachedPathsForTests(tree.Root)));

        tree.Write("config/acceptance-manifest.json", Manifest());

        Assert.Empty(AcceptanceTestClassSourceScanner.Scan(tree.Root));
        Assert.Empty(cache.CachedPathsForTests(tree.Root));
    }

    [Fact]
    public void MissingDeclaredDirectory_Inventory_ReportsEmptyRunnableInventory()
    {
        using var tree = new TestTree(Manifest("missing"));

        Assert.Empty(AcceptanceTestClassSourceScanner.Scan(tree.Root));
        var error = Assert.Throws<InvalidDataException>(() => AcceptanceTestInventorySource.Read(
            tree.Root, "trunk", (_, _) => throw new InvalidOperationException("Git must not run for empty inventory.")));
        Assert.Equal("Runnable test inventory is empty.", error.Message);
    }

    [Fact]
    public void MalformedManifest_AllDiscoveryOperations_PropagateParseErrors()
    {
        using var tree = new TestTree("{invalid json");

        Assert.ThrowsAny<JsonException>(() => AcceptanceTestClassSourceScanner.Scan(tree.Root));
        Assert.ThrowsAny<JsonException>(() => AcceptanceTestClassSourceScanner.ScanSources(tree.Root));
        Assert.ThrowsAny<JsonException>(() => AcceptanceTestInventorySource.Read(tree.Root, "trunk", (_, _) => ""));
    }

    [Fact]
    public void RepositoryManifest_ResolvesInfrastructureAndSupportSourcesButNotCoreTests()
    {
        var repository = VerifiedRepositoryRoot.Find();
        var roots = LaneTestSourceRootResolver.Resolve(repository);
        var paths = LaneTestSourceRootResolver.SourceFiles(repository, roots)
            .Select(path => Path.GetRelativePath(repository, path).Replace('\\', '/')).ToArray();

        Assert.Contains(paths, path => path.StartsWith("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/", StringComparison.Ordinal));
        Assert.Contains("tests/Mcg.AgentOrchestrator.TestSupport/TestCollections.cs", paths);
        Assert.DoesNotContain(paths, path => path.StartsWith("tests/Mcg.AgentOrchestrator.Core.Tests/", StringComparison.Ordinal));
        var classes = AcceptanceTestClassSourceScanner.Scan(repository);
        Assert.NotEmpty(classes);
        Assert.Contains(classes, item => item.Collection == TestCollections.DotnetBuildSlots);
        var sources = AcceptanceTestClassSourceScanner.ScanSources(repository);
        Assert.Equal(classes.Select(item => item.FullName), sources.Select(item => item.FullName));
        Assert.DoesNotContain(sources.SelectMany(item => item.SourcePaths),
            path => path.StartsWith("tests/Mcg.AgentOrchestrator.Core.Tests/", StringComparison.Ordinal));
    }

    private static string Manifest(params string[] roots) =>
        JsonSerializer.Serialize(new { engine = new { laneTestSourceRoots = roots } });

    private static string Consumer(string name) =>
        $"public class {name} {{ [Xunit.Fact] public void Runs() {{ }} }}";

    private sealed class TestTree : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "lane-source-roots-" + Guid.NewGuid().ToString("N"));

        internal TestTree(string? manifest)
        {
            Directory.CreateDirectory(Root);
            if (manifest is not null) Write("config/acceptance-manifest.json", manifest);
        }

        internal string PathOf(string relativePath) => Path.GetFullPath(Path.Combine(Root, relativePath));

        internal void Write(string relativePath, string source)
        {
            var path = PathOf(relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, source);
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
