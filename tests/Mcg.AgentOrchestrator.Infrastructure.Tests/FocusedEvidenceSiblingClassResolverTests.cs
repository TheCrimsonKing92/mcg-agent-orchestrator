using Mcg.AgentOrchestrator.Infrastructure;

public sealed class FocusedEvidenceSiblingClassResolverTests
{
    [Xunit.Fact]
    public void SharedFileReturnsOnlyConcretePublicTestSiblings()
    {
        var root = CreateProject(
            """
            namespace Fixture;
            public sealed class RequestedTests { [Xunit.Fact] public void Runs() { } }
            public sealed class FirstSiblingTests { [Xunit.Theory] public void Runs() { } }
            public sealed class CustomSiblingTests { [OptInFact] public void Runs() { } }
            public sealed record RecordSiblingTests { [Xunit.Fact] public void Runs() { } }
            public sealed class FourthSiblingTests { [Xunit.Fact] public void Runs() { } }
            public sealed class FifthSiblingTests { [Xunit.Fact] public void Runs() { } }
            public abstract class AbstractTests { [Xunit.Fact] public void Runs() { } }
            internal sealed class InternalHelper { public void Runs() { } }
            public sealed class PublicHelper { public void Runs() { } }
            internal sealed class OptInFactAttribute : System.Attribute { }
            """);

        var siblings = Resolve(root, "Fixture.RequestedTests");

        Assert.Contains("FirstSiblingTests", siblings);
        Assert.Contains("CustomSiblingTests", siblings);
        Assert.Contains("RecordSiblingTests", siblings);
        Assert.Contains("FourthSiblingTests", siblings);
        Assert.Contains("FifthSiblingTests", siblings);
        Assert.DoesNotContain("RequestedTests", siblings);
        Assert.DoesNotContain("AbstractTests", siblings);
        Assert.DoesNotContain("InternalHelper", siblings);
        Assert.DoesNotContain("PublicHelper", siblings);
        Assert.DoesNotContain("OptInFactAttribute", siblings);
    }

    [Xunit.Fact]
    public void UnknownOrAmbiguousClassReturnsNoSiblings()
    {
        var root = CreateProject(
            "public sealed class DuplicateTests { [Xunit.Fact] public void Runs() { } }");
        File.WriteAllText(
            Path.Combine(root, "tests", "Fixture.Tests", "Duplicate.cs"),
            "public sealed class DuplicateTests { [Xunit.Fact] public void AlsoRuns() { } }");

        Assert.Empty(Resolve(root, "MissingTests"));
        Assert.Empty(Resolve(root, "DuplicateTests"));
    }

    [Xunit.Fact]
    public void NestedProjectClassesAreNotReturnedAsSiblings()
    {
        var root = CreateProject(
            """
            public sealed class RequestedTests { [Xunit.Fact] public void Runs() { } }
            public sealed class MainSiblingTests { [Xunit.Fact] public void Runs() { } }
            """);
        var nested = Path.Combine(root, "tests", "Fixture.Tests", "Nested");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(nested, "Nested.Tests.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(
            Path.Combine(nested, "NestedTests.cs"),
            "public sealed class NestedTests { [Xunit.Fact] public void Runs() { } }");

        var siblings = Resolve(root, "RequestedTests");

        Assert.Contains("MainSiblingTests", siblings);
        Assert.DoesNotContain("NestedTests", siblings);
    }

    [Xunit.Fact]
    public void RealVerifierFileIncludesBuildSlotSiblingAndExcludesHelpers()
    {
        var root = InfrastructureTestSupport.FindRepositoryRoot();

        var siblings = FocusedEvidenceSiblingClassResolver.ResolveSiblingTestClassNames(
            root,
            "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
            "GoalAcceptanceVerifierTests");

        Assert.Contains("GoalAcceptanceVerifierDotnetBuildSlotTests", siblings);
        Assert.Contains("RealProcessShardAlphaSmokeTests", siblings);
        Assert.DoesNotContain("GoalAcceptanceVerifierTestBase", siblings);
        Assert.DoesNotContain("RecordingTimeProvider", siblings);
        Assert.DoesNotContain("AcceptanceManifestTestDefaults", siblings);
        Assert.DoesNotContain("OptInRealAcceptanceVerifierFactAttribute", siblings);
    }

    private static string CreateProject(string source)
    {
        var root = InfrastructureTestSupport.CreateTempDirectory();
        var projectDirectory = Path.Combine(root, "tests", "Fixture.Tests");
        Directory.CreateDirectory(projectDirectory);
        File.WriteAllText(
            Path.Combine(projectDirectory, "Fixture.Tests.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(Path.Combine(projectDirectory, "Tests.cs"), source);
        return root;
    }

    private static IReadOnlyList<string> Resolve(string root, string requestedClass) =>
        FocusedEvidenceSiblingClassResolver.ResolveSiblingTestClassNames(
            root,
            "tests/Fixture.Tests/Fixture.Tests.csproj",
            requestedClass);
}
