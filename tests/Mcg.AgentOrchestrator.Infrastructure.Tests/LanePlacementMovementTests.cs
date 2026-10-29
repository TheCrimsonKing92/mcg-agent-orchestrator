using System.Reflection;
using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: reads checked-in configuration and mutates only in-memory lane copies.
public sealed class LanePlacementMovementTests
{
    [Xunit.Fact]
    public void Move_FromRemainder_RequiresOnlyDestinationAndExclusionEdits()
    {
        const string destination = "Goal acceptance build slots";
        string[] group = ["Goal acceptance verifier", destination];
        var lanes = IsolatedProcessSpawningLaneSplitTests.LoadLanes();
        var types = IsolatedProcessSpawningLaneSplitTests.RunnableTypes();
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            InfrastructureTestSupport.FindRepositoryRoot(), "config", "acceptance-manifest.json")));
        var partitionLaneNames = manifest.RootElement.GetProperty("engine")
            .GetProperty("localTestPartitions").EnumerateArray()
            .SelectMany(partition => partition.GetProperty("laneNames").EnumerateArray())
            .Select(name => name.GetString()!).ToArray();
        var resolved = AcceptanceLaneMembership.ResolveOwnedCollections(lanes, types.Select(type =>
            new AcceptanceTestClassDescriptor(type.FullName!,
                type.GetCustomAttribute<Xunit.CollectionAttribute>(inherit: true)?.Name)).ToArray());
        var fixture = types.FirstOrDefault(type =>
            !type.IsNested && type != typeof(LanePlacementMovementTests) &&
            type != typeof(IsolatedProcessSpawningLaneSplitTests) &&
            AcceptanceLaneMembership.LanesIncluding(resolved, type.FullName!)
                .Select(lane => lane.Name).SequenceEqual(["Remainder"]) &&
            !lanes.Any(lane => lane.OwnedCollections.Contains(
                type.GetCustomAttribute<Xunit.CollectionAttribute>(inherit: true)?.Name, StringComparer.Ordinal)) &&
            !types.Any(other => other != type &&
                other.FullName!.Contains(type.Name, StringComparison.OrdinalIgnoreCase)));
        Xunit.Assert.NotNull(fixture);
        LanePlacementAssertions.AssertGroup(lanes, group, partitionLaneNames, types);
        Xunit.Assert.Empty(IsolatedProcessSpawningLaneSplitTests.MembershipReport(
            lanes, IsolatedProcessSpawningLaneSplitTests.BuildReference(lanes), types));

        var moved = lanes.Select(lane => lane.Name == destination
            ? lane with { Filter = lane.Filter + $"|FullyQualifiedName~{fixture.Name}" }
            : lane.Name == "Remainder"
                ? lane with { Filter = lane.Filter + $"&FullyQualifiedName!~{fixture.Name}" }
                : lane).ToArray();
        Xunit.Assert.Equal(2, lanes.Zip(moved).Count(pair => pair.First != pair.Second));
        Xunit.Assert.Equal(destination, Xunit.Assert.Single(
            AcceptanceLaneMembership.LanesIncluding(moved, fixture.FullName!)).Name);
        LanePlacementAssertions.AssertGroup(moved, group, partitionLaneNames, types);
        Xunit.Assert.Empty(IsolatedProcessSpawningLaneSplitTests.MembershipReport(
            moved, IsolatedProcessSpawningLaneSplitTests.BuildReference(moved), types));

        var originalRemainder = Xunit.Assert.Single(lanes, lane => lane.Name == "Remainder");
        var missingExclusion = moved.Select(lane => lane.Name == "Remainder" ? originalRemainder : lane).ToArray();
        Xunit.Assert.Equal(1, lanes.Zip(missingExclusion).Count(pair => pair.First != pair.Second));
        var error = Xunit.Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
            LanePlacementAssertions.AssertGroup(missingExclusion, group, partitionLaneNames, types));
        Xunit.Assert.Contains(fixture.FullName!, error.Message, StringComparison.Ordinal);
        Xunit.Assert.Contains(destination, error.Message, StringComparison.Ordinal);
        Xunit.Assert.Contains("Remainder", error.Message, StringComparison.Ordinal);
    }
}
