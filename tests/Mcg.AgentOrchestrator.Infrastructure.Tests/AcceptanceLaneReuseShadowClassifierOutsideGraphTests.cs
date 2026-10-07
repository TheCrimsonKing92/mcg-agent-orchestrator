using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using AcceptanceManifestCheck = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;

// Parallel-safe: the fixture owns and deletes a unique temporary repository.
public sealed class AcceptanceLaneReuseShadowClassifierOutsideGraphTests
{
    [Fact]
    public void Classify_NestedCliSource_DoesNotDegradeInfrastructureLanes()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mcg-shadow-outside-graph-{Guid.NewGuid():N}");
        const string source = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Cli/ExampleCliTests.cs";
        try
        {
            Write(root, "src/Fixture/Fixture.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            Write(root, "src/Fixture/Widget.cs", "public class Widget {}");
            Write(root, "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
                "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup>" +
                "<ProjectReference Include=\"../../src/Fixture/Fixture.csproj\" /></ItemGroup></Project>");
            Write(root, "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Cli/Mcg.AgentOrchestrator.Infrastructure.Cli.Tests.csproj",
                "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            Write(root, source, "public class ExampleCliTests { [Xunit.Fact] public void Example() {} }");
            Write(root, "config/acceptance-manifest.json", """
                {"checks":[{"name":"cli tests","type":"dotnet-test",
                "project":"tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Cli/Mcg.AgentOrchestrator.Infrastructure.Cli.Tests.csproj"}]}
                """);
            var paths = AcceptanceLaneReuseShadowClassifier.SelectLookupPaths([source]);
            Assert.Equal([source], paths.LookupPaths);
            var lookup = ReverseDependencyTestImpactReaderLookup.Find(root, paths.LookupPaths);
            Assert.True(lookup.Resolved, lookup.Reason);
            Assert.Null(lookup.DegradationKind);
            Assert.Empty(lookup.TestClassNames);
            Assert.Equal(["cli tests"], lookup.AffectedTestLanes);

            var result = AcceptanceLaneReuseShadowClassifier.Classify([source], [Lane("Alpha"), Lane("Beta")],
                [new("PlainTests", ["tests/Mcg.AgentOrchestrator.Infrastructure.Tests/PlainTests.cs"], "")], lookup);

            Assert.Equal(2, result.Lanes.Count);
            Assert.All(result.Lanes, lane =>
            {
                Assert.False(lane.Reason.StartsWith("dependency-index-degraded", StringComparison.Ordinal));
                Assert.Equal("would-reuse", lane.Decision);
                Assert.Equal("unaffected", lane.Reason);
            });
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static void Write(string root, string path, string text)
    {
        var absolute = Path.Combine(root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        File.WriteAllText(absolute, text);
    }

    private static AcceptanceManifestCheck Lane(string name) => new()
    {
        Name = $"infrastructure tests: {name}", Type = "dotnet-test",
        Project = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
        Arguments = ["--filter", "FullyQualifiedName~PlainTests"]
    };
}
