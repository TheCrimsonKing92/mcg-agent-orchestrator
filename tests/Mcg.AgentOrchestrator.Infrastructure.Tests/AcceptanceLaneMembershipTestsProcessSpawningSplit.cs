using System.Reflection;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class AcceptanceLaneMembershipTestsProcessSpawningSplit
{
    private const string OriginalLaneName = "Process spawning";
    private const string NewLaneName = "Process spawning process-local";
    private const string OriginalCollection = "ProcessSpawning";
    private const string NewCollection = "ProcessSpawningProcessLocal";

    [Xunit.Fact]
    public void ProcessSpawningSplitPreservesEveryRunnableClassExactlyOnce()
    {
        var lanes = AcceptanceGateEngineSettings.Load(InfrastructureTestSupport.FindRepositoryRoot())
            .InfrastructureTestLanes;
        var newLane = Xunit.Assert.Single(lanes, lane => lane.Name == NewLaneName);
        var originalLane = Xunit.Assert.Single(lanes, lane => lane.Name == OriginalLaneName);
        var movedClassNames = new[]
        {
            typeof(ChildOutputDrainAdoptionSaturationTests).FullName!,
            typeof(PipeDrainThreadPoolSaturationTests).FullName!
        };
        var descriptors = typeof(AcceptanceLaneMembershipTestsProcessSpawningSplit).Assembly.GetTypes()
            .Where(type => type is { IsClass: true, IsAbstract: false } &&
                type.GetMethods(BindingFlags.Instance | BindingFlags.Public)
                    .Any(method => method.GetCustomAttributes(inherit: true)
                        .Any(attribute => attribute is Xunit.FactAttribute)))
            .Select(type => new AcceptanceTestClassDescriptor(type.FullName ?? type.Name,
                type.GetCustomAttribute<Xunit.CollectionAttribute>(inherit: true)?.Name))
            .ToArray();

        var resolved = AcceptanceLaneMembership.ResolveOwnedCollections(lanes, descriptors);
        var selectedByNewLane = descriptors
            .Where(descriptor => AcceptanceLaneMembership.LanesIncluding(resolved, descriptor.FullName)
                .Any(lane => lane.Name == newLane.Name))
            .Select(descriptor => descriptor.FullName)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Xunit.Assert.Equal(movedClassNames.Order(StringComparer.Ordinal), selectedByNewLane);
        Xunit.Assert.All(movedClassNames, name => Xunit.Assert.DoesNotContain(
            AcceptanceLaneMembership.LanesIncluding(resolved, name), lane => lane.Name == OriginalLaneName));

        // Reconstruct the pre-split selection from the original lane and the two known moved classes.
        var baseline = lanes.Where(lane => lane.Name != NewLaneName)
            .Select(lane => lane.Name == OriginalLaneName
                ? originalLane with { Filter = originalLane.Filter + "|" + newLane.Filter }
                : lane)
            .ToArray();
        var baselineDescriptors = descriptors.Select(descriptor => descriptor.Collection == NewCollection
            ? descriptor with { Collection = OriginalCollection }
            : descriptor).ToArray();
        var baselineResolved = AcceptanceLaneMembership.ResolveOwnedCollections(baseline, baselineDescriptors);
        var currentSelected = new HashSet<string>(StringComparer.Ordinal);
        var baselineSelected = new HashSet<string>(StringComparer.Ordinal);
        foreach (var descriptor in descriptors)
        {
            var matches = AcceptanceLaneMembership.LanesIncluding(resolved, descriptor.FullName);
            Xunit.Assert.True(matches.Count == 1,
                $"{descriptor.FullName} selected by {matches.Count} lanes: {string.Join(", ", matches.Select(lane => lane.Name))}");
            currentSelected.Add(descriptor.FullName);

            var baselineMatches = AcceptanceLaneMembership.LanesIncluding(baselineResolved, descriptor.FullName);
            Xunit.Assert.True(baselineMatches.Count == 1,
                $"Pre-split selection mapped {descriptor.FullName} to {baselineMatches.Count} lanes.");
            baselineSelected.Add(descriptor.FullName);
        }

        Xunit.Assert.Equal(baselineSelected.Order(StringComparer.Ordinal),
            currentSelected.Order(StringComparer.Ordinal));
        Xunit.Assert.Equal(descriptors.Length, currentSelected.Count);
    }
}
