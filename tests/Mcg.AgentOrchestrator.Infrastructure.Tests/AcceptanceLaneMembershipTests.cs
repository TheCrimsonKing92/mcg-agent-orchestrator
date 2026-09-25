using System.Reflection;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class AcceptanceLaneMembershipTests
{
    [Xunit.Fact]
    public void ExactClassFilterKeepsTheFullNameWithoutWildcards()
    {
        Xunit.Assert.Equal(
            ["--filter-class", "Example.Outer+Nested", "--filter-not-class", "Example.Other"],
            AcceptanceCheckCommandBuilder.TranslateMtpFilter(
                "FullyQualifiedName=Example.Outer+Nested&FullyQualifiedName!=Example.Other").ToArray());
    }

    [Xunit.Theory]
    [Xunit.InlineData("ProcessSpawning", "Process spawning")]
    [Xunit.InlineData("DotnetBuildSlots", "Dotnet build slots")]
    public void NewCollectionMemberUsesOwningLaneWithoutManifestEdit(string collection, string laneName)
    {
        var root = InfrastructureTestSupport.FindRepositoryRoot();
        var lanes = AcceptanceGateEngineSettings.Load(root).InfrastructureTestLanes;
        var scratch = Path.Combine(Path.GetTempPath(), "acceptance-lane-" + Guid.NewGuid().ToString("N"));
        var testDirectory = Path.Combine(scratch, "tests", "Mcg.AgentOrchestrator.Infrastructure.Tests");
        Directory.CreateDirectory(testDirectory);
        try
        {
            File.WriteAllText(Path.Combine(testDirectory, "QuokkaHarborSerializedFixture.cs"),
                $"[Xunit.Collection(\"{collection}\")] public class QuokkaHarborSerializedFixture {{ [Xunit.Fact] public void Example() {{ }} }}");
            var className = "QuokkaHarborSerializedFixture";
            var baseline = lanes.Select(lane => lane with { OwnedCollections = [] }).ToArray();
            Xunit.Assert.Equal("Remainder", Xunit.Assert.Single(
                AcceptanceLaneMembership.LanesIncluding(baseline, className)).Name);

            var resolved = AcceptanceLaneMembership.ResolveOwnedCollections(lanes, scratch);
            Xunit.Assert.Equal(laneName, Xunit.Assert.Single(
                AcceptanceLaneMembership.LanesIncluding(resolved, className)).Name);
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }

    [Xunit.Fact]
    public void ExistingRunnableClassesKeepTheirSubstringLaneMembership()
    {
        var root = InfrastructureTestSupport.FindRepositoryRoot();
        var lanes = AcceptanceGateEngineSettings.Load(root).InfrastructureTestLanes;
        var descriptors = RunnableClasses();
        var baseline = lanes.Select(lane => lane with { OwnedCollections = [] }).ToArray();
        var resolved = AcceptanceLaneMembership.ResolveOwnedCollections(lanes, descriptors);
        foreach (var descriptor in descriptors)
        {
            Xunit.Assert.Equal(
                AcceptanceLaneMembership.LanesIncluding(baseline, descriptor.FullName).Select(lane => lane.Name).Order(),
                AcceptanceLaneMembership.LanesIncluding(resolved, descriptor.FullName).Select(lane => lane.Name).Order());
        }
    }

    [Xunit.Fact]
    public void OwnedCollectionMemberAlsoMatchingAnotherLaneStillUsesItsOwner()
    {
        var lanes = AcceptanceGateEngineSettings.Load(InfrastructureTestSupport.FindRepositoryRoot()).InfrastructureTestLanes;
        var descriptor = new AcceptanceTestClassDescriptor("WorkerShellTestsQuokka", "ProcessSpawning");
        var baseline = lanes.Select(lane => lane with { OwnedCollections = [] }).ToArray();
        Xunit.Assert.DoesNotContain(AcceptanceLaneMembership.LanesIncluding(baseline, descriptor.FullName),
            lane => lane.Name == "Process spawning");

        var resolved = AcceptanceLaneMembership.ResolveOwnedCollections(lanes, [descriptor]);
        Xunit.Assert.Contains(AcceptanceLaneMembership.LanesIncluding(resolved, descriptor.FullName),
            lane => lane.Name == "Process spawning");
    }

    [Xunit.Fact]
    public void SourceScanAndReflectionFindTheSameOwnedCollectionMembers()
    {
        var root = InfrastructureTestSupport.FindRepositoryRoot();
        var lanes = AcceptanceGateEngineSettings.Load(root).InfrastructureTestLanes;
        var owned = lanes.SelectMany(lane => lane.OwnedCollections).ToHashSet(StringComparer.Ordinal);
        var reflected = RunnableClasses().Where(item => item.Collection is not null && owned.Contains(item.Collection))
            .OrderBy(item => item.FullName).ToArray();
        var scanned = AcceptanceTestClassSourceScanner.Scan(root)
            .Where(item => item.Collection is not null && owned.Contains(item.Collection))
            .OrderBy(item => item.FullName).ToArray();
        Xunit.Assert.Equal(reflected, scanned);
    }

    [Xunit.Fact]
    public void RemainderExcludesEveryOwnedCollectionMember()
    {
        var lanes = AcceptanceGateEngineSettings.Load(InfrastructureTestSupport.FindRepositoryRoot()).InfrastructureTestLanes;
        var descriptors = RunnableClasses();
        var resolved = AcceptanceLaneMembership.ResolveOwnedCollections(lanes, descriptors);
        var owned = lanes.SelectMany(lane => lane.OwnedCollections).ToHashSet(StringComparer.Ordinal);
        foreach (var descriptor in descriptors.Where(item => item.Collection is not null && owned.Contains(item.Collection)))
        {
            Xunit.Assert.DoesNotContain(AcceptanceLaneMembership.LanesIncluding(resolved, descriptor.FullName),
                lane => lane.Name == "Remainder");
        }
    }

    private static AcceptanceTestClassDescriptor[] RunnableClasses() =>
        typeof(AcceptanceLaneMembershipTests).Assembly.GetTypes()
            .Where(type => type is { IsClass: true, IsAbstract: false } &&
                type.GetMethods(BindingFlags.Instance | BindingFlags.Public)
                    .Any(method => method.GetCustomAttributes(inherit: true)
                        .Any(attribute => attribute is Xunit.FactAttribute)))
            .Select(type => new AcceptanceTestClassDescriptor(type.FullName ?? type.Name,
                type.GetCustomAttribute<Xunit.CollectionAttribute>(inherit: true)?.Name))
            .ToArray();
}
