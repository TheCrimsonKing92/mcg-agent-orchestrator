using System.Reflection;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

// Parallel-safe: fixture roots are unique; real repository access is read-only.
public sealed class RepositoryTestReachTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(100)]
    public void TwoHopReach_ReturnsEveryConsumerBeyondPlannerClassCap(int count)
    {
        using var fixture = new RepositoryFixture();
        fixture.Write(RepositoryFixture.Intermediate, "public class Intermediate { private ChangedSource source; }");
        for (var index = 0; index < count; index++)
            fixture.Write($"{RepositoryFixture.TestDirectory}/Consumer{index}Tests.cs",
                $"public class Consumer{index}Tests {{ private Intermediate value; [Xunit.Fact] public void Runs() {{ }} }}");
        var reach = RepositoryTestReach.TryReadReachableTestClasses(fixture.Root, [RepositoryFixture.Source]);
        Assert.NotNull(reach);
        Assert.Equal(Enumerable.Range(0, count).Select(index => $"Consumer{index}Tests").Order(), reach.Order());
        if (count == 100)
            Assert.Equal("Abandoned", ReaderOutcome(fixture.Root));
    }

    [Fact]
    public void ShippingIntermediate_DefaultStaysOwnedAndOptionIncludesConsumers()
    {
        using var fixture = new RepositoryFixture();
        fixture.Write(RepositoryFixture.ShippingIntermediate, "public class Intermediate { private ChangedSource source; }");
        fixture.Write($"{RepositoryFixture.TestDirectory}/IntermediateTests.cs",
            "public class IntermediateTests { private Intermediate value; [Xunit.Fact] public void Runs() { } }");
        fixture.Write($"{RepositoryFixture.TestDirectory}/ConsumerTests.cs",
            "public class ConsumerTests { private Intermediate value; [Xunit.Fact] public void Runs() { } }");
        var defaultSelection = ReadSelection(fixture.Root, null);
        var explicitOff = ReadSelection(fixture.Root, false);
        Assert.Equal(defaultSelection.GetRawText(), explicitOff.GetRawText());
        Assert.Equal(new[] { "IntermediateTests" }, Names(defaultSelection));
        Assert.Equal(new[] { "ConsumerTests", "IntermediateTests" }, Names(ReadSelection(fixture.Root, true)));
        Assert.Equal(new[] { "IntermediateTests" }, Names(ReadSelection(fixture.Root, null)));
    }

    [Fact]
    public void SixChangedSources_ReturnsNull()
    {
        using var fixture = new RepositoryFixture();
        var paths = Enumerable.Range(0, 6).Select(index => $"src/Mcg.AgentOrchestrator.Infrastructure/Source{index}.cs").ToArray();
        foreach (var path in paths) fixture.Write(path, $"public class {Path.GetFileNameWithoutExtension(path)} {{ }}");
        Assert.Null(RepositoryTestReach.TryReadReachableTestClasses(fixture.Root, paths));
    }

    [Fact]
    public void SourceOutsideIndexedProjects_ReturnsNull()
    {
        using var fixture = new RepositoryFixture();
        fixture.Write("src/Unreferenced/Outside.cs", "public class Outside { }");
        Assert.Null(RepositoryTestReach.TryReadReachableTestClasses(fixture.Root, ["src/Unreferenced/Outside.cs"]));
    }

    [Fact]
    public void DeletedSourceAlongsideReadableSource_ReturnsNull()
    {
        using var fixture = new RepositoryFixture();
        Assert.Null(RepositoryTestReach.TryReadReachableTestClasses(fixture.Root,
            [RepositoryFixture.Source, "src/Mcg.AgentOrchestrator.Infrastructure/Deleted.cs"]));
    }

    [Fact]
    public void RealRepository_ResolverReachesBothIncidentClassesWithinBounds()
    {
        var root = VerifiedRepositoryRoot.Find();
        var reach = RepositoryTestReach.TryReadReachableTestClasses(root,
            ["src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/GoalAcceptanceVerifier.FocusedEvidenceRequestResolver.cs"]);
        Assert.NotNull(reach);
        Assert.Contains("GoalAcceptanceVerifierDotnetBuildSlotTestsSourceRevertedDeclaredSet", reach);
        Assert.Contains("GoalAcceptanceVerifierDotnetBuildSlotTestsFocusedUnionReceipts", reach);
    }

    [Fact]
    public void RealRepository_SelfCheckProbeResolvesWithoutIncidentClasses()
    {
        var reach = RepositoryTestReach.TryReadReachableTestClasses(VerifiedRepositoryRoot.Find(),
            ["src/Mcg.AgentOrchestrator.App/Orchestration/ConductorSelfCheckProbe.cs"]);
        Assert.NotNull(reach);
        Assert.DoesNotContain("GoalAcceptanceVerifierDotnetBuildSlotTestsSourceRevertedDeclaredSet", reach);
        Assert.DoesNotContain("GoalAcceptanceVerifierDotnetBuildSlotTestsFocusedUnionReceipts", reach);
    }

    // Core exposes only the reach API to this assembly. Reflect the reader to pin its
    // default semantics without widening production internals visibility for a test.
    private static readonly MethodInfo Reader = typeof(RepositoryTestReach).Assembly
        .GetType("Mcg.AgentOrchestrator.Core.ReverseDependencyTestImpactReader", throwOnError: true)!
        .GetMethod("Read", BindingFlags.Static | BindingFlags.NonPublic)!;

    private static object ReadRaw(string root, bool? propagate) => Reader.Invoke(null,
        [root, new[] { RepositoryFixture.Source }, true, Type.Missing, Type.Missing, Type.Missing,
            propagate.HasValue ? propagate.Value : Type.Missing])!;

    private static string ReaderOutcome(string root)
    {
        var result = ReadRaw(root, null);
        return result.GetType().GetProperty("Outcome")!.GetValue(result)!.ToString()!;
    }

    private static JsonElement ReadSelection(string root, bool? propagate)
    {
        var result = ReadRaw(root, propagate);
        var serialized = JsonSerializer.SerializeToElement(result, result.GetType());
        // Cache retention is process-wide diagnostic data, not selection output.
        return JsonSerializer.SerializeToElement(serialized.EnumerateObject()
            .Where(property => property.Name != "CacheReceipt")
            .ToDictionary(property => property.Name, property => property.Value));
    }

    private static string[] Names(JsonElement selection) => selection.GetProperty("TestClassNames")
        .EnumerateArray().Select(value => value.GetString()!).ToArray();

    internal sealed class RepositoryFixture : IDisposable
    {
        internal const string Source = "src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/ChangedSource.cs";
        internal const string Other = "src/Mcg.AgentOrchestrator.App/Orchestration/Other.Source.cs";
        internal const string ShippingIntermediate = "src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/Intermediate.cs";
        internal const string TestDirectory = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests";
        internal const string Intermediate = TestDirectory + "/Intermediate.cs";
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), $"test-reach-{Guid.NewGuid():N}");

        internal RepositoryFixture()
        {
            Directory.CreateDirectory(Path.Combine(Root, ".git"));
            Populate(Root);
        }

        internal static void Populate(string root)
        {
            foreach (var project in new[] { "Core", "Infrastructure", "App" })
                WriteAt(root, $"src/Mcg.AgentOrchestrator.{project}/Mcg.AgentOrchestrator.{project}.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            WriteAt(root, TestDirectory + "/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
                "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup>" + string.Concat(new[] { "Core", "Infrastructure", "App" }
                    .Select(project => $"<ProjectReference Include=\"../../src/Mcg.AgentOrchestrator.{project}/Mcg.AgentOrchestrator.{project}.csproj\" />")) +
                "</ItemGroup></Project>");
            WriteAt(root, "tests/Mcg.AgentOrchestrator.Core.Tests/Mcg.AgentOrchestrator.Core.Tests.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            WriteAt(root, Source, "public class ChangedSource { }");
            WriteAt(root, Other, "public class OtherSource { }");
        }

        internal void Write(string path, string text) => WriteAt(Root, path, text);
        internal static void WriteAt(string root, string path, string text)
        {
            var fullPath = Path.Combine(root, path);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, text);
        }
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
