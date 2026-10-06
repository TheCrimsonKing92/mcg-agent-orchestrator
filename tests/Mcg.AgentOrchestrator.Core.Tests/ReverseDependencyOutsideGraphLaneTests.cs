using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

// Parallel-safe: each fact owns and deletes a unique temporary repository.
public sealed class ReverseDependencyOutsideGraphLaneTests
{
    private const string InfrastructureProject =
        "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj";
    private const string CliPrefix = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Cli/";
    private const string CliProject = CliPrefix + "Mcg.AgentOrchestrator.Infrastructure.Cli.Tests.csproj";
    private const string CliSource = CliPrefix + "ExampleCliTests.cs";
    private const string Manifest = "config/acceptance-manifest.json";

    [Fact]
    public void Lookup_NestedCliSource_RecordsLaneWithoutDegradation()
    {
        using var repository = new Repository();
        var indexed = ReverseDependencyTestImpactReader.Read(repository.Root, [CliSource]);
        Assert.Equal(ReverseDependencySelectionOutcome.Unreadable, indexed.Outcome);

        var result = ReverseDependencyTestImpactReaderLookup.Find(repository.Root, [CliSource]);

        Assert.True(result.Resolved, result.Reason);
        Assert.Null(result.DegradationKind);
        Assert.Null(result.Reason);
        Assert.Empty(result.TestClassNames);
        Assert.Equal(["cli tests"], result.AffectedTestLanes);
    }

    [Fact]
    public void Planner_NestedCliSource_SelectsDeclaredClassesInCliOnly()
    {
        using var repository = new Repository();
        var plan = repository.Plan(CliSource);

        var check = Assert.Single(plan.Checks);
        Assert.Equal("focused changed cli tests", check.Name);
        Assert.Equal(RepositoryTestProject.Cli, check.TestProject);
        Assert.Equal(["ExampleCliHelperTests", "ExampleCliTests"], check.TestClassSelections);
        Assert.Equal(new[] { "dotnet", "test", "--project", CliProject, "--verbosity", "minimal",
            "--filter", "FullyQualifiedName~ExampleCliHelperTests|FullyQualifiedName~ExampleCliTests" }, check.Command);
        Assert.DoesNotContain(plan.Checks, item => item.TestProject == RepositoryTestProject.Infrastructure);
    }

    [Fact]
    public void Planner_MultipleCliFiles_AggregatesOneCliCheck()
    {
        using var repository = new Repository();
        const string nested = CliPrefix + "Nested/MoreCliTests.cs";
        repository.Write(nested, "public class MoreCliTests { [Xunit.Fact] public void Example() {} }");

        var check = Assert.Single(repository.Plan(CliSource, nested).Checks);

        Assert.Equal("focused changed cli tests", check.Name);
        Assert.Equal(RepositoryTestProject.Cli, check.TestProject);
        Assert.Equal(["ExampleCliHelperTests", "ExampleCliTests", "MoreCliTests"], check.TestClassSelections);
        Assert.Equal("FullyQualifiedName~ExampleCliHelperTests|FullyQualifiedName~ExampleCliTests|FullyQualifiedName~MoreCliTests",
            check.Command.Last());
    }

    [Fact]
    public void Planner_CliFileWithoutTestClass_RunsCliUnfiltered()
    {
        using var repository = new Repository();
        repository.Write(CliSource, "public class CliHelper {}");

        var check = Assert.Single(repository.Plan(CliSource).Checks);

        Assert.Equal("focused changed cli tests", check.Name);
        Assert.Equal(RepositoryTestProject.Cli, check.TestProject);
        Assert.Null(check.TestClassSelections);
        Assert.Equal(new[] { "dotnet", "test", "--project", CliProject, "--verbosity", "minimal" }, check.Command);
        Assert.Contains("declares no qualifying test class", check.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Planner_CliAndInfrastructureTests_SeparatesProjectFilters()
    {
        using var repository = new Repository();

        var plan = repository.Plan(CliSource, "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ConsumerTests.cs");

        Assert.Equal(2, plan.Checks.Count);
        var infrastructure = Assert.Single(plan.Checks.Where(check => check.TestProject == RepositoryTestProject.Infrastructure));
        Assert.Equal(["ConsumerTests"], infrastructure.TestClassSelections);
        Assert.Equal("FullyQualifiedName~ConsumerTests", infrastructure.Command.Last());
        var cli = Assert.Single(plan.Checks.Where(check => check.TestProject == RepositoryTestProject.Cli));
        Assert.Equal(["ExampleCliHelperTests", "ExampleCliTests"], cli.TestClassSelections);
    }

    [Theory]
    [InlineData("scripts/verify.ps1", false)]
    [InlineData("scripts/verify.ps1", true)]
    [InlineData("config/settings.json", false)]
    [InlineData("config/settings.json", true)]
    public void Planner_ScriptsOrConfig_KeepsInfrastructureCheck(string path, bool includeCli)
    {
        using var repository = new Repository();
        repository.Write(path, "fixture");

        var plan = includeCli ? repository.Plan(path, CliSource) : repository.Plan(path);

        var infrastructure = Assert.Single(plan.Checks.Where(check => check.TestProject == RepositoryTestProject.Infrastructure));
        Assert.Equal("infrastructure tests", infrastructure.Name);
        Assert.DoesNotContain("--filter", infrastructure.Command);
        Assert.Equal(includeCli ? 2 : 1, plan.Checks.Count);
    }

    [Theory]
    [InlineData("outside/Unindexed.cs")]
    [InlineData("scripts/Unindexed.cs")]
    [InlineData("config/Unindexed.cs")]
    public void Lookup_UnownedSource_PreservesUnreadable(string path)
    {
        using var repository = new Repository();
        repository.Write(path, "public class Unindexed {}");
        var expected = ReverseDependencyTestImpactReader.Read(repository.Root, [path]);

        var result = ReverseDependencyTestImpactReaderLookup.Find(repository.Root, [path]);

        Assert.False(result.Resolved);
        Assert.Equal("Unreadable", result.DegradationKind);
        Assert.Equal(expected.Reason, result.Reason);
        Assert.Empty(result.TestClassNames);
        Assert.Empty(result.AffectedTestLanes);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("invalid json")]
    [InlineData("{}")]
    [InlineData("{\"checks\":{}}")]
    public void Lookup_MissingOrMalformedManifest_PreservesDependencyRead(string? manifest)
    {
        using var repository = new Repository();
        if (manifest is null) File.Delete(Path.Combine(repository.Root, Manifest));
        else repository.Write(Manifest, manifest);

        var result = ReverseDependencyTestImpactReaderLookup.Find(repository.Root, [CliSource]);

        Assert.False(result.Resolved);
        Assert.Equal("Unreadable", result.DegradationKind);
        Assert.Empty(result.AffectedTestLanes);
    }

    [Fact]
    public void Lookup_UnreadableManifest_PreservesDependencyRead()
    {
        using var repository = new Repository();
        var path = Path.Combine(repository.Root, Manifest);
        File.Delete(path);
        Directory.CreateDirectory(path);

        var result = ReverseDependencyTestImpactReaderLookup.Find(repository.Root, [CliSource]);

        Assert.False(result.Resolved);
        Assert.Equal("Unreadable", result.DegradationKind);
        Assert.Empty(result.AffectedTestLanes);
    }

    [Fact]
    public void Lookup_InfrastructureProject_IsStillRead()
    {
        using var repository = new Repository();
        const string path = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ConsumerTests.cs";
        repository.Write("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/InfrastructureFollowerTests.cs",
            "public class InfrastructureFollowerTests { private ConsumerTests value; [Xunit.Fact] public void Example() {} }");

        var result = ReverseDependencyTestImpactReaderLookup.Find(repository.Root, [path]);

        Assert.True(result.Resolved, result.Reason);
        Assert.Equal(["InfrastructureFollowerTests"], result.TestClassNames);
        Assert.Empty(result.AffectedTestLanes);
    }

    [Fact]
    public void Lookup_MixedPaths_CombinesClassesAndDeduplicatedLanes()
    {
        using var repository = new Repository();
        const string otherProject = "tests/Other/Other.csproj";
        const string otherSource = "tests/Other/OtherTests.cs";
        repository.Write(otherProject, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        repository.Write(otherSource, "public class OtherTests { [Xunit.Fact] public void Example() {} }");
        const string anotherCliSource = CliPrefix + "Nested/MoreCliTests.cs";
        repository.Write(anotherCliSource, "public class MoreCliTests { [Xunit.Fact] public void Example() {} }");
        repository.WriteManifest((CliProject.ToUpperInvariant().Replace('/', '\\'), "cli tests"),
            (otherProject, "other tests"));

        var result = ReverseDependencyTestImpactReaderLookup.Find(repository.Root,
            [otherSource, CliSource.Replace('/', '\\'), "src/Fixture/Widget.cs", CliSource, anotherCliSource]);

        Assert.True(result.Resolved, result.Reason);
        Assert.Equal(["ConsumerTests"], result.TestClassNames);
        Assert.Equal(["cli tests", "other tests"], result.AffectedTestLanes);
    }

    [Fact]
    public void Lookup_UnlistedNearestProject_DoesNotUseAncestorLane()
    {
        using var repository = new Repository();
        const string path = CliPrefix + "Nested/UnlistedTests.cs";
        repository.Write(CliPrefix + "Nested/Unlisted.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        repository.Write(path, "public class UnlistedTests { [Xunit.Fact] public void Example() {} }");

        var result = ReverseDependencyTestImpactReaderLookup.Find(repository.Root, [path]);

        Assert.False(result.Resolved);
        Assert.Equal("Unreadable", result.DegradationKind);
        Assert.Empty(result.AffectedTestLanes);
    }

    [Fact]
    public void Lookup_LaneThenDeletedPath_PreservesFirstDegradation()
    {
        using var repository = new Repository();
        const string deleted = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/MissingTests.cs";

        var result = ReverseDependencyTestImpactReaderLookup.Find(repository.Root, [CliSource, deleted]);

        Assert.False(result.Resolved);
        Assert.Equal("Unavailable", result.DegradationKind);
        Assert.Empty(result.TestClassNames);
        Assert.Equal(["cli tests"], result.AffectedTestLanes);
    }

    private sealed class Repository : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), $"mcg-outside-graph-{Guid.NewGuid():N}");

        internal Repository()
        {
            Write("src/Fixture/Fixture.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            Write("src/Fixture/Widget.cs", "public class Widget {}");
            Write(InfrastructureProject, "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup>" +
                "<ProjectReference Include=\"../../src/Fixture/Fixture.csproj\" /></ItemGroup></Project>");
            Write("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ConsumerTests.cs",
                "public class ConsumerTests { private Widget value; [Xunit.Fact] public void Example() {} }");
            Write(CliProject, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            Write(CliSource, "public class ExampleCliTests { [Xunit.Fact] public void Example() {} } " +
                "public class ExampleCliHelperTests { [Xunit.Fact] public void Example() {} }");
            WriteManifest((InfrastructureProject, "infrastructure tests"), (CliProject, "cli tests"));
        }

        internal RepositoryTestImpactPlan Plan(params string[] paths) =>
            RepositoryTestImpactPlanner.Plan(RepositoryChangeClassifier.Classify(paths), Root);

        internal void WriteManifest(params (string Project, string Name)[] lanes) => Write(Manifest,
            JsonSerializer.Serialize(new { checks = lanes.Select(lane =>
                new { type = "dotnet-test", project = lane.Project, name = lane.Name }).ToArray() }));

        internal void Write(string path, string text)
        {
            var absolute = Path.Combine(Root, path);
            Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
            File.WriteAllText(absolute, text);
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
