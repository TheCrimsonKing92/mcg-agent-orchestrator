using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: fixture writes use private temporary roots; repository cases only read source.
public sealed class FocusedEvidenceSiblingExpansionBoundTests
{
    private const string FixtureDirectory = "tests/Fixture.Tests/";
    private const string FixtureProject = FixtureDirectory + "Fixture.Tests.csproj";
    private const string RepositoryDirectory = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/";
    private const string RepositoryProject = RepositoryDirectory + "Mcg.AgentOrchestrator.Infrastructure.Tests.csproj";

    [Xunit.Fact]
    public void Decide_ConcreteChanges_KeepOnlyChangedSiblingsInResolverOrder()
    {
        using var fixture = CreateFixture();
        var siblings = ResolveFixture(fixture.Root);
        var bound = new FocusedEvidenceSiblingExpansionBound(2);

        var unrelated = bound.Decide(fixture.Root, FixtureProject, siblings,
            [FixtureDirectory + "Unrelated.cs"]);
        Assert.Empty(unrelated.KeptSiblings);
        Assert.Equal("bounded family=20 kept=0", unrelated.Reason);

        var one = bound.Decide(fixture.Root, FixtureProject, siblings,
            [FixtureDirectory + "Derived07Tests.cs"]);
        Assert.Equal(new[] { "Derived07Tests" }, one.KeptSiblings);
        Assert.Equal("bounded family=20 kept=1", one.Reason);

        var several = bound.Decide(fixture.Root, FixtureProject, siblings,
            [FixtureDirectory + "Derived12Tests.cs", FixtureDirectory + "Derived07Tests.cs"]);
        Assert.Equal(new[] { "Derived07Tests", "Derived12Tests" }, several.KeptSiblings);
    }

    [Xunit.Theory]
    [Xunit.InlineData("Base.cs")]
    [Xunit.InlineData("Mid.cs")]
    public void Decide_AbstractLayerChange_PreservesTheResolverList(string changedFile)
    {
        using var fixture = CreateFixture();
        fixture.Write(FixtureDirectory + "Mid.cs", "public abstract class MidTestBase : FamilyTestBase { }");
        var siblings = ResolveFixture(fixture.Root);

        var result = new FocusedEvidenceSiblingExpansionBound(2).Decide(
            fixture.Root, FixtureProject, siblings, [FixtureDirectory + changedFile]);

        Assert.Same(siblings, result.KeptSiblings);
        Assert.Equal(siblings, result.KeptSiblings);
        Assert.Equal("abstract-layer-changed", result.Reason);
    }

    [Xunit.Fact]
    public void Decide_UnavailablePaths_PreservesTheResolverList()
    {
        using var fixture = CreateFixture();
        var siblings = ResolveFixture(fixture.Root);
        var bound = new FocusedEvidenceSiblingExpansionBound(2);
        foreach (var paths in new IReadOnlyList<string>?[] { null, Array.Empty<string>() })
        {
            var result = bound.Decide(fixture.Root, FixtureProject, siblings, paths);
            Assert.Same(siblings, result.KeptSiblings);
            Assert.Equal("changed-paths-unavailable", result.Reason);
        }
    }

    [Xunit.Theory]
    [Xunit.InlineData("Missing.cs", false)]
    [Xunit.InlineData("Broken.cs", true)]
    public void Decide_UnreadableSource_WinsOverAbstractRegardlessOfOrder(
        string changedFile, bool writeBrokenSource)
    {
        using var fixture = CreateFixture();
        var siblings = ResolveFixture(fixture.Root);
        if (writeBrokenSource)
            fixture.Write(FixtureDirectory + changedFile, "public class Broken { void Runs( { }");
        var bound = new FocusedEvidenceSiblingExpansionBound(2);
        var paths = new[] { FixtureDirectory + "Base.cs", FixtureDirectory + changedFile };
        foreach (var orderedPaths in new[] { paths, paths.Reverse().ToArray() })
        {
            var result = bound.Decide(fixture.Root, FixtureProject, siblings, orderedPaths);
            Assert.Same(siblings, result.KeptSiblings);
            Assert.Equal("changed-source-unreadable", result.Reason);
        }
    }

    [Xunit.Theory]
    [Xunit.InlineData(3)]
    [Xunit.InlineData(16)]
    public void Resolve_WithinBound_DoesNotConsultChangedPaths(int count)
    {
        using var fixture = CreateFixture(count);
        var calls = 0;
        var result = new FocusedEvidenceSiblingExpansionBound(2).Resolve(
            fixture.Root, FixtureProject, "RequestedTests", _ =>
            {
                calls++;
                throw new InvalidOperationException("Provider must not be needed.");
            });

        Assert.Equal(ResolveFixture(fixture.Root, count), result.KeptSiblings);
        Assert.Equal("within-bound", result.Reason);
        Assert.Equal(0, calls);
    }

    [Xunit.Fact]
    public void Resolve_UnchangedSources_ReuseFactsUntilMtimeChanges()
    {
        using var fixture = CreateFixture();
        var bound = new FocusedEvidenceSiblingExpansionBound(2);
        var calls = 0;
        IReadOnlyList<string> Provider(string _) { calls++; return [FixtureDirectory + "Derived07Tests.cs"]; }

        var first = bound.Resolve(fixture.Root, FixtureProject, "RequestedTests", Provider);
        var second = bound.Resolve(fixture.Root, FixtureProject, "RequestedTests", Provider);
        Assert.Equal(new[] { "Derived07Tests" }, first.KeptSiblings);
        Assert.Equal(first.KeptSiblings, second.KeptSiblings);
        Assert.Equal(first.Reason, second.Reason);
        Assert.Equal(1, calls);

        var path = Path.Combine(fixture.Root, FixtureDirectory, "Unrelated.cs");
        File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddMinutes(1));
        var third = bound.Resolve(fixture.Root, FixtureProject, "RequestedTests", Provider);
        Assert.Equal(first.KeptSiblings, third.KeptSiblings);
        Assert.Equal(2, calls);
    }

    [Xunit.Theory]
    [Xunit.InlineData("throw", "changed-paths-unavailable")]
    [Xunit.InlineData("empty", "changed-paths-unavailable")]
    [Xunit.InlineData("missing", "changed-source-unreadable")]
    public void Resolve_FailedFacts_AreNotMemoized(string failure, string reason)
    {
        using var fixture = CreateFixture();
        var bound = new FocusedEvidenceSiblingExpansionBound(2);
        var siblings = ResolveFixture(fixture.Root);
        var failed = bound.Resolve(fixture.Root, FixtureProject, "RequestedTests", _ => failure switch
        {
            "throw" => throw new IOException("Changed paths unavailable."),
            "empty" => [],
            _ => [FixtureDirectory + "Missing.cs"]
        });
        Assert.Equal(siblings, failed.KeptSiblings);
        Assert.Equal(reason, failed.Reason);

        var calls = 0;
        var recovered = bound.Resolve(fixture.Root, FixtureProject, "RequestedTests", _ =>
        {
            calls++;
            return [FixtureDirectory + "Derived07Tests.cs"];
        });
        Assert.Equal(1, calls);
        Assert.Equal(new[] { "Derived07Tests" }, recovered.KeptSiblings);
    }

    [Xunit.Fact]
    public void Resolve_CapacityExceeded_EvictsLeastRecentlyUsedFacts()
    {
        using var first = CreateFixture();
        using var second = CreateFixture();
        using var third = CreateFixture();
        var bound = new FocusedEvidenceSiblingExpansionBound(2);
        var calls = 0;
        IReadOnlyList<string> Provider(string _) { calls++; return [FixtureDirectory + "Derived07Tests.cs"]; }
        foreach (var root in new[] { first.Root, second.Root, first.Root, third.Root, second.Root })
            Assert.Equal(new[] { "Derived07Tests" },
                bound.Resolve(root, FixtureProject, "RequestedTests", Provider).KeptSiblings);

        Assert.Equal(4, calls);
    }

    [Xunit.Fact]
    public void Decide_ExcludedSourcesAndNestedTypes_DoNotRetainSiblings()
    {
        using var fixture = CreateFixture();
        var siblings = ResolveFixture(fixture.Root);
        fixture.Write(FixtureDirectory + "Nested/Nested.csproj", "<Project />");
        fixture.Write(FixtureDirectory + "Nested/Base.cs", "public abstract class NestedBase { }");
        fixture.Write(FixtureDirectory + "obj/Generated.cs", "syntax is invalid {");
        fixture.Write(FixtureDirectory + "Names.cs", """
            namespace Fixture {
                public class Container { public class Derived07Tests { } }
                public record Derived12Tests;
            }
            """);
        var bound = new FocusedEvidenceSiblingExpansionBound(2);
        var excluded = bound.Decide(fixture.Root, FixtureProject, siblings,
            ["elsewhere/Missing.cs", FixtureDirectory + "Nested/Base.cs",
             FixtureDirectory + "obj/Generated.cs", FixtureDirectory + "notes.txt"]);
        Assert.Empty(excluded.KeptSiblings);
        Assert.Equal("bounded family=20 kept=0", excluded.Reason);

        var names = bound.Decide(fixture.Root, FixtureProject, siblings, [FixtureDirectory + "Names.cs"]);
        Assert.Equal(new[] { "Derived12Tests" }, names.KeptSiblings);
    }

    [Xunit.Fact]
    public void Decide_IncidentReplay_BoundsConcreteChangesAndPreservesAbstractChanges()
    {
        var root = InfrastructureTestSupport.FindRepositoryRoot();
        var snapshot = RepositorySourceSnapshotCache.Get(root);
        var siblings = FocusedEvidenceSiblingClassResolver.ResolveSiblingTestClassNames(
            root, RepositoryProject, "GoalWorktreeReducedFixtureTests",
            snapshot.GetText, (path, _) => snapshot.GetSyntaxTree(path));
        Assert.True(siblings.Count > 16);
        Assert.Contains("AcceptanceCohortWorkflowTestsMergeTrain", siblings);
        var bound = new FocusedEvidenceSiblingExpansionBound(2);

        Assert.Empty(bound.Decide(root, RepositoryProject, siblings, IncidentChangedPaths()).KeptSiblings);
        Assert.Equal(new[] { "GoalWorktreeTestsRebaseMerge" },
            bound.Decide(root, RepositoryProject, siblings,
                [RepositoryDirectory + "GoalWorktreeTestsRebaseMerge.cs"]).KeptSiblings);
        foreach (var file in new[] { "GoalWorktreeTests.cs", "AcceptanceCohortWorkflowTests.cs" })
        {
            var result = bound.Decide(root, RepositoryProject, siblings, [RepositoryDirectory + file]);
            Assert.Same(siblings, result.KeptSiblings);
            Assert.Equal("abstract-layer-changed", result.Reason);
        }
    }

    [Xunit.Fact]
    public void Normalize_IncidentReplay_KeepsFourUnlessAbstractLayerChanged()
    {
        var root = InfrastructureTestSupport.FindRepositoryRoot();
        var filters = new[] { "GoalWorktreeReducedFixtureTests", "GoalWorktreeTestsAcceptanceRetry",
            "GoalWorktreeIsolatedDotnetTests", "GoalWorktreeAcceptanceContentionTests" }
            .Select(name => "FullyQualifiedName~" + name).Order(StringComparer.Ordinal).ToArray();
        var request = new FindingEvidenceRequest(filters
            .Select(filter => new FindingEvidenceSelection("Infrastructure.Tests", filter)).ToArray());
        var bounded = Normalize(root, request, IncidentChangedPaths());
        Assert.Equal(4, bounded.Selections.Count);
        Assert.Equal(filters, bounded.Selections.Select(selection => selection.TestClass));
        Assert.All(bounded.Selections, selection => Assert.Equal("Infrastructure.Tests", selection.TestProject));

        var expanded = Normalize(root, request, [RepositoryDirectory + "GoalWorktreeTests.cs"]);
        Assert.Contains(expanded.Selections,
            selection => selection.TestClass == "FullyQualifiedName~AcceptanceCohortWorkflowTestsMergeTrain");
    }

    private static FindingEvidenceRequest Normalize(
        string root, FindingEvidenceRequest request, IReadOnlyList<string> changedPaths)
    {
        var bound = new FocusedEvidenceSiblingExpansionBound(2);
        Assert.True(ConductorDriver.TryNormalizeFindingEvidenceRequest(request, new AcceptanceGateEngineSettings(),
            (project, requested) => bound.Resolve(root, project, requested, _ => changedPaths).KeptSiblings,
            out var normalized, out _, out _, out var detail), detail);
        return normalized;
    }

    private static string[] IncidentChangedPaths() =>
        [RepositoryDirectory + "TestCollections.cs", RepositoryDirectory + "WorkspaceProbeTimeoutRegateScenarioTests.cs"];

    private static SourceSnapshotFixture CreateFixture(int count = 20)
    {
        var fixture = new SourceSnapshotFixture();
        fixture.Write(FixtureProject, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        fixture.Write(FixtureDirectory + "Base.cs", """
            public abstract class FamilyTestBase { }
            public sealed class RequestedTests : FamilyTestBase { [Xunit.Fact] public void Runs() { } }
            """);
        for (var index = 1; index <= count; index++)
        {
            var name = $"Derived{index:00}Tests";
            fixture.Write(FixtureDirectory + name + ".cs",
                $"public sealed class {name} : FamilyTestBase {{ [Xunit.Fact] public void Runs() {{ }} }}");
        }
        fixture.Write(FixtureDirectory + "Unrelated.cs",
            "public sealed class UnrelatedTests { [Xunit.Fact] public void Runs() { } }");
        return fixture;
    }

    private static IReadOnlyList<string> ResolveFixture(string root, int count = 20)
    {
        var siblings = FocusedEvidenceSiblingClassResolver.ResolveSiblingTestClassNames(
            root, FixtureProject, "RequestedTests");
        Assert.Equal(count, siblings.Count);
        return siblings;
    }
}
