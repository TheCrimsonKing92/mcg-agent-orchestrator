using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using AcceptanceManifestCheck = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;

public sealed class AcceptanceLaneReuseShadowClassifierTests
{
    private const string Source = "src/Fixture/Widget.cs";
    private const string TestFile = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/PlainTests.cs";
    private static readonly ReverseDependencyTestImpactLookupResult Resolved = new(true, [], null, null);

    [Theory]
    [InlineData("ProcessStartInfo", "process-spawning")]
    [InlineData("Process.Start(", "process-spawning")]
    [InlineData("pwsh", "process-spawning")]
    [InlineData("powershell", "process-spawning")]
    [InlineData(".exe\"", "built-binary")]
    [InlineData(".dll\"", "built-binary")]
    [InlineData("AppContext.BaseDirectory", "built-binary")]
    [InlineData("VerifiedRepositoryRoot", "source-text-guard")]
    [InlineData("FindRepositoryRoot", "source-text-guard")]
    [InlineData("CallerFilePath", "source-text-guard")]
    [InlineData("acceptance-manifest.json", "config-driven")]
    [InlineData("\"config\"", "config-driven")]
    [InlineData("\"config/", "config-driven")]
    public void MarkerTableRequiresExecution(string text, string category)
    {
        var result = Classify([Source], [new("PlainTests", [TestFile], text)]);
        AssertDecision(result, "must-run", $"always-affected:{category}:PlainTests");
    }

    [Theory]
    [InlineData("xunit:ProcessSpawning")]
    [InlineData("xunit:ProcessSpawningProcessLocal")]
    [InlineData("xunit:DotnetBuildSlots")]
    public void ProcessResourceKeysRequireExecution(string key)
    {
        var result = AcceptanceLaneReuseShadowClassifier.Classify([Source], [Lane(keys: [key])], [], Resolved);
        AssertDecision(result, "must-run", $"always-affected:process-spawning:{key}");
    }

    [Theory]
    [InlineData("PlainTests")]
    [InlineData("DerivedTests")]
    public void ChangedDeclaringOrAncestorFileRequiresExecution(string className)
    {
        var source = new AcceptanceTestClassSource(className, [TestFile, "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/DerivedTests.cs"], "");
        var result = Classify([TestFile], [source]);
        AssertDecision(result, "must-run", $"changed-test-class:{className}");
    }

    [Theory]
    [InlineData("Unavailable")]
    [InlineData("Unreadable")]
    [InlineData("IndexedSourceBound")]
    public void DegradedLookupRequiresEveryLane(string kind)
    {
        var result = AcceptanceLaneReuseShadowClassifier.Classify([Source], [Lane("Alpha"), Lane("Beta")], [],
            new(false, [], kind, "fixture degradation"));
        Assert.Equal(2, result.Lanes.Count);
        Assert.All(result.Lanes, lane => { Assert.Equal("must-run", lane.Decision); Assert.Equal($"dependency-index-degraded:{kind}", lane.Reason); });
    }

    [Fact]
    public void ProjectChangeRequiresEveryLane()
    {
        var result = Classify(["src/Fixture/Fixture.csproj"], []);
        AssertDecision(result, "must-run", "non-csharp-change:src/Fixture/Fixture.csproj");
    }

    [Fact]
    public void OtherTestProjectIsIgnoredWithoutADegradedLookup()
    {
        const string path = "tests/Mcg.AgentOrchestrator.Core.Tests/WidgetTests.cs";
        var result = AcceptanceLaneReuseShadowClassifier.Classify([path], [Lane("Alpha"), Lane("Beta")], [],
            new(false, [], "Unavailable", "lookup is unnecessary"));
        Assert.Equal([path], result.IgnoredPaths);
        Assert.Equal(2, result.Lanes.Count);
        Assert.All(result.Lanes, lane => { Assert.Equal("would-reuse", lane.Decision); Assert.Equal("unaffected", lane.Reason); });
        Assert.Empty(AcceptanceLaneReuseShadowClassifier.SelectLookupPaths([path]).LookupPaths);
    }

    [Fact]
    public void LookupSelectionIncludesEverySourceAndOnlyInfrastructureAndSupportTests()
    {
        var paths = new[] { Source, TestFile, "tests/Mcg.AgentOrchestrator.TestSupport/Base.cs", "tests/Mcg.AgentOrchestrator.Core.Tests/WidgetTests.cs" };
        var result = AcceptanceLaneReuseShadowClassifier.SelectLookupPaths(paths);
        Assert.Equal(paths.Take(3).Order(StringComparer.Ordinal), result.LookupPaths);
        Assert.Equal([paths[3]], result.IgnoredPaths);
    }

    [Fact]
    public void OutsidePathWinsOverNonCSharpLookupAndMarkers()
    {
        var result = AcceptanceLaneReuseShadowClassifier.Classify([Source, "docs/note.md", "src/Fixture/Fixture.csproj"],
            [Lane(keys: ["xunit:ProcessSpawning"])], [], new(false, [], "Unavailable", "fixture"));
        AssertDecision(result, "must-run", "change-outside-src-tests:docs/note.md");
    }

    [Fact]
    public void MarkerWinsOverChangedTestAndReferencedSource()
    {
        var result = Classify([TestFile], [new("PlainTests", [TestFile], "ProcessStartInfo")], new(true, ["PlainTests"], null, null));
        AssertDecision(result, "must-run", "always-affected:process-spawning:PlainTests");
    }

    [Theory]
    [InlineData("processstartinfo")]
    [InlineData("PROCESSSTARTINFO")]
    public void MarkerMatchingIsOrdinalAndCaseSensitive(string text)
    {
        AssertDecision(Classify([Source], [new("PlainTests", [TestFile], text)]), "would-reuse", "unaffected");
    }

    [Fact]
    public void AmbiguousShortNameSelectsEveryMatchingClass()
    {
        var result = AcceptanceLaneReuseShadowClassifier.Classify([Source],
            [Lane("Alpha", "FullyQualifiedName=One.PlainTests"), Lane("Beta", "FullyQualifiedName=Two.PlainTests")],
            [new("One.PlainTests", [TestFile], ""), new("Two.PlainTests", [TestFile], "")],
            new(true, ["PlainTests"], null, null));
        Assert.Equal(2, result.Lanes.Count);
        Assert.Equal("references-changed-source:One.PlainTests", result.Lanes[0].Reason);
        Assert.Equal("references-changed-source:Two.PlainTests", result.Lanes[1].Reason);
        Assert.All(result.Lanes, lane => Assert.Equal("must-run", lane.Decision));
    }

    [Fact]
    public void UnsupportedMembershipCannotBeReusableEvenWithAnEmptyInventory()
    {
        var result = AcceptanceLaneReuseShadowClassifier.Classify([Source], [Lane(filter: "Unsupported=Value")], [], Resolved);
        AssertDecision(result, "must-run", "lane-membership-unresolved");
    }

    [Fact]
    public void EmptyOrUnresolvedChangesCannotBeReusable()
    {
        AssertDecision(Classify([], []), "must-run", "shadow-unavailable:no-changed-files");
        var result = AcceptanceLaneReuseShadowClassifier.Classify(null, [Lane()], [], Resolved, "changed-files:IOException");
        AssertDecision(result, "must-run", "shadow-unavailable:changed-files:IOException");
    }

    [Fact]
    public void ScannerIncludesPartialDeclaringFilesAndTransitiveAncestorFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mcg-shadow-inventory-{Guid.NewGuid():N}");
        var directory = Path.Combine(root, "tests", "Mcg.AgentOrchestrator.Infrastructure.Tests");
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "Ancestor.cs"),
                "public abstract class Ancestor { [Xunit.Fact] public void Example() {} } // ProcessStartInfo");
            File.WriteAllText(Path.Combine(directory, "Middle.cs"), "public abstract class Middle : Ancestor {}");
            File.WriteAllText(Path.Combine(directory, "DerivedTests.cs"), "public partial class DerivedTests : Middle {}");
            File.WriteAllText(Path.Combine(directory, "DerivedPart.cs"), "public partial class DerivedTests {}");
            var inventory = AcceptanceTestClassSourceScanner.ScanSources(root);
            var derived = Assert.Single(inventory);
            Assert.Equal("DerivedTests", derived.FullName);
            Assert.Equal(new[] { "Ancestor.cs", "DerivedPart.cs", "DerivedTests.cs", "Middle.cs" }
                .Select(name => $"tests/Mcg.AgentOrchestrator.Infrastructure.Tests/{name}"), derived.SourcePaths);
            Assert.Contains("ProcessStartInfo", derived.SourceText, StringComparison.Ordinal);
            AssertDecision(Classify([Source], inventory), "must-run", "always-affected:process-spawning:DerivedTests");
            AssertDecision(Classify(["tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Ancestor.cs"],
                [derived with { SourceText = "" }]), "must-run", "changed-test-class:DerivedTests");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static AcceptanceLaneReuseShadowClassification Classify(IReadOnlyList<string> paths,
        IReadOnlyList<AcceptanceTestClassSource> inventory, ReverseDependencyTestImpactLookupResult? lookup = null) =>
        AcceptanceLaneReuseShadowClassifier.Classify(paths, [Lane()], inventory, lookup ?? Resolved);

    private static void AssertDecision(AcceptanceLaneReuseShadowClassification result, string decision, string reason)
    {
        var lane = Assert.Single(result.Lanes);
        Assert.Equal(decision, lane.Decision);
        Assert.Equal(reason, lane.Reason);
    }

    private static AcceptanceManifestCheck Lane(string name = "Alpha", string filter = "FullyQualifiedName~Tests", string[]? keys = null) => new()
    {
        Name = $"infrastructure tests: {name}", Type = "dotnet-test",
        Project = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
        Arguments = ["--filter", filter], ExclusiveResourceKeys = keys ?? []
    };
}
